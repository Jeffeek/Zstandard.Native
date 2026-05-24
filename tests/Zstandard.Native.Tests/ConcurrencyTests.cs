using Xunit;

namespace Zstandard.Native.Tests;

// ReSharper disable once ClassCanBeSealed.Global
public class ConcurrencyTests
{
    [Fact]
    public void Parallel_Threads_With_Separate_Contexts_Are_Independent()
    {
        const int threads = 16;
        const int iterationsPerThread = 50;
        const int payload = 32 * 1024;

        var failures = 0;

        Parallel.For(
            0,
            threads,
            threadId =>
            {
                using var compressor = new ZstdStreamCompressor(compressionLevel: 5);
                using var decompressor = new ZstdStreamDecompressor();

                var src = new byte[payload];
                new Random(threadId).NextBytes(src);

                var compressed = new byte[ZstdCompressor.GetCompressBound(payload)];
                var roundTrip = new byte[payload];

                for (var i = 0; i < iterationsPerThread; i++)
                {
                    compressor.Reset();
                    var c = compressor.Compress(src, compressed, ZstdEndDirective.End);
                    if (!c.IsCompleted)
                    {
                        Interlocked.Increment(ref failures);
                        return;
                    }

                    decompressor.Reset();
                    var d = decompressor.Decompress(compressed.AsSpan(0, c.BytesWritten), roundTrip);

                    if (d is (_, payload, true) && src.AsSpan().SequenceEqual(roundTrip))
                        continue;

                    Interlocked.Increment(ref failures);
                    return;
                }
            });

        Assert.Equal(0, failures);
    }

    [Fact]
    public void OneShot_Static_Api_Is_Safe_To_Call_Concurrently()
    {
        const int threads = 32;
        var ok = 0;

        Parallel.For(
            0,
            threads,
            threadId =>
            {
                var src = new byte[4096];
                new Random(threadId).NextBytes(src);
                var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
                var written = ZstdCompressor.Compress(src, dst);
                var back = new byte[src.Length];
                var n = ZstdCompressor.Decompress(dst.AsSpan(0, written), back);
                if (n == src.Length && src.AsSpan().SequenceEqual(back))
                    Interlocked.Increment(ref ok);
            });

        Assert.Equal(threads, ok);
    }

    [Fact]
    public void Disposed_Stream_Compressor_Throws_ObjectDisposed()
    {
        var c = new ZstdStreamCompressor();
        c.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            c.Compress(new byte[4], new byte[64], ZstdEndDirective.End));
    }
}
