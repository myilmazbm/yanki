namespace Yanki.Server.Hosting;

/// <summary>
/// Thrown when the node cannot start for a reason the operator can fix (missing certificate,
/// insecure production setup, unreadable configuration file). The CLI turns it into a one-line
/// error message and a non-zero exit code instead of a stack trace.
/// </summary>
public sealed class YankiStartupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public YankiStartupException()
        : base("The Yanki host cannot start.")
    {
    }

    /// <summary>Creates the exception with an operator-facing message.</summary>
    /// <param name="message">One line, English, safe to print and log.</param>
    public YankiStartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an operator-facing message and the underlying cause.</summary>
    /// <param name="message">One line, English, safe to print and log.</param>
    /// <param name="innerException">Underlying cause.</param>
    public YankiStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
