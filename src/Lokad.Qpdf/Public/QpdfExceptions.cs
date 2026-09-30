namespace Lokad.Qpdf;

/// <summary>Why a host operation failed. Transport detail stays with the host.</summary>
public enum QpdfHostFailureReason
{
    /// <summary>The logical input was not found.</summary>
    NotFound = 0,

    /// <summary>A ranged read failed.</summary>
    ReadFailed = 1,

    /// <summary>A staged write failed.</summary>
    WriteFailed = 2,

    /// <summary>Publication of staged output failed.</summary>
    CommitFailed = 3,

    /// <summary>The host does not support the requested capability.</summary>
    Unsupported = 4,

    /// <summary>An output aliases an open input or another planned output.</summary>
    Conflict = 5,

    /// <summary>A read returned no bytes without reaching end of input.</summary>
    NonProgressingRead = 6,
}

/// <summary>Base class for library failures. Messages never embed input content.</summary>
public class QpdfException : Exception
{
    /// <summary>Creates a library failure.</summary>
    public QpdfException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a library failure wrapping a lower-level cause.</summary>
    public QpdfException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A reserved resource budget was exceeded before allocating.</summary>
public sealed class QpdfLimitException : QpdfException
{
    /// <summary>Gets the budget name, such as <c>MaxOutputBytes</c>.</summary>
    public string LimitName { get; }

    /// <summary>Gets the requested amount.</summary>
    public long Requested { get; }

    /// <summary>Gets the applicable budget.</summary>
    public long Allowed { get; }

    /// <summary>Creates a budget failure.</summary>
    public QpdfLimitException(string limitName, long requested, long allowed)
        : base($"Budget {limitName} exceeded: requested {requested}, allowed {allowed}.")
    {
        LimitName = limitName;
        Requested = requested;
        Allowed = allowed;
    }
}

/// <summary>A host-mediated operation failed explicitly.</summary>
public sealed class QpdfHostException : QpdfException
{
    /// <summary>Gets the logical path involved.</summary>
    public QpdfPath Path { get; }

    /// <summary>Gets the failure reason.</summary>
    public QpdfHostFailureReason Reason { get; }

    /// <summary>Creates a host failure without leaking transport detail.</summary>
    public QpdfHostException(QpdfPath path, QpdfHostFailureReason reason)
        : base($"Host operation failed for '{path}' with reason {reason}.")
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>Creates a host failure wrapping a transport error.</summary>
    public QpdfHostException(QpdfPath path, QpdfHostFailureReason reason, Exception? innerException)
        : base($"Host operation failed for '{path}' with reason {reason}.", innerException)
    {
        Path = path;
        Reason = reason;
    }
}



