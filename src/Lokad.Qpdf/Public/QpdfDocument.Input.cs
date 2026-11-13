using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public sealed partial class QpdfDocument
{
    private enum InputReadMode { Complete, Selection, PageCount }

    // The caller owns the host lease. This shared first stage admits bytes and
    // xrefs before either security inspection or ordinary document traversal.
    private static async ValueTask<(DocumentEngine Engine, VersionParts Header)> ReadInputEngine(
        IQpdfInput input, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery, CancellationToken cancellationToken, bool valuesEscape,
        bool borrowInputStreams, InputReadMode readMode)
    {
        DocumentEngine? engine = null;
        XRefTable? table = null;
        SelectiveInputData? selectiveInput = null;
        byte[]? bytes = null;
        ReadOnlyMemory<byte> data = default;
        long reservedInput = 0L;
        bool hostInput = false;
        IDisposable? snapshotBorrow = null;
        try
        {
            if (input.Length > limits.MaxInputBytes)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes); }
            if (input.Length > int.MaxValue)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue); }
            if (input.Length == 0L) { throw new QpdfParseException("Input is empty.", 0L, null); }
            if (readMode != InputReadMode.Complete)
            {
                selectiveInput = readMode == InputReadMode.PageCount
                    ? await SelectiveInputData.TryCreateForPageCountAsync(input, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false)
                    : await SelectiveInputData.TryCreateAsync(input, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            if (selectiveInput is not null)
            {
                data = selectiveInput.At(0L);
                engine = DocumentEngine.CreateSelectiveOwned(selectiveInput, limits, governor, diagnostics);
                selectiveInput = null;
            }
            else
            {
                long inputBytes = checked(input.Length + GovernedBufferStream.ArrayOverheadBytes);
                bool snapshot = !valuesEscape && GovernedHost.TryReadInputSnapshot(input, cancellationToken,
                    out data, out hostInput, out snapshotBorrow);
                if (hostInput) { governor.ReserveHostMemory(inputBytes); }
                else { governor.ReserveMemory(inputBytes); }
                reservedInput = inputBytes;
                // Public documents retain fresh reading; exclusive memory-host
                // jobs retain their existing immutable snapshot path.
                if (!snapshot)
                {
                    bytes = await QpdfInputReader.ReadAdmittedAsync(input, 0L, (int)input.Length, governor, cancellationToken).ConfigureAwait(false);
                    data = bytes;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            VersionParts headerParts = ParseHeader(data);
            cancellationToken.ThrowIfCancellationRequested();
            if (engine is null)
            {
                table = XRefTable.Parse(data, limits, governor, diagnostics, recovery, valuesEscape);
                engine = DocumentEngine.CreateOwned(data, table, limits, governor, diagnostics, recovery,
                    reservedInput, valuesEscape, hostInput, snapshotBorrow);
                snapshotBorrow = null;
            }
            engine.BorrowImmutableStreamData = borrowInputStreams;
            table = null;
            reservedInput = 0L;
            cancellationToken.ThrowIfCancellationRequested();
            return (engine, headerParts);
        }
        catch (Exception error)
        {
            bool retry = error is QpdfParseException && diagnostics.ReadingPartialInput;
            bytes = null;
            data = default;
            engine?.Dispose();
            selectiveInput?.Dispose();
            table?.Dispose();
            snapshotBorrow?.Dispose();
            if (hostInput) { governor.ReleaseHostMemory(reservedInput); }
            else { governor.ReleaseMemory(reservedInput); }
            if (retry) { throw new CompleteInputRequiredException(); }
            throw;
        }
    }
}
