using System.Diagnostics.CodeAnalysis;
using ImageScope = Lokad.Image.IMemoryScope;
using ImageReservation = Lokad.Image.IMemoryReservation;

namespace Lokad.Qpdf.Image;

// Vocabulary forwarding only. Ownership, refusal and subtree cleanup belong to
// the public qpdf budget; Image introduces no second pool or bookkeeping tree.
internal class JpegMemoryBudget(IMemoryBudget budget) : Lokad.Image.IMemoryBudget
{
    public ImageScope CreateScope()
    {
        IMemoryScope scope = budget.CreateScope();
        try { return new Scope(scope); }
        catch { scope.Dispose(); throw; }
    }

    public bool TryReserve(long bytes, [NotNullWhen(true)] out ImageReservation? reservation)
    {
        if (!budget.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
        try { reservation = new Receipt(admitted); return true; }
        catch { admitted.Dispose(); throw; }
    }

    private sealed class Scope(IMemoryScope scope) : JpegMemoryBudget(scope), ImageScope
    {
        public void Dispose() => scope.Dispose();
    }

    private sealed class Receipt(IMemoryReservation reservation) : ImageReservation
    {
        public long Bytes => reservation.Bytes;
        public bool TryResize(long bytes) => reservation.TryResize(bytes);
        public void Dispose() => reservation.Dispose();
    }
}
