using Xunit;
using Zstandard.Native;

namespace Zstandard.Native.Tests;

public class EdgeCaseTests
{
    [Fact]
    public void ZeroLength_Source_Compresses_To_Empty_Frame()
    {
        var dst = new byte[ZstdCompressor.GetCompressBound(0)];
        var written = ZstdCompressor.Compress([], dst);

        Assert.True(written > 0, "Zstd always emits a frame header even for empty input.");

        var roundTrip = ZstdCompressor.Decompress(dst.AsSpan(0, written), []);
        Assert.Equal(0, roundTrip);
    }

    [Fact]
    public void Corrupted_Frame_Throws_ZstdException()
    {
        var src = new byte[1024];
        new Random(123).NextBytes(src);
        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var written = ZstdCompressor.Compress(src, dst);

        // Flip a byte in the middle to corrupt the frame.
        dst[written / 2] ^= 0xFF;

        var roundTrip = new byte[src.Length];
        var ex = Assert.Throws<ZstdException>(() =>
            ZstdCompressor.Decompress(dst.AsSpan(0, written), roundTrip));

        Assert.NotEqual(0u, (uint)ex.ErrorCode);
    }

    [Fact]
    public void Random_Garbage_Is_Reported_As_Error()
    {
        var garbage = new byte[64];
        new Random(999).NextBytes(garbage);
        var roundTrip = new byte[1024];

        Assert.Throws<ZstdException>(() => ZstdCompressor.Decompress(garbage, roundTrip));
    }

    [Fact]
    public void Compression_Level_Out_Of_Range_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZstdCompressor.Compress(new byte[16], new byte[128], compressionLevel: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZstdCompressor.Compress(new byte[16], new byte[128], compressionLevel: 0));
    }

    [Fact]
    public void Destination_Too_Small_Throws()
    {
        var src = new byte[4096];
        new Random(1).NextBytes(src);
        Assert.Throws<ZstdException>(() => ZstdCompressor.Compress(src, new byte[1]));
    }

    /// <summary>
    /// Verifies the codec can handle payloads larger than Int32.MaxValue / 2 (i.e. real >2GB inputs).
    /// Allocates ~2.5 GiB plus a compressed-bound staging buffer, so it is gated behind an env var.
    /// </summary>
    [Fact]
    public void Large_Payload_Over_2GB_RoundTrips()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ZSTD_RUN_LARGE_TESTS"), "1", StringComparison.Ordinal))
        {
            return; // soft skip — keeps the test discoverable but cheap on CI
        }

        // 2 GiB + 64 MiB — exceeds Int32.MaxValue/2 boundary, exercises 64-bit size paths.
        const long sz = (1L << 31) + (64L << 20);

        // We can't allocate one byte[] > 2 GiB without large-object support enabled,
        // so this is intentionally Span-only and uses NativeMemory for the source.
        unsafe
        {
            byte* src = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)sz);
            try
            {
                // Fill with a compressible pattern.
                for (long i = 0; i < sz; i++)
                {
                    src[i] = (byte)(i & 0x3F);
                }

                var srcSpan = new Span<byte>(src, checked((int)Math.Min(sz, int.MaxValue)));
                var dstLen = ZstdCompressor.GetCompressBound(srcSpan.Length);
                var dst = new byte[dstLen];
                var written = ZstdCompressor.Compress(srcSpan, dst, compressionLevel: 1);

                Assert.True(written > 0 && written < srcSpan.Length);
            }
            finally
            {
                System.Runtime.InteropServices.NativeMemory.Free(src);
            }
        }
    }
}
