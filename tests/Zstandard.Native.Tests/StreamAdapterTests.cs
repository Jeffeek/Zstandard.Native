using Xunit;

namespace Zstandard.Native.Tests;

public class StreamAdapterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    public void Compression_Then_Decompression_Streams_RoundTrip(int level)
    {
        var src = new byte[256 * 1024];
        new Random(level).NextBytes(src);

        using var compressed = new MemoryStream();
        using (var cs = new ZstdCompressionStream(compressed, compressionLevel: level, leaveOpen: true))
        {
            cs.Write(src, 0, src.Length);
        }

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
        {
            cs.Write(src);
        }

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
