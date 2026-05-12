using BenchmarkDotNet.Attributes;
using Zstandard.Native;

namespace Zstandard.Benchmarks;

/// <summary>
/// Head-to-head: Zstandard.Native (libzstd P/Invoke, AOT-safe) vs ZstdSharp.Port
/// (managed translation). Reports time, allocations, and computed MB/s.
/// </summary>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
[GcServer(true)]
[GcConcurrent(true)]
public class CompressionBenchmarks
{
    [Params(4 * 1024, 64 * 1024, 1 * 1024 * 1024, 16 * 1024 * 1024)]
    public int PayloadSize { get; set; }

    [Params(1, 3, 9)]
    public int Level { get; set; }

    private byte[] _payload = [];
    private byte[] _compressed = [];
    private byte[] _native_dst = [];
    private byte[] _native_decompressed = [];
    private byte[] _sharp_dst = [];
    private byte[] _sharp_decompressed = [];

    [GlobalSetup]
    public void Setup()
    {
        // Deterministic, semi-compressible payload — a mix of zeros and pseudo-random
        // bytes so the codec actually has something to chew on.
        _payload = new byte[PayloadSize];
        var rng = new Random(42);
        rng.NextBytes(_payload);
        for (int i = 0; i < _payload.Length; i += 32)
        {
            _payload[i] = 0;
        }

        var bound = ZstdCompressor.GetCompressBound(PayloadSize);
        _native_dst = new byte[bound];
        _sharp_dst = new byte[bound];
        _native_decompressed = new byte[PayloadSize];
        _sharp_decompressed = new byte[PayloadSize];

        // Prime the compressed buffer for decompress benchmarks.
        var written = ZstdCompressor.Compress(_payload, _native_dst, Level);
        _compressed = _native_dst.AsSpan(0, written).ToArray();
    }

    // ---------- Compress ----------

    [Benchmark(Baseline = true, Description = "Native.Compress")]
    public int NativeCompress() =>
        ZstdCompressor.Compress(_payload, _native_dst, Level);

    [Benchmark(Description = "ZstdSharp.Compress")]
    public int SharpCompress()
    {
        using var compressor = new ZstdSharp.Compressor(Level);
        return compressor.Wrap(_payload, _sharp_dst);
    }

    // ---------- Decompress ----------

    [Benchmark(Description = "Native.Decompress")]
    public int NativeDecompress() =>
        ZstdCompressor.Decompress(_compressed, _native_decompressed);

    [Benchmark(Description = "ZstdSharp.Decompress")]
    public int SharpDecompress()
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(_compressed, _sharp_decompressed);
    }
}
