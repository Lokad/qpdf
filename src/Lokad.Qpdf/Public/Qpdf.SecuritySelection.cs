using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    /// <summary>Copies selected pages using an input password and preserves supported input encryption.</summary>
    /// <remarks>A null password attempts the empty password. Either matching user or owner password
    /// preserves both passwords, permissions and metadata policy in the derived PDF.</remarks>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, QpdfPassword? password, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, password, QpdfLimits.Default, cancellationToken);

    /// <summary>Copies selected pages with explicit budgets, authenticating and preserving supported input encryption.</summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, QpdfPassword? password, QpdfLimits limits, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, Array.Empty<PdfPageRotation>(), password, limits, cancellationToken);

    /// <summary>Copies and rotates selected pages, authenticating and preserving supported input encryption.</summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, IReadOnlyList<PdfPageRotation> rotations,
        QpdfPassword? password, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, rotations, password, QpdfLimits.Default, cancellationToken);

    /// <summary>Copies and rotates selected pages with explicit budgets and preserves supported input encryption.</summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, IReadOnlyList<PdfPageRotation> rotations,
        QpdfPassword? password, QpdfLimits limits, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, rotations, password, QpdfOutputEncryption.Preserve, limits, cancellationToken);

    /// <summary>Copies selected pages with an input credential and explicit output encryption policy.</summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, password, outputEncryption, QpdfLimits.Default, cancellationToken);

    /// <summary>Copies selected pages with explicit budgets, input credential and output encryption policy.</summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, Array.Empty<PdfPageRotation>(), password, outputEncryption, limits, cancellationToken);

    /// <summary>Copies and rotates selected pages with an explicit output encryption policy.</summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, IReadOnlyList<PdfPageRotation> rotations,
        QpdfPassword? password, QpdfOutputEncryption outputEncryption, CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, rotations, password, outputEncryption, QpdfLimits.Default, cancellationToken);

    /// <summary>Copies and rotates selected pages with explicit budgets and output encryption policy.</summary>
    public static async ValueTask<PdfPageSelectionResult> SelectPageAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, PdfPageSelectionOptions options, IReadOnlyList<PdfPageRotation> rotations,
        QpdfPassword? password, QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rotations); ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(outputEncryption);
        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation? preparation = rotations.Count == 0 ? null : TypedSelectionPreparation.Create(governor);
            preparation?.ConvertRotations(rotations);
            DiagnosticBag diagnostics = new(governor);
            PageSelectionOutcome result = await SelectPageCoreAsync(host, inputPath, outputPath, options, limits,
                QpdfRecoveryMode.Strict, SelectionSettings.Typed with
                { Rotations = preparation?.Rotations ?? Array.Empty<OutputRotation>(), AuthenticatePrimary = true,
                    PrimaryPassword = password, OutputEncryption = outputEncryption },
                governor, diagnostics, cancellationToken).ConfigureAwait(false);
            preparation?.Dispose();
            PdfPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters(); return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    private static async ValueTask<(OutputSecurity? Security, byte[]? Identifier)> PrepareOutputSecurityAsync(
        IQpdfHost host, QpdfOutputEncryption policy, QpdfDocument? document, byte[]? identifier,
        ParserMemoryScope memory, ResourceGovernor governor, QpdfLimits limits, DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery, CancellationToken cancellationToken)
    {
        if (policy.TemplateInput is QpdfPath input)
        {
            OutputSecurity? prepared = null;
            try
            {
                await using (QpdfDocument template = await QpdfDocument.OpenInspectionCore(host, input, policy.TemplatePassword,
                    limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false))
                {
                    if (template.Engine.Security is not null)
                    {
                        memory.Reserve(288L); // Bounded independent first-identifier storage.
                        identifier = TryGetOriginalId(template.Engine);
                        prepared = PreparePreservedSecurity(template, ref identifier, memory, governor);
                    }
                }
                return (prepared, identifier);
            }
            catch { prepared?.Dispose(); throw; }
        }
        if (!policy.PreservesInput) { return (policy.CreateOutput(governor), identifier); }
        OutputSecurity? preserved = document is null ? null : PreparePreservedSecurity(document, ref identifier, memory, governor);
        return (preserved, identifier);
    }

    private static OutputSecurity? PreparePreservedSecurity(QpdfDocument document, ref byte[]? identifier,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (document.Engine.Security is not DocumentSecurity security) { return null; }
        if (identifier is null)
        {
            if (security.Revision == 4)
            { throw new QpdfUnsupportedFeatureException("Preserving AES-128 requires a first document identifier of 1..256 bytes."); }
            memory.Reserve(40L);
            identifier = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        }
        return security.PrepareOutput(identifier);
    }

    // Multi-source copying closes each reader before publication. This owner
    // retains only the independently prepared output key and dictionary.
    private sealed class SelectionSecurity : IDisposable
    {
        private ParserMemoryScope? _memory;
        internal OutputSecurity? Value { get; private set; }
        private SelectionSecurity(ParserMemoryScope memory) { _memory = memory; }

        internal static SelectionSecurity Create(ResourceGovernor governor)
        {
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            try { memory.Reserve(32L); return new(memory); }
            catch { memory.Dispose(); throw; }
        }

        internal async ValueTask<byte[]?> PrepareAsync(IQpdfHost host, QpdfOutputEncryption policy, QpdfDocument? document,
            byte[]? identifier, ParserMemoryScope memory, ResourceGovernor governor, QpdfLimits limits,
            DiagnosticBag diagnostics, QpdfRecoveryMode recovery, CancellationToken cancellationToken)
        {
            var prepared = await PrepareOutputSecurityAsync(host, policy, document, identifier, memory, governor,
                limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            Value = prepared.Security;
            return prepared.Identifier;
        }

        public void Dispose()
        {
            Value?.Dispose(); Value = null;
            ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
        }
    }

    private static void RequireEncryptionVersion(OutputSecurity? security, ref PdfVersion version, ref int extension,
        string? forcedVersion, ResourceGovernor governor)
    {
        if (security is null) { return; }
        int minor = security.Revision >= 5 ? 7 : 6;
        int requiredExtension = security.Revision == 6 ? 8 : security.Revision == 5 ? 3 : 0;
        int order = PdfVersion.CompareParts(version.Major, version.Minor, 1, minor);
        if (forcedVersion is not null && (order < 0 || order == 0 && extension < requiredExtension))
        { throw new QpdfUnsupportedFeatureException("The forced PDF version cannot support the requested output encryption."); }
        IncludeVersionRequirement(ref version, ref extension, new QpdfDocument.VersionParts(1, minor), requiredExtension, governor);
    }

    private static int AddEncryptionObject(List<NumberedObject> model, OutputSecurity? security,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (security is null) { return 0; }
        int number = 0;
        foreach (NumberedObject item in model)
        { governor.CheckCancellation(); number = Math.Max(number, item.Number); }
        number = checked(number + 1);
        ParserContainers.AddItem(memory, model, new NumberedObject(number, security.Dictionary), 16L);
        return number;
    }

    private static void ValidateSourceCredentials(IReadOnlyList<PdfPageSource> sources,
        Dictionary<string, List<int>> groups, ResourceGovernor governor)
    {
        foreach (List<int> indices in groups.Values)
        {
            ValidateSourceCredentials(sources, indices, governor);
        }
    }

    private static void ValidateSourceCredentials(IReadOnlyList<PdfPageSource> sources, List<int> indices, ResourceGovernor governor)
    {
        QpdfPassword? first = sources[indices[0]].Password;
        foreach (int index in indices)
        {
            governor.CheckCancellation();
            QpdfPassword? password = sources[index].Password;
            if (ReferenceEquals(first, password)) { continue; }
            if (first is null || password is null || !first.HasSameInput(password, governor))
            { throw new ArgumentException("Repeated page sources must use the same credential value and representation.", nameof(sources)); }
        }
    }

    private static PdfSerializer CreatePublicationSerializer(QpdfLimits limits, ResourceGovernor governor,
        List<NumberedObject> model, OutputSecurity? security, int encryptionObject, int metadataObject,
        ParserMemoryScope? memory)
    {
        if (security is null) { return new(limits, governor); }
        ParserMemoryScope owner = memory ?? throw new InvalidOperationException("Missing publication owner.");
        Dictionary<int, QpdfObject> values = ParserContainers.CreateDictionary<int, QpdfObject>(owner);
        foreach (NumberedObject item in model)
        {
            governor.CheckCancellation(); governor.NoteOperation();
            ParserContainers.EnsureEntryCapacity(owner, values, IntPtr.Size);
            values.Add(item.Number, item.Value);
        }
        return new(limits, governor, security, encryptionObject, metadataObject, Content.PoolResolver.Create(owner, values));
    }
}
