namespace Lokad.Qpdf;

/// <summary>An immutable command invocation: an argument vector.</summary>
public sealed class QpdfCommandInvocation
{
    /// <summary>Creates an invocation, copying the arguments.</summary>
    /// <exception cref="ArgumentNullException">The arguments are null.</exception>
    public QpdfCommandInvocation(IEnumerable<string> arguments)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        Arguments = Array.AsReadOnly(arguments.ToArray());
    }

    /// <summary>Gets a read-only snapshot of the arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }
}
