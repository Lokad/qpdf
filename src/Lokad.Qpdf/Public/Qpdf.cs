using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf;

/// <summary>
/// Typed PDF operations over the shared engine. Each operation opens its
/// input, copies the selected graph once, serializes deterministically with a
/// counting pass, and publishes through a staged host transaction.
/// </summary>
public static class Qpdf
    // Command adapters: argv and job JSON converge on the typed selection plan.
{
    /// <summary>
    /// Copies one page into a standalone PDF with default budgets. Streams
    /// travel opaquely; inherited page attributes are materialized.
    /// </summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        CancellationToken cancellationToken)
    {
        return SelectPageAsync(host, inputPath, outputPath, options, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies one page into a standalone PDF with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page exceeds the count.</exception>
    public static async ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        if (options.PageNumber > document.PageReferences.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Page {options.PageNumber} exceeds the page count of {document.PageReferences.Count}.");
        }

        PageLocation location = document.PageReferences[options.PageNumber - 1];
        cancellationToken.ThrowIfCancellationRequested();
        GraphCopier copier = new(document.Engine, governor, limits, 3);
        Dictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken);
        (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
        RequireMediaBox(page);
        PdfDictionary catalog = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Catalog"),
            [PdfName.FromAscii("Pages")] = new PdfReference(2, 0),
        });
        PdfDictionary pages = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
            [PdfName.FromAscii("Kids")] = new PdfArray([new PdfReference(pageId, 0)]),
            [PdfName.FromAscii("Count")] = new PdfInteger("1", 1L),
        });
        List<NumberedObject> model = new()
        {
            new NumberedObject(1, catalog),
            new NumberedObject(2, pages),
        };
        List<int> ids = new(copier.CopiedObjects.Keys);
        ids.Sort();
        foreach (int id in ids)
        {
            model.Add(new NumberedObject(id, copier.CopiedObjects[id]));
        }

        PdfSerializer serializer = new(limits, governor);
        CountingSink counter = new();
        long counted = await serializer.SerializeAsync(document.Version, model, counter, cancellationToken).ConfigureAwait(false);
        if (counted > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), counted, limits.MaxOutputBytes);
        }

        governor.ReserveMemory(counted);
        governor.NoteHostOperation();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        long written = await serializer.SerializeAsync(document.Version, model, sink, cancellationToken).ConfigureAwait(false);
        if (written != counted)
        {
            throw new QpdfException($"Writer replay mismatch: counted {counted} bytes but wrote {written}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PdfPageSelectionResult(options.PageNumber, 1, [.. diagnostics.Entries], governor.Snapshot());
    }

    private static Dictionary<PdfName, QpdfObject> MaterializeAncestors(
        DocumentEngine engine,
        PageLocation location,
        GraphCopier copier,
        ResourceGovernor governor,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        string[] inheritable = ["Resources", "MediaBox", "CropBox", "Rotate"];
        Dictionary<PdfName, QpdfObject> extra = new();
        foreach (string name in inheritable)
        {
            PdfName key = PdfName.FromAscii(name);
            if (location.Page.TryGet(key, out QpdfObject _))
            {
                continue;
            }

            extra[key] = PdfNull.Instance;
        }

        if (extra.Count == 0)
        {
            return extra;
        }

        HashSet<int> visited = new();
        if (location.Source is not null)
        {
            visited.Add(location.Source.ObjectNumber);
        }

        PdfDictionary current = location.Page;
        PdfName parentKey = PdfName.FromAscii("Parent");
        int depth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!current.TryGet(parentKey, out QpdfObject parentRaw) || parentRaw is PdfNull)
            {
                break;
            }

            PdfDictionary parent;
            if (parentRaw is PdfReference parentReference)
            {
                if (!visited.Add(parentReference.ObjectNumber))
                {
                    throw new QpdfParseException($"Page ancestry cycle at object {parentReference.ObjectNumber}.", null, parentReference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(parentReference, out QpdfObject? resolved) || resolved is not PdfDictionary resolvedParent)
                {
                    throw new QpdfParseException("Unresolvable page parent.", null, parentReference.ObjectNumber);
                }

                parent = resolvedParent;
            }
            else if (parentRaw is PdfDictionary direct)
            {
                parent = direct;
            }
            else
            {
                throw new QpdfParseException("Page /Parent must be a dictionary or a reference.", null, null);
            }

            depth += 1;
            if (depth > limits.MaxNestingDepth)
            {
                throw new QpdfParseException($"Page ancestry depth exceeds {limits.MaxNestingDepth}.", null, null);
            }

            governor.ChargeWork(1L);
            foreach (KeyValuePair<PdfName, QpdfObject> pending in extra)
            {
                if (pending.Value is not PdfNull)
                {
                    continue;
                }

                if (parent.TryGet(pending.Key, out QpdfObject inherited))
                {
                    extra[pending.Key] = copier.CopyValue(inherited, 0);
                }
            }

            bool complete = true;
            foreach (KeyValuePair<PdfName, QpdfObject> pending in extra)
            {
                if (pending.Value is PdfNull)
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                break;
            }

            current = parent;
        }

        Dictionary<PdfName, QpdfObject> materialized = new();
        foreach (KeyValuePair<PdfName, QpdfObject> pending in extra)
        {
            if (pending.Value is not PdfNull)
            {
                materialized[pending.Key] = pending.Value;
            }
        }

        return materialized;
    }

    private static void RequireMediaBox(PdfDictionary page)
    {
        if (!page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject _))
        {
            throw new QpdfParseException("Selected page has no /MediaBox in its ancestry.", null, null);
        }
    }

    private sealed class TransactionSink : IByteSink
    {
        private readonly IQpdfOutputTransaction _transaction;
        private readonly ResourceGovernor _governor;
        private long _length;

        internal TransactionSink(IQpdfOutputTransaction transaction, ResourceGovernor governor)
        {
            _transaction = transaction;
            _governor = governor;
        }

        public long Length => _length;

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            _governor.ChargeOutputBytes(chunk.Length);
            await _transaction.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            checked
            {
                _length += chunk.Length;
            }
        }
    }

    /// <summary>Executes a command invocation with default budgets.</summary>
    public static ValueTask<QpdfExecutionResult> ExecuteAsync(
        IQpdfHost host,
        QpdfCommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(host, invocation, new QpdfExecutionOptions(), cancellationToken);
    }

    /// <summary>Executes a command invocation with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">Host, invocation, or options are null.</exception>
    public static async ValueTask<QpdfExecutionResult> ExecuteAsync(
        IQpdfHost host,
        QpdfCommandInvocation invocation,
        QpdfExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        SelectionPlan plan;
        try
        {
            plan = ArgvParser.Parse(invocation.Arguments);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        return await RunPlanAsync(host, plan, options.Limits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes a job-JSON invocation with default budgets.</summary>
    public static ValueTask<QpdfExecutionResult> ExecuteJobAsync(
        IQpdfHost host,
        string jobJson,
        CancellationToken cancellationToken)
    {
        return ExecuteJobAsync(host, jobJson, new QpdfExecutionOptions(), cancellationToken);
    }

    /// <summary>Executes a job-JSON invocation with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">Host, JSON, or options are null.</exception>
    public static async ValueTask<QpdfExecutionResult> ExecuteJobAsync(
        IQpdfHost host,
        string jobJson,
        QpdfExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (jobJson is null)
        {

            throw new ArgumentNullException(nameof(jobJson));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        SelectionPlan plan;
        try
        {
            plan = JobJsonParser.Parse(jobJson, options.Limits);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        return await RunPlanAsync(host, plan, options.Limits, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<QpdfExecutionResult> RunPlanAsync(
        IQpdfHost host,
        SelectionPlan plan,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            PdfPageSelectionResult selected = await SelectPageAsync(
                host,
                plan.Input,
                plan.Output,
                new PdfPageSelectionOptions(plan.Page),
                limits,
                cancellationToken).ConfigureAwait(false);
            return QpdfExecutionResult.Completed(selected.Warnings, selected.Counters);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], ZeroCounters());
        }
    }

    private static QpdfExecutionResult CryptoFailed(string option)
    {
        QpdfDiagnostic diagnostic = new(
            QpdfDiagnosticCode.UnsupportedCryptoOperation,
            QpdfDiagnosticSeverity.Error,
            $"Cryptographic option '{option}' is permanently out of scope.");
        return QpdfExecutionResult.Failed([diagnostic], ZeroCounters());
    }

    private static QpdfExecutionResult ToFailedResult(QpdfException ex)
    {
        if (ex is QpdfCryptoException crypto)
        {
            return CryptoFailed(crypto.Option);
        }

        if (ex is QpdfEncryptedInputException)
        {
            return Failed(QpdfDiagnosticCode.UnsupportedCryptoOperation, ex.Message);
        }

        if (ex is QpdfLimitException)
        {
            return Failed(QpdfDiagnosticCode.BudgetExceeded, ex.Message);
        }

        if (ex is QpdfFilterException)
        {
            return Failed(QpdfDiagnosticCode.UnsupportedFeature, ex.Message);
        }

        if (ex is QpdfParseException)
        {
            return Failed(QpdfDiagnosticCode.InvalidArguments, ex.Message);
        }

        return Failed(QpdfDiagnosticCode.HostOperationFailed, ex.Message);
    }

    private static QpdfExecutionResult Failed(QpdfDiagnosticCode code, string message)
    {
        QpdfDiagnostic diagnostic = new(code, QpdfDiagnosticSeverity.Error, message);
        return QpdfExecutionResult.Failed([diagnostic], ZeroCounters());
    }

    private static QpdfExecutionCounters ZeroCounters()
    {
        return new QpdfExecutionCounters(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
    }
}
