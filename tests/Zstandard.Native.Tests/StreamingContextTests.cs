using Xunit;

namespace Zstandard.Native.Tests;

// ReSharper disable once ClassCanBeSealed.Global
public class StreamingContextTests
{
    // -------------------------------------------------------------------------
    // ZstdStreamCompressor
    // -------------------------------------------------------------------------

    [Fact]
    public void StreamCompressor_RecommendedOutputSize_IsPositive()
        => Assert.True(ZstdStreamCompressor.RecommendedOutputSize > 0);

    [Fact]
    public void StreamCompressor_RecommendedInputSize_IsPositive()
        => Assert.True(ZstdStreamCompressor.RecommendedInputSize > 0);

    [Fact]
    public void StreamCompressor_WriteChecksum_RoundTrips()
    {
        var src = new byte[4096];
        new Random(1).NextBytes(src);
        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var back = new byte[src.Length];

        using var c = new ZstdStreamCompressor(compressionLevel: 3, writeChecksum: true);
        var r = c.Compress(src, dst, ZstdEndDirective.End);
        Assert.True(r.IsCompleted);

        using var d = new ZstdStreamDecompressor();
        var dr = d.Decompress(dst.AsSpan(0, r.BytesWritten), back);
        Assert.True(dr.IsCompleted);
        Assert.Equal(src, back.AsSpan(0, dr.BytesWritten).ToArray());
    }

    [Fact]
    public void StreamCompressor_Reset_WithResetParameters_AllowsNewFrame()
    {
        var src = new byte[1024];
        new Random(2).NextBytes(src);
        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var back = new byte[src.Length];

        using var c = new ZstdStreamCompressor();
        c.Compress(src, dst, ZstdEndDirective.End);

        c.Reset(resetParameters: true);

        var r = c.Compress(src, dst, ZstdEndDirective.End);
        Assert.True(r.IsCompleted);

        using var d = new ZstdStreamDecompressor();
        var dr = d.Decompress(dst.AsSpan(0, r.BytesWritten), back);
        Assert.True(dr.IsCompleted);
    }

    [Fact]
    public void StreamCompressor_Dispose_IsIdempotent()
    {
        var c = new ZstdStreamCompressor();
        c.Dispose();
        c.Dispose(); // must not throw
    }

    [Fact]
    public void StreamCompressor_Reset_AfterDispose_Throws()
    {
        var c = new ZstdStreamCompressor();
        c.Dispose();
        Assert.Throws<ObjectDisposedException>(() => c.Reset());
    }

    // -------------------------------------------------------------------------
    // ZstdStreamDecompressor
    // -------------------------------------------------------------------------

    [Fact]
    public void StreamDecompressor_RecommendedOutputSize_IsPositive()
        => Assert.True(ZstdStreamDecompressor.RecommendedOutputSize > 0);

    [Fact]
    public void StreamDecompressor_RecommendedInputSize_IsPositive()
        => Assert.True(ZstdStreamDecompressor.RecommendedInputSize > 0);

    [Fact]
    public void StreamDecompressor_WindowLogMax_Accepted()
    {
        var src = new byte[4096];
        new Random(3).NextBytes(src);
        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var written = ZstdCompressor.Compress(src, dst);

        var back = new byte[src.Length];
        using var d = new ZstdStreamDecompressor(windowLogMax: 27);
        var r = d.Decompress(dst.AsSpan(0, written), back);
        Assert.True(r.IsCompleted);
        Assert.Equal(src, back.AsSpan(0, r.BytesWritten).ToArray());
    }

    [Fact]
    public void StreamDecompressor_Reset_WithResetParameters_AllowsNewFrame()
    {
        var src = new byte[1024];
        new Random(4).NextBytes(src);
        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var written = ZstdCompressor.Compress(src, dst);

        var back = new byte[src.Length];
        using var d = new ZstdStreamDecompressor();
        d.Decompress(dst.AsSpan(0, written), back);

        d.Reset(resetParameters: true);

        var r = d.Decompress(dst.AsSpan(0, written), back);
        Assert.True(r.IsCompleted);
    }

    [Fact]
    public void StreamDecompressor_Dispose_IsIdempotent()
    {
        var d = new ZstdStreamDecompressor();
        d.Dispose();
        d.Dispose(); // must not throw
    }

    [Fact]
    public void StreamDecompressor_Decompress_AfterDispose_Throws()
    {
        var d = new ZstdStreamDecompressor();
        d.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            d.Decompress(new byte[8], new byte[64]));
    }

    [Fact]
    public void StreamDecompressor_Reset_AfterDispose_Throws()
    {
        var d = new ZstdStreamDecompressor();
        d.Dispose();
        Assert.Throws<ObjectDisposedException>(() => d.Reset());
    }
}
