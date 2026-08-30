using BenchmarkDotNet.Attributes;
using Zstandard.Native;

namespace Zstandard.Benchmarks;

/// <summary>
/// Zstandard.Native throughput benchmarks — one-shot and streaming, JIT and NativeAOT
/// .NET 10.0. Covers the full range of payload sizes and compression levels.
/// </summary>
/// <remarks>
/// <para>
/// One-shot methods use the static <see cref="ZstdCompressor"/> API, which creates a
/// fresh native <c>ZSTD_CCtx</c> / <c>ZSTD_DCtx</c> per call.
/// </para>
/// <para>
/// Streaming methods use a single <see cref="ZstdStreamCompressor"/> instance created
/// in <see cref="Setup"/> and reused across iterations via <c>Reset()</c>, eliminating
/// per-call context allocation.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig)), MemoryDiagnoser, GcServer(value: true), GcConcurrent(value: true)]
// ReSharper disable once ClassCanBeSealed.Global
public class CompressionBenchmarks
{
    [Params(
            4 * 1024, // 4 KB
            64 * 1024, // 64 KB
            1 * 1024 * 1024, // 1 MB
            16 * 1024 * 1024, // 16 MB,
            64 * 1024 * 1024 // 64 MB
        )
    ]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public int PayloadSize { get; set; }

    [Params(1, 3, 9)]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public int Level { get; set; }

    private byte[] _payload = [];
    private byte[] _compressed = [];
    private byte[] _compressDst = [];
    private byte[] _decompressDst = [];
    private ZstdStreamCompressor? _streamCompressor;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSize];
        var rng = new Random();
        rng.NextBytes(_payload);
        for (var i = 0; i < _payload.Length; i += 32)
            _payload[i] = 0;

        _compressDst = new byte[ZstdCompressor.GetCompressBound(PayloadSize)];
        _decompressDst = new byte[PayloadSize];

        var written = ZstdCompressor.Compress(_payload, _compressDst, Level);
        _compressed = [.. _compressDst[..written]];

        _streamCompressor = new ZstdStreamCompressor(compressionLevel: Level);
    }

    [GlobalCleanup]
    public void Cleanup() => _streamCompressor?.Dispose();

    // ---- One-shot ----

    [Benchmark(Baseline = true, Description = "OneShot.Compress")]
    public int OneShotCompress() =>
        ZstdCompressor.Compress(_payload, _compressDst, Level);

    [Benchmark(Description = "OneShot.Decompress")]
    public int OneShotDecompress() =>
        ZstdCompressor.Decompress(_compressed, _decompressDst);

    // ---- Streaming (context reuse) ----

    [Benchmark(Description = "Stream.Compress")]
    public int StreamCompress()
    {
        _streamCompressor!.Reset();
        return _streamCompressor.Compress(_payload, _compressDst, ZstdEndDirective.End).BytesWritten;
    }

    [Benchmark(Description = "Stream.Compress (fresh context)")]
    public int StreamCompressFresh()
    {
        using var c = new ZstdStreamCompressor(compressionLevel: Level);
        return c.Compress(_payload, _compressDst, ZstdEndDirective.End).BytesWritten;
    }
}
