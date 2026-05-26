using Xunit;

namespace Zstandard.Native.Tests;

// ReSharper disable once ClassCanBeSealed.Global
public class StreamAdapterTests
{
    // -------------------------------------------------------------------------
    // ZstdCompressionStream — construction & properties
    // -------------------------------------------------------------------------

    [Fact]
    public void CompressionStream_Null_Destination_Throws()
        => Assert.Throws<ArgumentNullException>(static () => new ZstdCompressionStream(null!));

    [Fact]
    public void CompressionStream_NonWritable_Destination_Throws()
    {
        using var ro = new MemoryStream(new byte[8], writable: false);
        Assert.Throws<ArgumentException>(() => new ZstdCompressionStream(ro));
    }

    [Fact]
    public void CompressionStream_CanRead_Is_False()
    {
        using var cs = new ZstdCompressionStream(new MemoryStream());
        Assert.False(cs.CanRead);
    }

    [Fact]
    public void CompressionStream_CanSeek_Is_False()
    {
        using var cs = new ZstdCompressionStream(new MemoryStream());
        Assert.False(cs.CanSeek);
    }

    [Fact]
    public void CompressionStream_CanWrite_Becomes_False_After_Dispose()
    {
        var cs = new ZstdCompressionStream(new MemoryStream());
        Assert.True(cs.CanWrite);
        cs.Dispose();
        Assert.False(cs.CanWrite);
    }

    [Fact]
    public void CompressionStream_NotSupported_Members_Throw()
    {
        using var cs = new ZstdCompressionStream(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => cs.Length);
        Assert.Throws<NotSupportedException>(() => cs.Position);
        Assert.Throws<NotSupportedException>(() => cs.Position = 0);
        Assert.Throws<NotSupportedException>(() => cs.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => cs.SetLength(0));
    }

    [Fact]
    public void CompressionStream_Write_ArrayOverload_Works()
    {
        var src = new byte[1024];
        new Random(10).NextBytes(src);

        using var sink = new MemoryStream();
        using (var cs = new ZstdCompressionStream(sink, leaveOpen: true))
            cs.Write(src, 0, src.Length);

        sink.Position = 0;
        using var ds = new ZstdDecompressionStream(sink);
        using var decoded = new MemoryStream();
        ds.CopyTo(decoded);
        Assert.Equal(src, decoded.ToArray());
    }

    [Fact]
    public async Task CompressionStream_WriteAsync_ArrayOverload_Works()
    {
        var src = new byte[1024];
        new Random(11).NextBytes(src);

        using var sink = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(sink, leaveOpen: true))
            await cs.WriteAsync(src, 0, src.Length, CancellationToken.None);

        sink.Position = 0;
        await using var ds = new ZstdDecompressionStream(sink);
        using var decoded = new MemoryStream();
        await ds.CopyToAsync(decoded);
        Assert.Equal(src, decoded.ToArray());
    }

    [Fact]
    public async Task CompressionStream_FlushAsync_Works()
    {
        var src = new byte[8192];
        new Random(12).NextBytes(src);

        using var sink = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(sink, leaveOpen: true))
        {
            await cs.WriteAsync(src.AsMemory(0, 4096));
            await cs.FlushAsync();
            await cs.WriteAsync(src.AsMemory(4096));
        }

        sink.Position = 0;
        await using var ds = new ZstdDecompressionStream(sink);
        using var decoded = new MemoryStream();
        await ds.CopyToAsync(decoded);
        Assert.Equal(src, decoded.ToArray());
    }

    [Fact]
    public async Task CompressionStream_DisposeAsync_LeaveOpen_Honored()
    {
        var inner = new MemoryStream();
        var cs = new ZstdCompressionStream(inner, leaveOpen: true);
        await cs.WriteAsync(new byte[] { 1, 2, 3 });
        await cs.DisposeAsync();
        Assert.True(inner.CanWrite);
        await inner.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // ZstdDecompressionStream — construction & properties
    // -------------------------------------------------------------------------

    [Fact]
    public void DecompressionStream_Null_Source_Throws()
        => Assert.Throws<ArgumentNullException>(static () => new ZstdDecompressionStream(null!));

    [Fact]
    public void DecompressionStream_NonReadable_Source_Throws()
    {
        using var wo = new MemoryStream();
        wo.Close();
        Assert.Throws<ArgumentException>(static () => new ZstdDecompressionStream(new WriteOnlyStream()));
    }

    [Fact]
    public void DecompressionStream_CanWrite_Is_False()
    {
        using var ds = new ZstdDecompressionStream(new MemoryStream());
        Assert.False(ds.CanWrite);
    }

    [Fact]
    public void DecompressionStream_CanSeek_Is_False()
    {
        using var ds = new ZstdDecompressionStream(new MemoryStream());
        Assert.False(ds.CanSeek);
    }

    [Fact]
    public void DecompressionStream_CanRead_Becomes_False_After_Dispose()
    {
        var ds = new ZstdDecompressionStream(new MemoryStream());
        Assert.True(ds.CanRead);
        ds.Dispose();
        Assert.False(ds.CanRead);
    }

    [Fact]
    public void DecompressionStream_NotSupported_Members_Throw()
    {
        using var ds = new ZstdDecompressionStream(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => ds.Length);
        Assert.Throws<NotSupportedException>(() => ds.Position);
        Assert.Throws<NotSupportedException>(() => ds.Position = 0);
        Assert.Throws<NotSupportedException>(() => ds.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => ds.SetLength(0));
    }

    [Fact]
    public void DecompressionStream_Flush_Is_NoOp()
    {
        using var ds = new ZstdDecompressionStream(new MemoryStream());
        ds.Flush(); // must not throw
    }

    [Fact]
    public void DecompressionStream_ReadByte_Returns_MinusOne_AtEof()
    {
        var src = new byte[] { 7, 8, 9 };
        using var compressed = new MemoryStream();
        using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
            cs.Write(src);

        compressed.Position = 0;
        using var ds = new ZstdDecompressionStream(compressed);
        var drain = new byte[src.Length];
        _ = ds.Read(drain); // drain the frame; result intentionally unused
        Assert.Equal(-1, ds.ReadByte());
    }

    [Fact]
    public async Task DecompressionStream_ReadAsync_ArrayOverload_Works()
    {
        var src = new byte[1024];
        new Random(13).NextBytes(src);

        using var compressed = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
            await cs.WriteAsync(src);

        compressed.Position = 0;
        await using var ds = new ZstdDecompressionStream(compressed);
        var buf = new byte[src.Length];
        var n = await ds.ReadAsync(buf, 0, buf.Length, CancellationToken.None);
        Assert.Equal(src.Length, n);
        Assert.Equal(src, buf);
    }

    [Fact]
    public async Task DecompressionStream_DisposeAsync_LeaveOpen_Honored()
    {
        var src = new byte[] { 1, 2, 3, 4 };
        using var compressed = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
            cs.Write(src);

        compressed.Position = 0;
        var ds = new ZstdDecompressionStream(compressed, leaveOpen: true);
        await ds.DisposeAsync();
        Assert.True(compressed.CanRead);
    }


    [
        Theory,
        InlineData(1),
        InlineData(3),
        InlineData(9)
    ]
    public void Compression_Then_Decompression_Streams_RoundTrip(int level)
    {
        var src = new byte[256 * 1024];
        new Random(level).NextBytes(src);

        using var compressed = new MemoryStream();
        using (var cs = new ZstdCompressionStream(compressed, compressionLevel: level, leaveOpen: true))
            cs.Write(src, 0, src.Length);

        compressed.Position = 0;
        using var ds = new ZstdDecompressionStream(compressed, leaveOpen: true);
        using var decoded = new MemoryStream();
        ds.CopyTo(decoded);

        Assert.Equal(src, decoded.ToArray());
    }

    [Fact]
    public void Compression_Stream_Flushes_Between_Frames()
    {
        var producer = new byte[16 * 1024];
        new Random(1).NextBytes(producer);

        using var compressed = new MemoryStream();
        using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
        {
            cs.Write(producer.AsSpan(0, 8192));
            cs.Flush();
            cs.Write(producer.AsSpan(8192, 8192));
        }

        compressed.Position = 0;
        using var ds = new ZstdDecompressionStream(compressed);
        using var decoded = new MemoryStream();
        ds.CopyTo(decoded);
        Assert.Equal(producer, decoded.ToArray());
    }

    [Fact]
    public void Compression_Stream_LeaveOpen_Honored()
    {
        var inner = new MemoryStream();
        var cs = new ZstdCompressionStream(inner, leaveOpen: true);
        cs.WriteByte(0x42);
        cs.Dispose();
        Assert.True(inner.CanWrite);
        inner.Dispose();
    }

    [Fact]
    public void Decompression_Stream_LeaveOpen_Honored()
    {
        // Build a tiny valid frame first.
        var src = new byte[] { 1, 2, 3, 4, 5 };
        var compressed = new MemoryStream();
        using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
            cs.Write(src);

        compressed.Position = 0;
        var ds = new ZstdDecompressionStream(compressed, leaveOpen: true);
        ds.ReadByte();
        ds.Dispose();
        Assert.True(compressed.CanRead);
        compressed.Dispose();
    }

    [Fact]
    public void Compression_Stream_Rejects_Reads()
    {
        using var compressed = new MemoryStream();
        using var cs = new ZstdCompressionStream(compressed);
        Assert.Throws<NotSupportedException>(() => cs.Read(new byte[8], 0, 8));
    }

    [Fact]
    public void Decompression_Stream_Rejects_Writes()
    {
        using var compressed = new MemoryStream();
        using var ds = new ZstdDecompressionStream(compressed);
        Assert.Throws<NotSupportedException>(() => ds.Write(new byte[8], 0, 8));
    }

    [Fact]
    public void Stream_Adapters_Decode_Concatenated_Frames()
    {
        // libzstd's streaming decompressor transparently consumes a sequence of
        // concatenated frames, so the consumer side reads them as one continuous
        // payload. We can't use a fresh decompressor per frame on a shared inner
        // stream because the first decompressor's read-ahead buffer would swallow
        // bytes from later frames.
        const int frames = 64;
        const int frameSize = 2048;
        var rng = new Random(42);

        var expected = new byte[frames * frameSize];
        rng.NextBytes(expected);

        using var sink = new MemoryStream();
        for (var i = 0; i < frames; i++)
        {
            using var cs = new ZstdCompressionStream(sink, leaveOpen: true);
            cs.Write(expected.AsSpan(i * frameSize, frameSize));
        }

        sink.Position = 0;
        using var ds = new ZstdDecompressionStream(sink, leaveOpen: true);
        using var decoded = new MemoryStream();
        ds.CopyTo(decoded);

        Assert.Equal(expected, decoded.ToArray());
    }
}

/// <summary>Write-only stream stub used to verify that ZstdDecompressionStream rejects it.</summary>
internal sealed class WriteOnlyStream : Stream
{
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { }
}
