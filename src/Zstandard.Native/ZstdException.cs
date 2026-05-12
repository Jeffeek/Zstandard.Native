using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native;

public sealed class ZstdException : Exception
{
    public nuint ErrorCode { get; }

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
