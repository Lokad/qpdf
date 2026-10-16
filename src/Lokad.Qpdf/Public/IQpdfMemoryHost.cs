namespace Lokad.Qpdf;

/// <summary>Optional host capability supplying the active shared memory owner for an invocation.</summary>
/// <remarks>
/// The library borrows this budget. Its caller owns the scope and keeps it alive
/// through execution, cleanup and all returned bytes, values and diagnostics.
/// Use a separate host view for each concurrent invocation; all views may share
/// one root allowance. Host-private persistent storage has its own durable owner.
/// </remarks>
public interface IQpdfMemoryHost
{
    /// <summary>Gets the borrowed invocation owner, or null to use local limits only.</summary>
    IMemoryBudget? MemoryBudget { get; }
}
