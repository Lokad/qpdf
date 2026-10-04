namespace Lokad.Qpdf;

/// <summary>Object syntax and cross-reference failure policy for opening documents.</summary>
public enum QpdfRecoveryMode
{
    /// <summary>Strict parsing only; damaged cross-references fail explicitly.</summary>
    Strict = 0,

    /// <summary>Repairs object syntax; rebuilds damaged cross-references with bounded scanning and warnings.</summary>
    BoundedRepair = 1,
}
