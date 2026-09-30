namespace Lokad.Qpdf;

/// <summary>Execution-scoped options for command adapters.</summary>
public sealed class QpdfExecutionOptions
{
    /// <summary>Creates options with default budgets.</summary>
    public QpdfExecutionOptions()
        : this(QpdfLimits.Default)
    {
    }

    /// <summary>Creates options with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">The limits are null.</exception>
    public QpdfExecutionOptions(QpdfLimits limits)
    {
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        limits.Validate();
    }

    /// <summary>Gets the effective budgets.</summary>
    public QpdfLimits Limits { get; }
}
