using Xunit;

namespace Zstandard.Native.Tests;

// ReSharper disable once ClassCanBeSealed.Global
public class StreamAsyncTests
{
    [Fact]
    public async Task Async_RoundTrip_Matches_Sync()
    {
        var src = new byte[128 * 1024];
        new Random(1).NextBytes(src);

        using var compressed = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
            await cs.WriteAsync(src);

        compressed.Position = 0;
        await using var ds = new ZstdDecompressionStream(compressed, leaveOpen: true);
        using var decoded = new MemoryStream();
        await ds.CopyToAsync(decoded);

        Assert.Equal(src, decoded.ToArray());
    }

    [Fact]
    public async Task WriteAsync_Honors_Cancellation_Before_First_IO()
    {
        using var sink = new MemoryStream();
        await using var cs = new ZstdCompressionStream(sink);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cs.WriteAsync(new byte[1024], cts.Token));
    }

    [Fact]
    public async Task ReadAsync_Returns_Zero_At_End_Of_Stream()
    {
        var src = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var compressed = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(compressed, leaveOpen: true))
            await cs.WriteAsync(src);

        compressed.Position = 0;
        await using var ds = new ZstdDecompressionStream(compressed);

        var buf = new byte[16];
        var n1 = await ds.ReadAsync(buf);
        Assert.Equal(src.Length, n1);
        Assert.Equal(src, buf.AsSpan(0, n1).ToArray());

        var n2 = await ds.ReadAsync(buf);
        Assert.Equal(0, n2);
    }

    [Fact]
    public async Task DisposeAsync_Closes_Frame_End_Marker()
    {
        var sink = new MemoryStream();
        await using (var cs = new ZstdCompressionStream(sink, leaveOpen: true))
            await cs.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
        // After DisposeAsync the frame must decode cleanly.

        sink.Position = 0;
        await using var ds = new ZstdDecompressionStream(sink);
        using var roundTrip = new MemoryStream();
        await ds.CopyToAsync(roundTrip);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, roundTrip.ToArray());
    }
}
