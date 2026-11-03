using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlinePacketBoundaryTests
{
    private sealed class FailureHost(InMemoryQpdfHost inner, IMemoryBudget budget, string phase, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await inner.CreateOutputAsync(path, token), phase, cancellation);
        private sealed class Transaction(IQpdfOutputTransaction inner, string phase, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            {
                if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (phase == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(data, token);
            }
            public ValueTask CommitAsync(CancellationToken token) => phase == "commit"
                ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static byte[] Samples()
    {
        byte[] samples = Enumerable.Repeat((byte)64, 64).ToArray();
        "A EI q Q q Q q Q q Q q Q /Unused 9 Tf (raw\rline) Tj "u8.CopyTo(samples); return samples;
    }

    private static byte[] StoredFlate(byte[] bytes)
    {
        using MemoryStream packet = new();
        using (ZLibStream encoder = new(packet, CompressionLevel.NoCompression, true)) { encoder.Write(bytes); }
        return packet.ToArray();
    }

    private static (byte[] Packet, string Filter) Packet(string filter)
    {
        byte[] samples = Samples();
        return filter switch
        {
            "raw" => (samples, "[]"),
            "FlateDecode" => (StoredFlate(samples), "/Fl"),
            "DCTDecode" => (PacketFramingTests.WithJpegComment(DctDecodeTests.EncodeGrayJpeg(8, 8), samples), "/DCT"),
            "chain" => (DecoderOwnershipFixtures.Encode("ASCIIHexDecode", StoredFlate(samples)), "[/AHx /Fl]"),
            _ => (DecoderOwnershipFixtures.Encode(filter, samples), "/" + filter),
        };
    }

    private static byte[] Image(byte[] packet, string filter)
        => [.. Encoding.ASCII.GetBytes("BI /W 8 /H 8 /BPC 8 /CS /G /F " + filter + " ID "), .. packet, .. " EI"u8];

    private static byte[] Fixture(byte[] program, bool form)
    {
        const string resources = "<< /Font << /Used 6 0 R /Unused 7 0 R >> >>";
        byte[] page = form ? "/Fm Do"u8.ToArray() : program;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Original " + (form ? 5 : 4) + " 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " + (form ? "<< /XObject << /Fm 5 0 R >> >>" : resources) + " /Contents 4 0 R >>", null),
            (4, "<< /Length " + page.Length + " >>", page),
            (5, form ? "<< /Subtype /Form /BBox [0 0 100 100] /Resources " + resources + " /Length " + program.Length + " >>" : "null", form ? program : null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null)], 1);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string filter in new[] { "raw", "FlateDecode", "RunLengthDecode", "DCTDecode", "ASCIIHexDecode", "ASCII85Decode", "LZWDecode", "chain" })
        foreach (string mode in new[] { "external-page", "external-form", "normalize", "qdf", "scan" })
        foreach (bool job in new[] { false, true }) { yield return [filter, mode, job]; }
    }

    public static IEnumerable<object[]> UnknownCases()
    {
        foreach (string mode in new[] { "external-page", "external-form", "normalize", "qdf", "scan", "check" })
        foreach (bool job in new[] { false, true }) { yield return [mode, job]; }
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (string phase in new[] { "write", "commit", "cancel", "local", "shared" })
        foreach (bool external in new[] { false, true }) { yield return [phase, external]; }
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task PacketConstructionAndPublicationFailuresKeepPriorOutputAndRefundOwners(string phase, bool external)
    {
        (byte[] packet, string filter) = Packet("FlateDecode");
        byte[] program = [.. Image(Samples(), "[]"), .. "\n"u8, .. Image(packet, filter), .. "\n"u8];
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture(program, false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(phase == "shared" ? 8192 : 16777216); using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            List<string> argv = ["input", external ? "--externalize-inline-images" : "--normalize-content=y", "--ii-min-bytes=0", "--compress-streams=n", "--decode-level=none", "output"];
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new FailureHost(storage, caller, phase, cancellation), new(argv),
                new(phase == "local" ? QpdfLimits.Default with { MaxMemoryBytes = 8192 } : QpdfLimits.Default), cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase is "local" or "shared") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [MemberData(nameof(UnknownCases))]
    public async Task UnknownFramingPreservesTheWholeProgramAndDiscardsEarlierCandidates(string mode, bool job)
    {
        byte[] program = [.. "q\r\n"u8, .. Image(Samples(), "[]"), .. "\n"u8, .. Image(Samples(), "/Unknown"), .. " Q\nBT /Used 8 Tf (after) Tj ET\n"u8];
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(program, mode == "external-form"), QpdfInputOwnership.Transfer);
        List<string> argv = ["input"]; Dictionary<string, object> settings = new() { ["inputFile"] = "input" };
        if (mode.StartsWith("external", StringComparison.Ordinal))
        { argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0"]); settings["externalizeInlineImages"] = true; settings["iiMinBytes"] = "0"; }
        if (mode == "normalize") { argv.Add("--normalize-content=y"); settings["normalizeContent"] = "y"; }
        if (mode == "qdf") { argv.AddRange(["--qdf", "--ii-min-bytes=999999"]); settings["qdf"] = true; settings["iiMinBytes"] = "999999"; }
        if (mode == "scan") { argv.AddRange(["--pages", ".", "1", "--", "--remove-unreferenced-resources=yes"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; settings["removeUnreferencedResources"] = "yes"; }
        if (mode == "check") { argv.Add("--check"); settings["check"] = ""; }
        else
        {
            argv.AddRange(["--compress-streams=n", "--decode-level=none", "output"]);
            settings["compressStreams"] = "n"; settings["decodeLevel"] = "none"; settings["outputFile"] = "output";
        }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        if (mode == "check") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckSkipped); return; }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page; PdfStream content = Assert.IsType<PdfStream>(Get(document, page, "Contents"));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
        if (mode == "external-form")
        {
            content = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, resources, "XObject")), "Fm"));
            resources = Assert.IsType<PdfDictionary>(Get(document, content.Dictionary, "Resources"));
        }
        Assert.Equal(program, content.RawMemory.ToArray());
        if (resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject objects))
        { Assert.Empty(Assert.IsType<PdfDictionary>(objects).Keys); }
        if (mode == "scan") { Assert.Equal(2, Assert.IsType<PdfDictionary>(Get(document, resources, "Font")).Count); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryConsumerSkipsCompleteFilteredPackets(string filter, string mode, bool job)
    {
        (byte[] packet, string spec) = Packet(filter); byte[] image = Image(packet, spec);
        byte[] program = [.. "q\r\n80 0 0 80 10 10 cm "u8, .. image, .. " Q\nBT /Used 8 Tf (after) Tj ET\n"u8];
        bool form = mode == "external-form", external = mode.StartsWith("external", StringComparison.Ordinal);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(program, form), QpdfInputOwnership.Transfer);
        List<string> argv = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (external) { argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0"]); settings["externalizeInlineImages"] = true; settings["iiMinBytes"] = "0"; }
        if (mode == "normalize") { argv.Add("--normalize-content=y"); settings["normalizeContent"] = "y"; }
        if (mode == "qdf") { argv.AddRange(["--qdf", "--ii-min-bytes=999999"]); settings["qdf"] = true; settings["iiMinBytes"] = "999999"; }
        if (mode == "scan") { argv.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
        argv.AddRange(["--compress-streams=n", "--decode-level=none", "--remove-unreferenced-resources=" + (mode == "scan" ? "yes" : "no"), "output"]);
        settings["compressStreams"] = "n"; settings["decodeLevel"] = "none"; settings["removeUnreferencedResources"] = mode == "scan" ? "yes" : "no";
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfStream escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Root"));
            Assert.Equal(program, Assert.IsType<PdfStream>(Get(document, catalog, "Original")).RawMemory.ToArray());
            PdfDictionary page = document.PageReferences[0].Page;
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
            PdfStream content = Assert.IsType<PdfStream>(Get(document, page, "Contents"));
            if (form)
            {
                content = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, resources, "XObject")), "Fm"));
                resources = Assert.IsType<PdfDictionary>(Get(document, content.Dictionary, "Resources"));
            }
            escaped = external ? Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, resources, "XObject")), "IIm1")) : content;
            if (mode == "scan")
            {
                PdfDictionary fonts = Assert.IsType<PdfDictionary>(Get(document, resources, "Font"));
                Assert.True(fonts.TryGet(PdfName.FromAscii("Used"), out _)); Assert.False(fonts.TryGet(PdfName.FromAscii("Unused"), out _));
            }
        }
        host.Dispose();
        if (external)
        {
            Assert.True(escaped.RawMemory.Span.StartsWith(packet));
            Assert.True(escaped.RawMemory.Span[packet.Length..].IndexOfAnyExcept((byte)' ') < 0);
        }
        else { Assert.True(escaped.RawMemory.Span.IndexOf(image) >= 0); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckingDoesNotParseBinarySamplesAsAnUnterminatedLiteral(bool job)
    {
        byte[] samples = new byte[64]; "A EI (unterminated"u8.CopyTo(samples);
        byte[] program = [.. "BI /W 8 /H 8 /CS /G /BPC 8 ID "u8, .. samples, .. " EI\n"u8];
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(program, false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","check":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppearanceScanningCannotSelectMarkedContentInsideSamples(bool job)
    {
        byte[] samples = Enumerable.Repeat((byte)64, 64).ToArray(); "A EI /Tx BMC (decoy) Tj EMC "u8.CopyTo(samples);
        byte[] image = [.. "BI /W 8 /H 8 /CS /G /BPC 8 ID "u8, .. samples, .. " EI"u8];
        byte[] program = [.. image, .. "\n/Tx BMC BT /Used 8 Tf (old) Tj ET EMC\n"u8];
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DA (/Used 8 Tf 0 g) /DR << /Font << /Used 7 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R] >>", null),
            (4, "null", null),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V (new-value) /Rect [10 10 90 90] /AP << /N 6 0 R >> >>", null),
            (6, "<< /Subtype /Form /BBox [0 0 80 80] /Resources << /Font << /Used 7 0 R >> >> /Length " + program.Length + " >>", program),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true,"streamData":"uncompress"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annots = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots"));
        PdfReference link = Assert.IsType<PdfReference>(annots[0]); PdfDictionary widget = Assert.IsType<PdfDictionary>(document.GetObject(link.ObjectNumber));
        PdfStream appearance = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, widget, "AP")), "N"));
        Assert.True(appearance.RawMemory.Span.IndexOf(image) >= 0); Assert.Contains("new-value", Encoding.ASCII.GetString(appearance.RawMemory.Span));
        Assert.DoesNotContain("(old)", Encoding.ASCII.GetString(appearance.RawMemory.Span)); Assert.Equal(0L, result.Counters.OpenHandles);
    }
}
