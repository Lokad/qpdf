using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using System.Security.Cryptography;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputPublication;

namespace Lokad.Qpdf.Writing;

internal static class OutputSecurityPreparation
{
    internal static byte[]? TryGetOriginalId(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        PdfDictionary trailer = engine.Trailer;
        PdfName idKey = PdfName.FromAscii("ID");
        if (!trailer.TryGet(idKey, out QpdfObject raw) || raw is not PdfArray array || array.Count == 0)
        {
            return null;
        }

        if (array[0] is not PdfByteString first || first.ValueMemory.Length == 0 || first.ValueMemory.Length > 256)
        {
            return null;
        }

        return first.Value;
    }

    internal static async ValueTask<(OutputSecurity? Security, byte[]? Identifier)> PrepareOutputSecurityAsync(
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

    internal static OutputSecurity? PreparePreservedSecurity(QpdfDocument document, ref byte[]? identifier,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (document.Engine.Security is not DocumentSecurity security) { return null; }
        if (identifier is null)
        {
            if (security.Revision <= 4)
            { throw new QpdfUnsupportedFeatureException("Preserving legacy encryption requires a first document identifier of 1..256 bytes."); }
            memory.Reserve(40L);
            identifier = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        }
        return security.PrepareOutput(identifier);
    }

    // Multi-source copying closes each reader before publication. This owner
    // retains only the independently prepared output key and dictionary.
    internal sealed class SelectionSecurity : IDisposable
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

}
