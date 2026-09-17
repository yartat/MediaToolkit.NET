namespace MediaToolkitNet.Abstractions;

/// <summary>
/// Raised when a native backend reports a failure. Carries the raw native
/// status code so callers can react to specific conditions without parsing text.
/// </summary>
public class MediaToolkitNetException : Exception
{
    /// <summary>Creates an exception without a native status code.</summary>
    public MediaToolkitNetException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception carrying a native status code.</summary>
    /// <param name="backend">Name of the backend that failed.</param>
    /// <param name="message">Description in the caller-facing language.</param>
    /// <param name="nativeCode">Raw status: an HRESULT, an AVERROR, a negated errno or an OSStatus.</param>
    public MediaToolkitNetException(string backend, string message, long nativeCode)
        : base($"[{backend}] {message} (code 0x{nativeCode:X})")
    {
        Backend = backend;
        NativeCode = nativeCode;
    }

    /// <summary>Name of the backend that failed, when known.</summary>
    public string? Backend { get; }

    /// <summary>Raw native status code, when one was available.</summary>
    public long? NativeCode { get; }
}

/// <summary>
/// Raised when a backend cannot be used at all: its native library is missing,
/// or the process is running on the wrong operating system.
/// </summary>
public sealed class MediaBackendUnavailableException : MediaToolkitNetException
{
    /// <summary>Creates the exception.</summary>
    public MediaBackendUnavailableException(string backend, string message, Exception? innerException = null)
        : base($"[{backend}] {message}", innerException)
    {
        BackendName = backend;
    }

    /// <summary>Name of the unavailable backend.</summary>
    public string BackendName { get; }
}
