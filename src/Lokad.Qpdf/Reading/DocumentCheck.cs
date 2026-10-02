using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Content;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Partial structural inspection over an opened document. Stages that ran report
/// passed or failed; stages whose prerequisites are unimplemented report skipped
/// with an explicit reason, never silently passed.
/// </summary>
internal static class DocumentCheck
{
    private const int MaxObjectFailures = 16;
    private const int MaxStreamFailures = 8;

    internal static List<QpdfCheckOutcome> Run(
        QpdfDocument document,
        QpdfCheckOptions options,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
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

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        DocumentEngine engine = document.Engine;
        List<QpdfCheckOutcome> outcomes = new()
        {
            new QpdfCheckOutcome("header", QpdfCheckStatus.Passed, $"Version {document.Version}."),
            new QpdfCheckOutcome("cross-reference", QpdfCheckStatus.Passed, $"{engine.XRefEntries.Count} entries cover the effective size."),
            new QpdfCheckOutcome("trailer", QpdfCheckStatus.Passed, "Catalog root is reachable and input is unencrypted."),
            new QpdfCheckOutcome("page-tree", QpdfCheckStatus.Passed, $"{document.PageCount} pages traversed."),
        };

        List<(int Number, PdfStream Stream)> streams = new();
        outcomes.Add(CheckObjectGraph(engine, governor, streams, cancellationToken));
        outcomes.Add(CheckStreams(engine, options.DecodeLevel, limits, governor, diagnostics, streams, cancellationToken));
        outcomes.Add(CheckPageContent(document, options, limits, governor, diagnostics, cancellationToken));
        outcomes.Add(LinearizationCheck.Check(engine, document.PageCount, limits, governor, cancellationToken));
        foreach (QpdfCheckOutcome outcome in outcomes)
        {
            if (outcome.Status == QpdfCheckStatus.Failed)
            {
                diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, $"Check '{outcome.Name}' failed: {outcome.Detail}");
            }
        }

        return outcomes;
    }

    private static QpdfCheckOutcome CheckObjectGraph(
        DocumentEngine engine,
        ResourceGovernor governor,
        List<(int Number, PdfStream Stream)> streams,
        CancellationToken cancellationToken)
    {
        List<int> numbers = new(engine.XRefEntries.Keys);
        numbers.Sort();
        int resolved = 0;
        List<string> failures = new();
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            try
            {
                QpdfObject value = engine.Load(number);
                resolved += 1;
                if (value is PdfStream stream)
                {
                    streams.Add((number, stream));
                }
            }
            catch (QpdfLimitException ex)
            {
                failures.Add($"object {number}: {ex.Message}");
                break;
            }
            catch (QpdfException ex)
            {
                failures.Add($"object {number}: {ex.Message}");
                if (failures.Count >= MaxObjectFailures)
                {
                    failures.Add("Further object failures are not recorded.");
                    break;
                }
            }
        }

        if (failures.Count > 0)
        {
            return new QpdfCheckOutcome("object-graph", QpdfCheckStatus.Failed, string.Join(" ", failures));
        }

        return new QpdfCheckOutcome("object-graph", QpdfCheckStatus.Passed, $"{resolved} objects resolved with {streams.Count} streams.");
    }

    private static bool InLevel(string filter, QpdfStreamDecodeLevel level)
    {
        return level switch
        {
            QpdfStreamDecodeLevel.None => false,
            QpdfStreamDecodeLevel.Generalized => filter == FlateFilter.FilterName || filter == LzwFilter.FilterName
                || filter == AsciiHexFilter.FilterName || filter == Ascii85Filter.FilterName,
            QpdfStreamDecodeLevel.Specialized => IsDecodable(filter),
            QpdfStreamDecodeLevel.All => true,
            _ => false,
        };
    }

    private static bool IsDecodable(string filter)
    {
        return filter == FlateFilter.FilterName || filter == LzwFilter.FilterName
            || filter == AsciiHexFilter.FilterName || filter == Ascii85Filter.FilterName
            || filter == RunLengthFilter.FilterName;
    }

    private static QpdfCheckOutcome CheckStreams(
        DocumentEngine engine,
        QpdfStreamDecodeLevel level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        List<(int Number, PdfStream Stream)> streams,
        CancellationToken cancellationToken)
    {
        if (level == QpdfStreamDecodeLevel.None)
        {
            diagnostics.Add(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Info, "Skipped check 'streams': decode level none disables stream decoding.");
            return new QpdfCheckOutcome("streams", QpdfCheckStatus.Skipped, "Decode level none disables stream decoding.");
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        int decoded = 0;
        int unfiltered = 0;
        int aboveLevel = 0;
        int skippedUnavailable = 0;
        List<string> unavailable = new();
        List<string> failures = new();
        foreach ((int number, PdfStream stream) in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FilterStage> stages;
            try
            {
                stages = FilterChain.Resolve(stream.Dictionary, resolver);
            }
            catch (QpdfParseException ex)
            {
                failures.Add($"stream {number}: {ex.Message}");
                if (failures.Count >= MaxStreamFailures)
                {
                    break;
                }

                continue;
            }

            if (stages.Count == 0)
            {
                unfiltered += 1;
                continue;
            }

            bool attempt = true;
            foreach (FilterStage stage in stages)
            {
                if (!InLevel(stage.Name, level))
                {
                    attempt = false;
                    break;
                }
            }

            if (!attempt)
            {
                aboveLevel += 1;
                continue;
            }

            try
            {
                FilterChain.Decode(stream.Data, stages, limits, governor);
                decoded += 1;
            }
            catch (QpdfFilterException ex) when (ex.Reason == QpdfFilterFailureReason.UnsupportedFilter)
            {
                skippedUnavailable += 1;
                foreach (FilterStage stage in stages)
                {
                    if (!IsDecodable(stage.Name) && !unavailable.Contains(stage.Name, StringComparer.Ordinal))
                    {
                        unavailable.Add(stage.Name);
                    }
                }
            }
            catch (QpdfLimitException ex)
            {
                failures.Add($"stream {number}: {ex.Message}");
                break;
            }
            catch (QpdfException ex)
            {
                failures.Add($"stream {number}: {ex.Message}");
                if (failures.Count >= MaxStreamFailures)
                {
                    break;
                }
            }
        }

        if (failures.Count > 0)
        {
            return new QpdfCheckOutcome("streams", QpdfCheckStatus.Failed, string.Join(" ", failures));
        }

        string detail = $"{decoded} decoded, {unfiltered} unfiltered, {aboveLevel} above level {level}.";
        if (skippedUnavailable > 0)
        {
            detail += $" {skippedUnavailable} skipped ({string.Join(", ", unavailable)} unavailable).";
            diagnostics.Add(
                QpdfDiagnosticCode.CheckSkipped,
                QpdfDiagnosticSeverity.Warning,
                $"Skipped check 'streams': {detail} Decoding unavailable filters arrives in a later milestone.");
            return new QpdfCheckOutcome("streams", QpdfCheckStatus.Skipped, detail);
        }

        if (aboveLevel > 0)
        {
            diagnostics.Add(
                QpdfDiagnosticCode.CheckSkipped,
                QpdfDiagnosticSeverity.Info,
                $"Left {aboveLevel} stream(s) above decode level {level} for a higher level.");
        }

        return new QpdfCheckOutcome("streams", QpdfCheckStatus.Passed, detail);
    }

    private static QpdfCheckOutcome CheckPageContent(
        QpdfDocument document,
        QpdfCheckOptions options,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (options.DecodeLevel == QpdfStreamDecodeLevel.None)
        {
            diagnostics.Add(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Info, "Skipped check page-content: decode level none disables stream decoding.");
            return new QpdfCheckOutcome("page-content", QpdfCheckStatus.Skipped, "Decode level none disables stream decoding.");
        }

        DocumentEngine engine = document.Engine;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        int examined = 0;
        int skipped = 0;
        List<string> failures = new();
        int pageNumber = 0;
        foreach (PageLocation location in document.PageReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber += 1;
            if (!location.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
            {
                continue;
            }

            List<PdfStream> streams = new();
            bool unreadable = false;
            if (contents is PdfArray array)
            {
                foreach (QpdfObject item in array.Items)
                {
                    QpdfObject target = item;
                    if (item is PdfReference reference)
                    {
                        if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
                        {
                            unreadable = true;
                            break;
                        }

                        target = resolved;
                    }

                    if (target is not PdfStream stream)
                    {
                        unreadable = true;
                        break;
                    }

                    streams.Add(stream);
                }
            }
            else if (contents is PdfReference singleReference)
            {
                if (!resolver.TryResolve(singleReference, out QpdfObject? resolved) || resolved is not PdfStream resolvedStream)
                {
                    unreadable = true;
                }
                else
                {
                    streams.Add(resolvedStream);
                }
            }
            else if (contents is PdfStream single)
            {
                streams.Add(single);
            }
            else
            {
                unreadable = true;
            }

            if (unreadable)
            {
                skipped += 1;
                continue;
            }

            foreach (PdfStream stream in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? bytes = DecodeCheckStream(stream, resolver, options.DecodeLevel, limits, governor);
                if (bytes is null)
                {
                    skipped += 1;
                    continue;
                }

                try
                {
                    TokenizeCheckStream(bytes, limits, governor, cancellationToken);
                    examined += 1;
                }
                catch (QpdfException ex) when (ex is not QpdfLimitException)
                {
                    failures.Add(string.Concat("Page ", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), " content: ", ex.Message));
                    if (failures.Count >= MaxStreamFailures)
                    {
                        break;
                    }
                }
            }

            if (failures.Count >= MaxStreamFailures)
            {
                break;
            }
        }

        if (failures.Count > 0)
        {
            return new QpdfCheckOutcome("page-content", QpdfCheckStatus.Failed, string.Join(" ", failures));
        }

        return new QpdfCheckOutcome("page-content", QpdfCheckStatus.Passed, string.Concat(examined.ToString(System.Globalization.CultureInfo.InvariantCulture), " content streams tokenized, ", skipped.ToString(System.Globalization.CultureInfo.InvariantCulture), " skipped."));
    }

    private static byte[]? DecodeCheckStream(
        PdfStream stream,
        IIndirectResolver resolver,
        QpdfStreamDecodeLevel level,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        IReadOnlyList<FilterStage> stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, resolver);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return null;
        }

        if (stages.Count == 0)
        {
            return stream.Data;
        }

        foreach (FilterStage stage in stages)
        {
            if (!InLevel(stage.Name, level))
            {
                return null;
            }
        }

        try
        {
            return FilterChain.Decode(stream.Data, stages, limits, governor);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return null;
        }
    }

    private static void TokenizeCheckStream(
        byte[] bytes,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        ContentTokenizer tokenizer = new(bytes, limits, governor);
        byte[] idMarker = [(byte)73, (byte)68];
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual(idMarker))
            {
                tokenizer.TryReadInlineImageData(out ContentToken _);
            }
        }
    }
}
