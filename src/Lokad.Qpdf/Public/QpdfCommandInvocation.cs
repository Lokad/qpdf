namespace Lokad.Qpdf;

/// <summary>An immutable command invocation: an argument vector.</summary>
public sealed class QpdfCommandInvocation
{
    private readonly string[] _arguments;

    /// <summary>Creates an invocation, copying the arguments.</summary>
    /// <exception cref="ArgumentNullException">The arguments are null.</exception>
    public QpdfCommandInvocation(IEnumerable<string> arguments)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        _arguments = [.. arguments];
    }

    /// <summary>Gets the copied arguments.</summary>
    public IReadOnlyList<string> Arguments => _arguments;
}
