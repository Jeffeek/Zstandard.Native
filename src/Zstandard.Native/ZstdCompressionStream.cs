using System.Buffers;

namespace Zstandard.Native;

/// <summary>
/// A <see cref="Stream"/> adapter that compresses data written to it and forwards
/// the compressed bytes to an inner destination stream. The full frame is closed
/// when the stream is disposed.
/// </summary>
/// <remarks>
/// <para>
/// Write-only. <see cref="Flush"/> emits any buffered compressed output and issues
/// a Zstandard partial-flush so the next read on a remote decompressor can make
/// progress without waiting for the frame to end. <see cref="Stream.Dispose()"/>
/// closes the frame with an end-of-frame marker — calling code that produces a
/// truncated frame and never disposes will produce an invalid stream.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The underlying <see cref="ZstdStreamCompressor"/>
/// holds mutable native state; serialize access externally or use one stream per
/// producer.
/// </para>
/// <para>
/// <b>AOT:</b> uses only blittable interop. No reflection, no runtime marshalling.
/// </para>
/// </remarks>
public sealed class ZstdCompressionStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly ZstdStreamCompressor _compressor;
    private byte[]? _outBuf;
    private bool _disposed;
    private bool _finished;

    /// <summary>
    /// Wraps <paramref name="destination"/> with a Zstandard compressor.
    /// </summary>
    /// <param name="destination">The sink that receives compressed bytes. Must be writable.</param>
    /// <param name="compressionLevel">Zstandard compression level (1..22). Default 3.</param>
    /// <param name="leaveOpen">When <c>true</c>, <see cref="Dispose"/> does not dispose <paramref name="destination"/>.</param>
    public ZstdCompressionStream(Stream destination, int compressionLevel = 3, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        _inner = destination;
        _leaveOpen = leaveOpen;
        _compressor = new ZstdStreamCompressor(compressionLevel);
        _outBuf = ArrayPool<byte>.Shared.Rent(ZstdStreamCompressor.RecommendedOutputSize);
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            // ReSharper disable once RedundantArgumentDefaultValue
            var r = _compressor.Compress(remaining, _outBuf, ZstdEndDirective.Continue);
            if (r.BytesWritten > 0)
            {
                _inner.Write(_outBuf.AsSpan(0, r.BytesWritten));
            }
            if (r is { BytesConsumed: 0, BytesWritten: 0 })
            {
                // Should never happen on a valid context, but guard against an infinite loop.
                throw new ZstdException("ZstdCompressionStream made no progress on Write.", 0);
            }
            remaining = remaining[r.BytesConsumed..];
        }
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1];
        one[0] = value;
        Write(one);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainTo(ZstdEndDirective.Flush);
        _inner.Flush();
    }

    private void DrainTo(ZstdEndDirective directive)
    {
        while (true)
        {
            var r = _compressor.Compress(default, _outBuf, directive);
            if (r.BytesWritten > 0)
            {
                _inner.Write(_outBuf.AsSpan(0, r.BytesWritten));
            }
            if (r.IsCompleted)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            // ReSharper disable once RedundantArgumentDefaultValue
            var r = _compressor.Compress(remaining.Span, _outBuf, ZstdEndDirective.Continue);
            if (r.BytesWritten > 0)
            {
                await _inner.WriteAsync(_outBuf.AsMemory(0, r.BytesWritten), cancellationToken).ConfigureAwait(false);
            }
            if (r is { BytesConsumed: 0, BytesWritten: 0 })
            {
                throw new ZstdException("ZstdCompressionStream made no progress on WriteAsync.", 0);
            }
            remaining = remaining[r.BytesConsumed..];
        }
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await DrainAsyncTo(ZstdEndDirective.Flush, cancellationToken).ConfigureAwait(false);
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DrainAsyncTo(ZstdEndDirective directive, CancellationToken cancellationToken)
    {
        while (true)
        {
            var r = _compressor.Compress(default, _outBuf, directive);
            if (r.BytesWritten > 0)
            {
                await _inner.WriteAsync(_outBuf.AsMemory(0, r.BytesWritten), cancellationToken).ConfigureAwait(false);
            }
            if (r.IsCompleted)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_finished)
            {
                await DrainAsyncTo(ZstdEndDirective.End, CancellationToken.None).ConfigureAwait(false);
                _finished = true;
            }
        }
        finally
        {
            _compressor.Dispose();
            var buf = _outBuf;
            _outBuf = null;
            if (buf is not null)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            if (!_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            _disposed = true;
            // ReSharper disable once GCSuppressFinalizeForTypeWithoutDestructor
            GC.SuppressFinalize(this);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            try
            {
                if (!_finished)
                {
                    DrainTo(ZstdEndDirective.End);
                    _finished = true;
                }
            }
            finally
            {
                _compressor.Dispose();
                var buf = _outBuf;
                _outBuf = null;
                if (buf is not null)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
