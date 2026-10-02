namespace Lokad.Qpdf;

/// <summary>
/// Per-job resource budgets enforced by every execution path. Obtain
/// <see cref="Default" /> and adjust individual properties with object
/// initializers; call <see cref="Validate" /> before use.
/// </summary>
public sealed record QpdfLimits
{
    /// <summary>Maximum bytes accepted for a single input.</summary>
    public long MaxInputBytes { get; init; } = 256L * 1024L * 1024L;

    /// <summary>Maximum bytes produced for a single output.</summary>
    public long MaxOutputBytes { get; init; } = 256L * 1024L * 1024L;

    /// <summary>Maximum live retained memory in bytes charged to the job.</summary>
    public long MaxMemoryBytes { get; init; } = 128L * 1024L * 1024L;

    /// <summary>Maximum cumulative abstract work units charged to the job.</summary>
    public long MaxWork { get; init; } = 1_000_000_000L;

    /// <summary>Maximum cumulative decoded stream bytes charged to the job.</summary>
    public long MaxDecodedBytes { get; init; } = 512L * 1024L * 1024L;

    /// <summary>Maximum host operations (reads, writes, opens) per job.</summary>
    public long MaxHostOperations { get; init; } = 100_000L;

    /// <summary>Maximum concurrently open inputs and staged outputs.</summary>
    public long MaxOpenHandles { get; init; } = 64L;

    /// <summary>Maximum diagnostics recorded per job.</summary>
    public long MaxDiagnostics { get; init; } = 256L;

    /// <summary>Maximum cumulative diagnostic message bytes per job.</summary>
    public long MaxDiagnosticBytes { get; init; } = 64L * 1024L;

    /// <summary>Operations between cancellation checks. Must be at least 1.</summary>
    public long CancellationCheckInterval { get; init; } = 1024L;

    /// <summary>Upstream default parser nesting cap, the largest invocation-level override accepted.</summary>
    internal const long ParserNestingCeiling = 499L;

    /// <summary>Maximum nesting depth for arrays, dictionaries, and page-tree recursion.</summary>
    public long MaxNestingDepth { get; init; } = 256L;

    /// <summary>Maximum stream filters decoded per stream; over-limit streams stay opaque.</summary>
    public long MaxStreamFilters { get; init; } = 25L;

    /// <summary>Maximum cross-reference sections followed per file.</summary>
    public long MaxRevisions { get; init; } = 64L;

    /// <summary>Gets the default budgets.</summary>
    public static QpdfLimits Default { get; } = new QpdfLimits();

    /// <summary>Validates that every budget is positive.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A budget is not positive.</exception>
    public void Validate()
    {
        if (MaxInputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputBytes), "Budget must be positive.");
        }

        if (MaxOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputBytes), "Budget must be positive.");
        }

        if (MaxMemoryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMemoryBytes), "Budget must be positive.");
        }

        if (MaxWork <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWork), "Budget must be positive.");
        }

        if (MaxDecodedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDecodedBytes), "Budget must be positive.");
        }

        if (MaxHostOperations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHostOperations), "Budget must be positive.");
        }

        if (MaxOpenHandles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOpenHandles), "Budget must be positive.");
        }

        if (MaxDiagnostics <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDiagnostics), "Budget must be positive.");
        }

        if (MaxDiagnosticBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDiagnosticBytes), "Budget must be positive.");
        }

        if (CancellationCheckInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CancellationCheckInterval), "Budget must be positive.");
        }

        if (MaxNestingDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxNestingDepth), "Budget must be positive.");
        }

        if (MaxRevisions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRevisions), "Budget must be positive.");
        }

        if (MaxStreamFilters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStreamFilters), "Stream-filter budget must not be negative.");
        }
    }
}

