using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native;

/// <summary>
/// Thrown when libzstd reports an error (frame corruption, output buffer too small,
/// invalid parameter, etc.). The native error name is included in
/// <see cref="Exception.Message"/> and the raw libzstd return code is exposed via
/// <see cref="ErrorCode"/>.
/// </summary>
public sealed class ZstdException : Exception
{
    /// <summary>
    /// The raw <c>size_t</c> error code returned by libzstd. Inspect with
    /// <c>ZSTD_isError</c> semantics if you need to branch on the kind of error.
    /// </summary>
    public nuint ErrorCode { get; }

    /// <summary>
    /// Creates a new <see cref="ZstdException"/>. Intended for internal use; consumers
    /// should catch this type rather than construct it.
    /// </summary>
    public ZstdException(string message, nuint errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }

    internal static void ThrowIfError(nuint code)
    {
        if (ZstdNative.ZSTD_isError(code) != 0)
        {
            Throw(code);
        }
    }

    private static void Throw(nuint code)
    {
        var ptr = ZstdNative.ZSTD_getErrorName(code);
        var name = ptr == nint.Zero ? "unknown error" : Marshal.PtrToStringUTF8(ptr) ?? "unknown error";
        throw new ZstdException($"Zstd error: {name}", code);
    }
}
