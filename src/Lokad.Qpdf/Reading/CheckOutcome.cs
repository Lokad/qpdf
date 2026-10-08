using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Reading;

/// <summary>Admits check records and independently escaping detail strings.</summary>
internal static class CheckOutcome
{
    // Two references and the status field after the object header, aligned to
    // a pointer. Names and literal details are borrowed process constants.
    private const long RecordBytes = 40L;

    internal static QpdfCheckOutcome Literal(ResourceGovernor governor, string name, QpdfCheckStatus status, string detail)
    {
        governor.CheckCancellation();
        governor.ReserveMemory(RecordBytes);
        try
        {
            QpdfCheckOutcome outcome = new(name, status, detail);
            long retained = governor.RetainCheckOutcome(outcome, 0L, RecordBytes);
            governor.ReleaseMemory(RecordBytes - retained);
            return outcome;
        }
        catch { governor.ReleaseMemory(RecordBytes); throw; }
    }

    internal static QpdfCheckOutcome Create(ResourceGovernor governor, string name, QpdfCheckStatus status, ReadOnlySpan<DiagnosticPart> parts)
    {
        DiagnosticTextInfo info = DiagnosticMessage.Measure(governor, parts);
        using GovernedText detail = GovernedText.Create(governor, parts, info);
        return Create(governor, name, status, detail);
    }

    internal static QpdfCheckOutcome Create(ResourceGovernor governor, string name, QpdfCheckStatus status, GovernedText detail)
    {
        detail.CheckOwner(governor);
        governor.CheckCancellation();
        governor.ReserveMemory(RecordBytes);
        try
        {
            QpdfCheckOutcome outcome = new(name, status, detail.Value);
            long reserved = checked(RecordBytes + detail.StringReservation);
            long retained = governor.RetainCheckOutcome(outcome, detail.StringReservation, reserved);
            detail.TransferToRetainedStorage(governor);
            governor.ReleaseMemory(reserved - retained);
            return outcome;
        }
        catch { governor.ReleaseMemory(RecordBytes); throw; }
    }
}
