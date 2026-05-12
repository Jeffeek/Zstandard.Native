using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native.SafeHandles;

/// <summary>
/// Owns a native <c>ZSTD_CCtx*</c>. Guarantees release even on exceptional teardown.
/// </summary>
public sealed class ZstdCompressionContextHandle : SafeHandle
{
    public ZstdCompressionContextHandle() : base(nint.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == nint.Zero;

    internal static ZstdCompressionContextHandle Create()
    {
        var h = new ZstdCompressionContextHandle();
        var ptr = ZstdNative.ZSTD_createCCtx();
        if (ptr == nint.Zero)
        {
            h.Dispose();
            throw new ZstdException("ZSTD_createCCtx returned null.", 0);
        }
        h.SetHandle(ptr);
        return h;
    }

    internal nint DangerousGet() => handle;

    protected override bool ReleaseHandle()
    {
        _ = ZstdNative.ZSTD_freeCCtx(handle);
        return true;
    }
}
