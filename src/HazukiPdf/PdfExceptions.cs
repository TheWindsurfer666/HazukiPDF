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

/// <summary>Thrown when the input PDF is encrypted; encrypted files are not supported.</summary>
public sealed class PdfEncryptedException : PdfFormatException
{
    /// <summary>Creates the exception.</summary>
    public PdfEncryptedException()
        : base("The PDF is encrypted. Encrypted files are not supported.")
    {
    }
}
