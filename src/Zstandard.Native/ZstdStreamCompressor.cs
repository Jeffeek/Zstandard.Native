using System.Buffers;
using System.Runtime.InteropServices;
using Zstandard.Native.Interop;
using Zstandard.Native.SafeHandles;

namespace Zstandard.Native;

/// <summary>
/// Streaming Zstandard compressor wrapping a native <c>ZSTD_CCtx</c>. Reusable across
/// independent frames via <see cref="Reset"/>. Internal scratch buffers come from
/// <see cref="ArrayPool{T}.Shared"/> and are returned in <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> instances are <i>not</i> thread-safe. The wrapped
/// <c>ZSTD_CCtx</c> carries mutable streaming state, so concurrent calls into
/// <see cref="Compress(ReadOnlySpan{byte}, Span{byte}, ZstdEndDirective)"/> or
/// <see cref="Reset"/> on the same instance will corrupt the frame. Create one
/// compressor per thread, or guard a shared instance with your own lock.
/// </para>
/// <para>
/// <b>Disposal:</b> always dispose to release the native context and return the
/// pooled scratch buffer. A finalizer on the underlying <see cref="SafeHandle"/>
/// guarantees the native pointer is freed even if <see cref="Dispose"/> is missed,
/// but the pooled buffer will be lost to GC and the leak will surface as ArrayPool
/// pressure under load.
/// </para>
/// </remarks>
public sealed class ZstdStreamCompressor : IDisposable
{
    private byte[]? _scratch;
    private bool _disposed;

    public ZstdStreamCompressor(int compressionLevel = 3, bool writeChecksum = false, int workerThreads = 0)
    {
        Handle = ZstdCompressionContextHandle.Create();
        try
        {
            SetParameter(ZstdNative.ZSTD_c_compressionLevel, compressionLevel);
            if (writeChecksum)
                SetParameter(ZstdNative.ZSTD_c_checksumFlag, 1);
            if (workerThreads > 0)
                SetParameter(ZstdNative.ZSTD_c_nbWorkers, workerThreads);

            // Allocate a recommended-size scratch buffer up front so streaming
            // callers can borrow it without re-renting per call.
            var recommended = (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_CStreamOutSize());
            _scratch = ArrayPool<byte>.Shared.Rent(recommended);
        }
        catch
        {
            Handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The native context handle. Exposed for advanced scenarios; do not free directly.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
    // ReSharper disable once MemberCanBePrivate.Global
    public ZstdCompressionContextHandle Handle { get; private set; }

    /// <summary>
    /// Recommended output buffer size for a single streaming call.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBeInternal
    public static int RecommendedOutputSize => (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_CStreamOutSize());

    /// <summary>
    /// Recommended input buffer size for a single streaming call.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    public static int RecommendedInputSize => (int)Math.Min(int.MaxValue, ZstdNative.ZSTD_CStreamInSize());

    /// <summary>
    /// Borrow the internal scratch buffer (pooled). The slice is valid until the next
    /// call that uses scratch or until <see cref="Dispose"/>.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    internal Span<byte> Scratch => _scratch.AsSpan();

    /// <summary>
    /// Run one streaming step. Returns bytes consumed from <paramref name="source"/>,
    /// bytes written to <paramref name="destination"/>, and whether the operation has
    /// fully drained for the given <paramref name="endOp"/>.
    /// </summary>
    public ZstdStreamResult Compress(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ZstdEndDirective endOp = ZstdEndDirective.Continue
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        unsafe
        {
            fixed (byte* srcPtr = &MemoryMarshal.GetReference(source))
            fixed (byte* dstPtr = &MemoryMarshal.GetReference(destination))
            {
                var input = new ZSTD_inBuffer { src = srcPtr, size = (nuint)source.Length, pos = 0 };
                var output = new ZSTD_outBuffer { dst = dstPtr, size = (nuint)destination.Length, pos = 0 };

                var remaining = ZstdNative.ZSTD_compressStream2(
                    Handle.DangerousGet(),
                    &output,
                    &input,
                    (int)endOp
                );

                ZstdException.ThrowIfError(remaining);

                var completed = endOp switch
                {
                    ZstdEndDirective.End => remaining == 0,
                    ZstdEndDirective.Flush => remaining == 0,
                    _ => input.pos == input.size
                };

                return new ZstdStreamResult(
                    BytesConsumed: checked((int)input.pos),
                    BytesWritten: checked((int)output.pos),
                    IsCompleted: completed);
            }
        }
    }

    /// <summary>
    /// Reset the session so the same context can be reused for a new independent frame.
    /// </summary>
    public void Reset(bool resetParameters = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var code = ZstdNative.ZSTD_CCtx_reset(
            Handle.DangerousGet(),
            resetParameters
                ? ZstdResetDirective.SessionAndParameters
                : ZstdResetDirective.SessionOnly);
        ZstdException.ThrowIfError(code);
    }

    private void SetParameter(int param, int value)
    {
        var code = ZstdNative.ZSTD_CCtx_setParameter(Handle.DangerousGet(), param, value);
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
