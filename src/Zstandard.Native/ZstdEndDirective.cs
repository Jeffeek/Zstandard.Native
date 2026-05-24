namespace Zstandard.Native;

/// <summary>
/// Mirrors libzstd's <c>ZSTD_EndDirective</c>.
/// </summary>
public enum ZstdEndDirective
{
    /// <summary>Collect more input; produce output as appropriate.</summary>
    Continue = 0,
    /// <summary>Flush any buffered data; frame stays open.</summary>
    Flush = 1,
    /// <summary>Flush and close the frame.</summary>
    End = 2
}

/// <summary>
/// Outcome of a single streaming step.
/// </summary>
public readonly record struct ZstdStreamResult(int BytesConsumed, int BytesWritten, bool IsCompleted);
