using Xunit;
using Zstandard.Native;

namespace Zstandard.Native.Tests;

public class RoundTripTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(19)]
    public void Compress_Decompress_RoundTrip_Equals_Original(int level)
    {
        var src = new byte[256 * 1024];
        new Random(level).NextBytes(src);

        var bound = ZstdCompressor.GetCompressBound(src.Length);
        var dst = new byte[bound];
        var written = ZstdCompressor.Compress(src, dst, level);

        var roundTrip = new byte[src.Length];
        var decoded = ZstdCompressor.Decompress(dst.AsSpan(0, written), roundTrip);

        Assert.Equal(src.Length, decoded);
        Assert.Equal(src, roundTrip);
    }

    [Fact]
    public void FrameContentSize_Returns_Original_Size()
    {
        var src = new byte[8192];
        new Random(0).NextBytes(src);

        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var written = ZstdCompressor.Compress(src, dst);

        var size = ZstdCompressor.GetFrameContentSize(dst.AsSpan(0, written));
        Assert.Equal(src.Length, size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(63)]
    [InlineData(65_537)]
    public void Various_Sizes_RoundTrip(int size)
    {
        var src = new byte[size];
        new Random(size + 1).NextBytes(src);

        var bound = ZstdCompressor.GetCompressBound(size);
        var dst = new byte[Math.Max(bound, 1)];
        var written = ZstdCompressor.Compress(src, dst);

        var roundTrip = new byte[size];
        var decoded = ZstdCompressor.Decompress(dst.AsSpan(0, written), roundTrip);

        Assert.Equal(size, decoded);
        Assert.Equal(src, roundTrip);
    }
}
