using System.Buffers;
using System.Runtime.InteropServices;
using Zstandard.Native.Interop;
using Zstandard.Native.SafeHandles;

namespace Zstandard.Native;

/// <summary>
/// Streaming Zstd decompressor wrapping a native <c>ZSTD_DCtx</c>. Reusable across
/// independent frames via <see cref="Reset"/>. Internal scratch buffers come from
/// <see cref="ArrayPool{T}.Shared"/> and are returned in <see cref="Dispose"/>.
/// </summary>
public sealed class ZstdStreamDecompressor : IDisposable
{
    private readonly ZstdDecompressionContextHandle _handle;
    private byte[]? _scratch;
    private bool _disposed;

    public ZstdStreamDecompressor(int? windowLogMax = null)
    {
        _handle = ZstdDecompressionContextHandle.Create();
        try
        {
            if (windowLogMax is int wlm)
            {
                var code = ZstdNative.ZSTD_DCtx_setParameter(
                    _handle.DangerousGet(), ZstdNative.ZSTD_d_windowLogMax, wlm);
                ZstdException.ThrowIfError(code);
            }

            var recommended = (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_DStreamOutSize());
            _scratch = ArrayPool<byte>.Shared.Rent(recommended);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public ZstdDecompressionContextHandle Handle => _handle;

    public static int RecommendedOutputSize => (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_DStreamOutSize());

    public static int RecommendedInputSize => (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_DStreamInSize());

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
                    _handle.DangerousGet(), &output, &input);

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
            _handle.DangerousGet(),
            resetParameters ? ZstdNative.ZSTD_reset_session_and_parameters : ZstdNative.ZSTD_reset_session_only);
        ZstdException.ThrowIfError(code);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        var scratch = _scratch;
        _scratch = null;
        if (scratch is not null)
        {
            HardwareAccelerator.ClearBuffer(scratch);
            ArrayPool<byte>.Shared.Return(scratch, clearArray: false);
        }

        _handle.Dispose();
    }
}
