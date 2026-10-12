namespace HazukiPdf;

/// <summary>Thrown when the input cannot be read as a PDF file.</summary>
public class PdfFormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error that caused it.</summary>
    public PdfFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an encrypted PDF cannot be opened: it needs a password that was not given
/// (see <see cref="PdfCompressionOptions.Password"/>), or it uses an unsupported security handler.
/// </summary>
public sealed class PdfEncryptedException : PdfFormatException
{
    /// <summary>Creates the exception.</summary>
    public PdfEncryptedException()
        : base("The PDF is encrypted and needs a password to open.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PdfEncryptedException(string message)
        : base(message)
    {
    }
}
