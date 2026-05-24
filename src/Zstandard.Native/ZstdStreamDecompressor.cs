using System.Buffers;
using System.Runtime.InteropServices;
using Zstandard.Native.Interop;
using Zstandard.Native.SafeHandles;

namespace Zstandard.Native;

/// <summary>
/// Streaming Zstandard decompressor wrapping a native <c>ZSTD_DCtx</c>. Reusable across
/// independent frames via <see cref="Reset"/>. Internal scratch buffers come from
/// <see cref="ArrayPool{T}.Shared"/> and are returned in <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> instances are <i>not</i> thread-safe. Use one instance per
/// thread, or serialize access externally. The <c>ZSTD_DCtx</c> carries window /
/// dictionary / position state and will produce garbage if two threads write to it.
/// </para>
/// <para>
/// <b>Disposal:</b> always dispose. The wrapped <see cref="SafeHandle"/> finalizes
/// the native context as a safety net, but pooled scratch buffers will not be
/// returned to <see cref="ArrayPool{T}.Shared"/> without an explicit
/// <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class ZstdStreamDecompressor : IDisposable
{
    private byte[]? _scratch;
    private bool _disposed;

    public ZstdStreamDecompressor(int? windowLogMax = null)
    {
        Handle = ZstdDecompressionContextHandle.Create();
        try
        {
            if (windowLogMax is { } wlm)
            {
                var code = ZstdNative.ZSTD_DCtx_setParameter(
                    Handle.DangerousGet(),
                    ZstdNative.ZSTD_d_windowLogMax,
                    wlm
                );
                ZstdException.ThrowIfError(code);
            }

            var recommended = (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_DStreamOutSize());
            _scratch = ArrayPool<byte>.Shared.Rent(recommended);
        }
        catch
        {
            Handle.Dispose();
            throw;
        }
    }

#pragma warning disable IDE0032
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
    // ReSharper disable once MemberCanBePrivate.Global
    public ZstdDecompressionContextHandle Handle { get; private set; }
#pragma warning restore IDE0032

    // ReSharper disable once UnusedMember.Global
    public static int RecommendedOutputSize => (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_DStreamOutSize());

    // ReSharper disable once MemberCanBeInternal
    public static int RecommendedInputSize => (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_DStreamInSize());

    // ReSharper disable once UnusedMember.Global
    internal Span<byte> Scratch => _scratch.AsSpan();

    /// <summary>
    /// Decompress one streaming step. <see cref="ZstdStreamResult.IsCompleted"/> is
    /// <c>true</c> when the current frame has been fully decoded (libzstd returned 0).
    /// </summary>
    public ZstdStreamResult Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        unsafe
        {
            fixed (byte* srcPtr = &MemoryMarshal.GetReference(source))
            fixed (byte* dstPtr = &MemoryMarshal.GetReference(destination))
            {
                var input = new ZSTD_inBuffer { src = srcPtr, size = (nuint)source.Length, pos = 0 };
                var output = new ZSTD_outBuffer { dst = dstPtr, size = (nuint)destination.Length, pos = 0 };

                var ret = ZstdNative.ZSTD_decompressStream(
                    Handle.DangerousGet(),
                    &output,
                    &input
                );

                ZstdException.ThrowIfError(ret);

                return new ZstdStreamResult(
                    BytesConsumed: checked((int)input.pos),
                    BytesWritten: checked((int)output.pos),
                    IsCompleted: ret == 0);
            }
        }
    }

    public void Reset(bool resetParameters = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var code = ZstdNative.ZSTD_DCtx_reset(
            Handle.DangerousGet(),
            resetParameters
                ? ZstdResetDirective.SessionAndParameters
                : ZstdResetDirective.SessionOnly);
        ZstdException.ThrowIfError(code);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        var scratch = _scratch;
        _scratch = null;
        if (scratch is not null)
        {
            HardwareAccelerator.ClearBuffer(scratch);
            ArrayPool<byte>.Shared.Return(scratch, clearArray: false);
        }

        Handle.Dispose();
    }
}
