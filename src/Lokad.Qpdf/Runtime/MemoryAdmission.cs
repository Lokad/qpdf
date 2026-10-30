namespace Lokad.Qpdf.Runtime;

internal static class MemoryAdmission
{
    internal static IMemoryReservation Reserve(IMemoryBudget budget, long bytes)
    {
        if (!budget.TryReserve(bytes, out IMemoryReservation? reservation))
        { throw new QpdfMemoryAdmissionException(bytes); }
        return reservation;
    }

}
