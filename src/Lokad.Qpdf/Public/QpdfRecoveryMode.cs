namespace Lokad.Qpdf;

/// <summary>Cross-reference failure policy for opening documents.</summary>
public enum QpdfRecoveryMode
{
    /// <summary>Strict parsing only; damaged cross-references fail explicitly.</summary>
    Strict = 0,

    /// <summary>Strict parsing first, then bounded rescan repair with a warning.</summary>
    BoundedRepair = 1,
}
