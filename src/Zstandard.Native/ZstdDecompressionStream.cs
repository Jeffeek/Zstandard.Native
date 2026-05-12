using System.Buffers;

namespace Zstandard.Native;

/// <summary>
/// A <see cref="Stream"/> adapter that reads compressed bytes from an inner source
/// stream and decompresses them on demand. Read-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The underlying <see cref="ZstdStreamDecompressor"/>
/// holds mutable native state; use one instance per reader or serialize access externally.
/// </para>
/// <para>
/// <b>AOT:</b> uses only blittable interop. No reflection, no runtime marshalling.
/// </para>
/// </remarks>
public sealed class ZstdDecompressionStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly ZstdStreamDecompressor _decompressor;
    private byte[]? _inBuf;
    private int _inBufPos;
    private int _inBufLen;
    private bool _disposed;
    private bool _innerEof;

    /// <summary>
    /// Wraps <paramref name="source"/> with a Zstandard decompressor.
    /// </summary>
    /// <param name="source">A readable stream containing one or more Zstandard frames.</param>
    /// <param name="leaveOpen">When <c>true</c>, <see cref="Dispose"/> does not dispose <paramref name="source"/>.</param>
    public ZstdDecompressionStream(Stream source, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        }

        _inner = source;
        _leaveOpen = leaveOpen;
        _decompressor = new ZstdStreamDecompressor();
        _inBuf = ArrayPool<byte>.Shared.Rent(ZstdStreamDecompressor.RecommendedInputSize);
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

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
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush() { /* Read-only stream: nothing to flush. */ }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var totalWritten = 0;
        while (totalWritten < buffer.Length)
        {
            // Refill compressed-input buffer if drained.
            if (_inBufPos >= _inBufLen && !_innerEof)
            {
                _inBufLen = _inner.Read(_inBuf.AsSpan());
                _inBufPos = 0;
                if (_inBufLen == 0)
                {
                    _innerEof = true;
                }
            }

            var inSlice = _inBuf.AsSpan(_inBufPos, _inBufLen - _inBufPos);
            var dstSlice = buffer[totalWritten..];

            var r = _decompressor.Decompress(inSlice, dstSlice);
            _inBufPos += r.BytesConsumed;
            totalWritten += r.BytesWritten;

            if (r.IsCompleted && _innerEof && _inBufPos >= _inBufLen)
            {
                break;
            }

            // No progress and no more input available: end-of-stream.
            if (r.BytesConsumed == 0 && r.BytesWritten == 0)
            {
                break;
            }
        }

        return totalWritten;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        var n = Read(one);
        return n == 0 ? -1 : one[0];
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
            _decompressor.Dispose();
            var buf = _inBuf;
            _inBuf = null;
            if (buf is not null)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
