namespace Talky.Server.Hosting;

/// <summary>
/// Thrown when the node cannot start for a reason the operator can fix (missing certificate,
/// insecure production setup, unreadable configuration file). The CLI turns it into a one-line
/// error message and a non-zero exit code instead of a stack trace.
/// </summary>
public sealed class TalkyStartupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TalkyStartupException()
        : base("The Talky host cannot start.")
    {
    }

    /// <summary>Creates the exception with an operator-facing message.</summary>
    /// <param name="message">One line, English, safe to print and log.</param>
    public TalkyStartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an operator-facing message and the underlying cause.</summary>
    /// <param name="message">One line, English, safe to print and log.</param>
    /// <param name="innerException">Underlying cause.</param>
    public TalkyStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
