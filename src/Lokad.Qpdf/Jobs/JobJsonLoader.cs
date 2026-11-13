using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Jobs;

/// <summary>Host-mediated, iterative partial-job loading into the shared parser.</summary>
internal static class JobJsonLoader
{
    internal const string FileKey = "jobJsonFile";

    internal static async ValueTask<LoadedJob> ParseAsync(
        IQpdfHost host, string json, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        Session session = Session.Create(host, limits, governor);
        try
        {
            session.AddText(json);
            await session.LoadChildrenAsync(0, cancellationToken).ConfigureAwait(false);
            session.AppendChainFields(0);
            return new LoadedJob(session.ParsePlan(), session);
        }
        catch { session.Dispose(); throw; }
    }

    internal static async ValueTask<LoadedJob> ParseArgvAsync(
        IQpdfHost host, IReadOnlyList<string> argv, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        Session session = Session.Create(host, limits, governor);
        try
        {
            session.AdmitArguments(argv);
            await ArgvConfigurationReader.ReadAsync(argv, session.AddSetting,
                name => session.LoadFileChainAsync(name, cancellationToken), governor, session.TextStorage).ConfigureAwait(false);
            return new LoadedJob(session.ParsePlan(), session);
        }
        catch { session.Dispose(); throw; }
    }

    internal sealed class LoadedJob(JobPlan plan, IDisposable owner) : IDisposable
    {
        private JobPlan? _plan = plan;
        private IDisposable? _owner = owner;
        internal JobPlan Plan => _plan ?? throw new ObjectDisposedException(nameof(LoadedJob));

        public void Dispose()
        {
            _plan?.CollationStorage?.Dispose();
            _plan = null;
            IDisposable? retained = _owner;
            _owner = null;
            retained?.Dispose();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly IQpdfHost _host;
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private List<JsonDocument>? _documents;
        private List<JobSetting>? _fields;
        private HashSet<string>? _identities;
        private long _bytes;
        private long _reserved;
        private ConfigurationText? _textStorage;

        private Session(IQpdfHost host, QpdfLimits limits, ResourceGovernor governor)
        {
            _host = host;
            _limits = limits;
            _governor = governor;
            _reserved = 512L;
            _documents = new List<JsonDocument>();
            _fields = new List<JobSetting>();
            _identities = new HashSet<string>(StringComparer.Ordinal);
            _textStorage = ConfigurationText.Create(governor);
        }

        internal static Session Create(IQpdfHost host, QpdfLimits limits, ResourceGovernor governor)
        {
            governor.CheckCancellation();
            governor.ReserveMemory(512L);
            try
            {
                return new Session(host, limits, governor);
            }
            catch
            {
                governor.ReleaseMemory(512L);
                throw;
            }
        }

        private List<JsonDocument> Documents => _documents ?? throw new ObjectDisposedException(nameof(Session));
        internal ConfigurationText TextStorage => _textStorage ?? throw new ObjectDisposedException(nameof(Session));

        internal void AdmitArguments(IReadOnlyList<string> argv)
        {
            long bytes = 0;
            foreach (string token in argv)
            {
                _governor.CheckCancellation();
                if (!GovernedText.IsValidUtf16(token, _governor))
                { throw new QpdfParseException("Job arguments contain malformed UTF-16.", null, null); }
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(token));
                if (bytes > Math.Min(_limits.MaxInputBytes, int.MaxValue))
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), bytes, Math.Min(_limits.MaxInputBytes, int.MaxValue));
                }
            }
            // Coarse storage for the syntax adapter, direct settings and semantic
            // collections. Borrowed argv strings need no encoded/DOM copy pool;
            // slices use ConfigurationText's separate admitted owner.
            ReserveDocument(checked(argv.Count * 512L + 4096L));
            _bytes = CheckedDocumentTotal(bytes);
        }

        internal void AddSetting(string name, object value)
        {
            _governor.CheckCancellation();
            List<JobSetting> fields = _fields ?? throw new ObjectDisposedException(nameof(Session));
            fields.Add(new(name, new(value)));
        }

        internal void AddText(string text)
        {
            _governor.CheckCancellation();
            if (text.Length > _limits.MaxInputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), text.Length, _limits.MaxInputBytes);
            }
            _governor.NoteOperation();
            long bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes < JobJsonMemory.DetailedThreshold) { AdmitDocument(bytes); }
            else
            {
                long total = CheckedDocumentTotal(bytes);
                var estimate = JobJsonMemory.Estimate(text.AsSpan(), bytes, _governor);
                AdmitDetailedDocument(estimate.Document, estimate.Copies);
                _bytes = total;
                _governor.NoteOperation();
            }
            AddDocument(() =>
            {
                try { return JsonDocument.Parse(text, Options()); }
                catch (ArgumentException ex)
                { throw new QpdfParseException("Invalid string encoding in job JSON.", 0L, null, ex); }
            });
        }

        internal async ValueTask AddFileAsync(QpdfPath path, int depth, CancellationToken cancellationToken)
        {
            _governor.CheckCancellation();
            if (depth >= _limits.MaxNestingDepth)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth + 1L, _limits.MaxNestingDepth);
            }
            await using IQpdfInput input = await GovernedHost.OpenInputAsync(_host, path, _governor, cancellationToken).ConfigureAwait(false);
            HashSet<string> identities = _identities ?? throw new ObjectDisposedException(nameof(Session));
            if (identities.Contains(input.Identity))
            {
                throw new QpdfParseException("Nested job JSON contains an input identity cycle.", 0L, null);
            }
            if (input.Length < 0L)
            {
                throw new QpdfParseException("Job JSON input has an invalid length.", 0L, null);
            }
            bool detailed = input.Length >= JobJsonMemory.DetailedThreshold;
            if (!detailed) { AdmitDocument(input.Length); }
            else
            {
                long total = CheckedDocumentTotal(input.Length);
                ReserveDocument(checked(input.Length + GovernedBufferStream.ArrayOverheadBytes));
                _bytes = total;
                _governor.NoteOperation();
            }
            identities.Add(input.Identity);
            byte[] bytes = new byte[(int)input.Length];
            for (int offset = 0; offset < bytes.Length;)
            {
                _governor.CheckCancellation();
                int count = Math.Min(65536, bytes.Length - offset);
                await QpdfInputReader.ReadExactlyAsync(input, offset, bytes.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                _governor.CheckCancellation();
                offset += count;
            }
            if (detailed)
            {
                try
                {
                    var estimate = JobJsonMemory.Estimate(bytes, _governor);
                    AdmitDetailedDocument(estimate.Document, estimate.Copies);
                }
                catch (JsonException ex) { throw new QpdfParseException("Invalid job JSON.", ex.BytePositionInLine, null, ex); }
            }
            AddDocument(() => JsonDocument.Parse(bytes, Options()));
        }

        private static JsonDocumentOptions Options() => new()
        {
            MaxDepth = JobJsonMemory.MaxDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };

        private long CheckedDocumentTotal(long bytes)
        {
            long total = checked(_bytes + bytes);
            if (total > _limits.MaxInputBytes || bytes > int.MaxValue)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), total, Math.Min(_limits.MaxInputBytes, int.MaxValue));
            }
            return total;
        }

        private void ReserveDocument(long bytes)
        {
            _governor.ReserveMemory(bytes);
            _reserved = checked(_reserved + bytes);
        }

        private void AdmitDetailedDocument(long document, long copies)
        {
            ReserveDocument(document);
            TextStorage.AdmitJsonCopies(copies);
        }

        private void AdmitDocument(long bytes)
        {
            long total = CheckedDocumentTotal(bytes);
            // Covers encoded storage, DOM/token tables, names and property sorting
            // before allocation. This coarse estimate does not bound BCL pool retention.
            long reservation = checked(bytes * 32L + 4096L);
            long copies = ConfigurationText.JsonCopyBytes(bytes);
            ReserveDocument(reservation - copies);
            TextStorage.AdmitJsonCopies(copies);
            _bytes = total;
            _governor.NoteOperation();
        }

        private void AddDocument(Func<JsonDocument> parse)
        {
            JsonDocument document;
            try { document = parse(); }
            catch (JsonException ex) { throw new QpdfParseException("Invalid job JSON.", ex.BytePositionInLine, null, ex); }
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new QpdfParseException("Job JSON must be an object.", 0L, null);
                }
                Validate(document.RootElement, true, null);
                Documents.Add(document);
            }
            catch (InvalidOperationException ex)
            {
                document.Dispose();
                throw new QpdfParseException("Invalid string encoding in job JSON.", 0L, null, ex);
            }
            catch { document.Dispose(); throw; }
        }

        private void Validate(JsonElement value, bool root, string? context)
        {
            _governor.CheckCancellation();
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    _governor.NoteOperation();
                    if ((root && JobPlanner.IsCryptoKey(property.Name) && property.Name is not ("password" or "passwordFile" or "passwordMode" or "suppressPasswordRecovery" or "decrypt" or "encrypt" or "copyEncryption" or "encryptionFilePassword" or "removeRestrictions"))
                        || (!root && property.Name == "password" && context is not ("pages" or "copyAttachmentsFrom" or "overlay" or "underlay")))
                    {
                        throw new QpdfCryptoException(property.Name);
                    }
                    Validate(property.Value, false, root ? property.Name : null);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray()) { Validate(item, false, context); }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                _ = value.GetString();
            }
        }

        internal async ValueTask LoadFileChainAsync(string name, CancellationToken cancellationToken)
        {
            HashSet<string> identities = _identities ?? throw new ObjectDisposedException(nameof(Session));
            identities.Clear();
            int first = Documents.Count;
            await AddFileAsync(SelectionPlan.CheckedPath(name), 0, cancellationToken).ConfigureAwait(false);
            await LoadChildrenAsync(first, cancellationToken).ConfigureAwait(false);
            AppendChainFields(first);
            identities.Clear();
        }

        internal async ValueTask LoadChildrenAsync(int first, CancellationToken cancellationToken)
        {
            while (Documents[^1].RootElement.TryGetProperty(FileKey, out JsonElement child))
            {
                _governor.CheckCancellation();
                if (child.ValueKind != JsonValueKind.String)
                {
                    throw new QpdfParseException("Job jobJsonFile must be a nonempty string.", 0L, null);
                }
                string name = TextStorage.ReadJsonString(child) ?? throw new QpdfParseException("Job jobJsonFile must name a file.", 0L, null);
                if (name.Length == 0) { throw new QpdfParseException("Job jobJsonFile must be a nonempty string.", 0L, null); }
                await AddFileAsync(SelectionPlan.CheckedPath(name), Documents.Count - first, cancellationToken).ConfigureAwait(false);
            }
        }

        internal JobPlan ParsePlan()
        {
            ConfigurationText textStorage = TextStorage;
            JobPlan plan = JobPlanner.ParseProperties(_fields ?? throw new ObjectDisposedException(nameof(Session)),
                _governor, textStorage);
            textStorage.CompleteJsonCopies();
            // LoadedJob owns this session through plan consumption. Its copied
            // text follows that boundary, alongside the retained JSON documents.
            return plan;
        }

        internal void AppendChainFields(int first)
        {
            List<JobSetting> fields = _fields ?? throw new ObjectDisposedException(nameof(Session));
            // Each document has one include: forward prefixes, then reverse suffixes
            // reproduce native sorted-key traversal without recursive loading.
            for (int index = first; index < Documents.Count; index++) { AddFields(Documents[index], true, fields); }
            for (int index = Documents.Count - 1; index >= first; index--) { AddFields(Documents[index], false, fields); }
        }

        private void AddFields(JsonDocument document, bool prefix, List<JobSetting> fields)
        {
            List<JobSetting> sorted = new(JobPlanner.EffectiveProperties(new JobValue(document.RootElement)));
            int kept = 0;
            for (int index = 0; index < sorted.Count; index++)
            {
                _governor.NoteOperation();
                JobSetting property = sorted[index];
                int order = string.CompareOrdinal(property.Name, FileKey);
                if (order != 0 && (prefix ? order < 0 : order > 0)) { sorted[kept++] = property; }
            }
            sorted.RemoveRange(kept, sorted.Count - kept);
            try
            {
                sorted.Sort((left, right) =>
                {
                    _governor.CheckCancellation();
                    return string.CompareOrdinal(left.Name, right.Name);
                });
            }
            catch (InvalidOperationException ex) when (ex.InnerException is QpdfException or OperationCanceledException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
            }
            foreach (JobSetting property in sorted)
            {
                _governor.NoteOperation();
                fields.Add(property);
            }
        }

        public void Dispose()
        {
            if (_documents is not null)
            {
                foreach (JsonDocument document in _documents) { document.Dispose(); }
            }
            _documents = null;
            _fields = null;
            _identities = null;
            ConfigurationText? textStorage = _textStorage;
            _textStorage = null;
            textStorage?.Dispose();
            _governor.ReleaseMemory(_reserved);
            _reserved = 0L;
        }
    }
}
