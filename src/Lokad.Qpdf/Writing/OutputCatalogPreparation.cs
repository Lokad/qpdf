using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal readonly record struct CatalogContents
{
    internal QpdfObject? PageLabels { get; init; }
    internal PdfReference? Metadata { get; init; }
    internal PdfReference? Names { get; init; }
    internal QpdfObject? PageMode { get; init; }
    internal PdfReference? Outlines { get; init; }
    internal PdfDictionary? AcroForm { get; init; }
    internal PdfReference? Structure { get; init; }
    internal QpdfObject? MarkInfo { get; init; }
    internal IReadOnlyDictionary<PdfName, QpdfObject>? Extras { get; init; }
    internal QpdfObject? OptionalContent { get; init; }
}

internal static class OutputCatalogPreparation
{
    internal static readonly PdfName UseAttachmentsPageMode = PdfName.FromAscii("UseAttachments");

    internal static PdfDictionary PagesNode(IReadOnlyList<PdfReference> groupKids,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        // Fixed bounded names, three-entry table and count token; child backing
        // is admitted exactly and adopted without a second array copy.
        memory.Reserve(checked(1024L + IntPtr.Size * (long)groupKids.Count));
        QpdfObject[] children = new QpdfObject[groupKids.Count];
        for (int index = 0; index < groupKids.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); children[index] = groupKids[index];
        }
        string text = groupKids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return PdfDictionary.FromOwnedEntries(new Dictionary<PdfName, QpdfObject>(3)
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
            [PdfName.FromAscii("Kids")] = PdfArray.FromOwnedItems(children),
            [PdfName.FromAscii("Count")] = new PdfInteger(text, groupKids.Count),
        });
    }

    internal static PdfDictionary NewCatalog(CatalogContents contents, PdfVersion finalVersion, int extensionLevel,
        IReadOnlyDictionary<int, QpdfObject> pool, ResourceGovernor governor, CancellationToken token)
    {
        if (finalVersion is null)
        {
            throw new ArgumentNullException(nameof(finalVersion));
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        void Set(ReadOnlySpan<byte> key, QpdfObject value)
            => entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, key), value, token);
        Set("Type"u8, PdfDictionaryRebuild.CreateName(graph, "Catalog"u8));
        graph.Memory.Reserve(24L); graph.Add(24L);
        Set("Pages"u8, new PdfReference(2, 0));
        if (contents.PageLabels is not null)
        {
            Set("PageLabels"u8, contents.PageLabels);
        }

        if (contents.Metadata is not null)
        {
            Set("Metadata"u8, contents.Metadata);
        }

        if (contents.Names is not null)
        {
            Set("Names"u8, contents.Names);
        }

        if (contents.PageMode is not null)
        {
            Set("PageMode"u8, contents.PageMode);
        }

        if (contents.Outlines is not null)
        {
            Set("Outlines"u8, contents.Outlines);
        }

        if (contents.AcroForm is not null)
        {
            Set("AcroForm"u8, contents.AcroForm);
        }

        if (contents.Structure is not null)
        {
            Set("StructTreeRoot"u8, contents.Structure);
        }

        if (contents.MarkInfo is not null && contents.MarkInfo is not PdfNull)
        {
            Set("MarkInfo"u8, contents.MarkInfo);
        }

        QpdfObject? originalExtensions = null;
        if (contents.Extras is not null)
        {
            foreach (KeyValuePair<PdfName, QpdfObject> extra in contents.Extras)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                // Imported fields replace an opaque primary form, as they do
                // in the reference; the preserved array must not overwrite it.
                if (contents.AcroForm is not null && extra.Key.RawSpan.SequenceEqual("AcroForm"u8)) { continue; }
                if (extra.Key.RawSpan.SequenceEqual("Extensions"u8)) { originalExtensions = extra.Value; continue; }
                if (contents.Names is not null && extra.Key.RawSpan.SequenceEqual("Names"u8))
                {
                    PdfDictionary names = extra.Value as PdfDictionary ?? throw new InvalidOperationException("Copied name categories must be a dictionary.");
                    PdfDictionary attachments = pool[contents.Names.ObjectNumber] as PdfDictionary ?? throw new InvalidOperationException("Attachment names must be a dictionary.");
                    if (!PdfDictionaryLookup.TryGet(attachments, "EmbeddedFiles"u8, governor, token, out QpdfObject files))
                    { throw new InvalidOperationException("Attachment names must contain EmbeddedFiles."); }
                    Set("Names"u8, PdfDictionaryRebuild.ReplaceEntry(graph, names, "EmbeddedFiles"u8, files));
                    continue;
                }
                entries.Assign(graph.Memory, governor, extra.Key, extra.Value, token);
            }
        }

        if (contents.OptionalContent is not null) { Set("OCProperties"u8, contents.OptionalContent); }
        if (originalExtensions is not null || extensionLevel > 0)
        {
            IIndirectResolver resolver = Content.PoolResolver.Create(graph.Memory, pool);
            QpdfObject? extensions = PrepareExtensions(originalExtensions, resolver, finalVersion, extensionLevel, graph);
            if (extensions is not null) { Set("Extensions"u8, extensions); }
        }

        PdfDictionary catalog = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Retain(); return catalog;
    }

    internal static QpdfObject? PrepareExtensions(QpdfObject? original, IIndirectResolver resolver, PdfVersion version, int extensionLevel, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        PdfDictionary? extensions = Content.CatalogCopy.ResolveDictionary(original, resolver, governor, token);
        QpdfObject raw = PdfNull.Instance;
        bool hasAdobe = extensions is not null && PdfDictionaryLookup.TryGet(extensions, "ADBE"u8, governor, token, out raw);
        if (extensionLevel == 0)
        {
            if (!hasAdobe) { return original; }
            PdfDictionary remaining = PdfDictionaryRebuild.RemoveEntry(graph, extensions ?? throw new InvalidOperationException(), "ADBE"u8);
            return remaining.Count == 0 ? null : remaining;
        }

        // Keep an unchanged declaration, including private fields and aliases.
        PdfDictionary? adbe = Content.CatalogCopy.ResolveDictionary(raw, resolver, governor, token);
        if (adbe is not null
            && PdfDictionaryLookup.TryGet(adbe, "BaseVersion"u8, governor, token, out QpdfObject baseRaw)
            && Content.CatalogCopy.ResolveValue(baseRaw, resolver, governor, token) is PdfName baseName
            && baseName.RawSpan.Length == 3 && baseName.RawSpan[0] == (byte)('0' + version.Major)
            && baseName.RawSpan[1] == (byte)'.' && baseName.RawSpan[2] == (byte)('0' + version.Minor)
            && PdfDictionaryLookup.TryGet(adbe, "ExtensionLevel"u8, governor, token, out QpdfObject levelRaw)
            && Content.CatalogCopy.ResolveValue(levelRaw, resolver, governor, token) is PdfInteger oldLevel && oldLevel.Value == extensionLevel)
        { return original; }

        graph.Memory.Reserve(512L);
        string baseVersion = version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + version.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string levelText = extensionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PdfName name = PdfName.FromOwnedBytes(System.Text.Encoding.ASCII.GetBytes(baseVersion));
        PdfInteger level = new(levelText, extensionLevel);
        graph.Add(checked(96L + baseVersion.Length + GovernedText.StringBytes(levelText.Length)));
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "BaseVersion"u8), name, token);
        entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "ExtensionLevel"u8), level, token);
        PdfDictionary replacement = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        if (extensions is not null) { return PdfDictionaryRebuild.ReplaceEntry(graph, extensions, "ADBE"u8, replacement); }
        entries = PdfDictionaryRebuild.CreateEntries(graph, out allocation);
        entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "ADBE"u8), replacement, token);
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

}
