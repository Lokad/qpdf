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

    private static OutputSecurity? PrepareOutputSecurity(QpdfOutputEncryption policy, QpdfDocument document, ref byte[]? identifier,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (!policy.PreservesInput) { return policy.CreateOutput(governor); }
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
