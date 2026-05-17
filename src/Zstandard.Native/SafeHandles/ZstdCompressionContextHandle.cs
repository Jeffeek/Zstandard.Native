using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native.SafeHandles;

/// <summary>
/// Owns a native <c>ZSTD_CCtx*</c>. Guarantees release even on exceptional teardown
/// (process unload, finalizer queue, explicit disposal).
/// </summary>
/// <remarks>
/// The handle itself is thread-safe to dispose, but the underlying <c>ZSTD_CCtx</c>
/// is not — see <see cref="ZstdStreamCompressor"/>.
/// </remarks>
public sealed class ZstdCompressionContextHandle() : SafeHandle(nint.Zero, ownsHandle: true)
{
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
