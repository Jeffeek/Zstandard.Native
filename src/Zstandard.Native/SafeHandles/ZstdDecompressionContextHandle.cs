using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native.SafeHandles;

/// <summary>
/// Owns a native <c>ZSTD_DCtx*</c>. Guarantees release even on exceptional teardown.
/// </summary>
public sealed class ZstdDecompressionContextHandle : SafeHandle
{
    public ZstdDecompressionContextHandle() : base(nint.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == nint.Zero;

    internal static ZstdDecompressionContextHandle Create()
    {
        var h = new ZstdDecompressionContextHandle();
        var ptr = ZstdNative.ZSTD_createDCtx();
        if (ptr == nint.Zero)
        {
            h.Dispose();
            throw new ZstdException("ZSTD_createDCtx returned null.", 0);
        }
        h.SetHandle(ptr);
        return h;
    }

    internal nint DangerousGet() => handle;

    protected override bool ReleaseHandle()
    {
        _ = ZstdNative.ZSTD_freeDCtx(handle);
        return true;
    }
}
