using BenchmarkDotNet.Attributes;
using Zstandard.Native;

namespace Zstandard.Benchmarks;

/// <summary>
/// Measures the streaming path with context reuse — the realistic case for
/// repeated invocations (e.g. per-message compression on a hot socket).
/// </summary>
[
    Config(typeof(BenchConfig)),
    MemoryDiagnoser
]
// ReSharper disable once ClassCanBeSealed.Global
public class StreamingBenchmarks
{
    [Params(64 * 1024, 1 * 1024 * 1024)]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public int PayloadSize { get; set; }

    private byte[] _payload = [];
    private byte[] _outBuf = [];
    private ZstdStreamCompressor? _compressor;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSize];
        new Random(7).NextBytes(_payload);
        _outBuf = new byte[ZstdCompressor.GetCompressBound(PayloadSize)];
        _compressor = new ZstdStreamCompressor(compressionLevel: 3);
    }

    [GlobalCleanup]
    public void Cleanup() => _compressor?.Dispose();

    [Benchmark(Description = "Stream.Compress (context reuse)")]
    public int StreamCompressReuse()
    {
        _compressor!.Reset();
        var r = _compressor.Compress(_payload, _outBuf, ZstdEndDirective.End);
        return r.BytesWritten;
    }

    [Benchmark(Description = "Stream.Compress (fresh context per call)")]
    public int StreamCompressFresh()
    {
        using var c = new ZstdStreamCompressor(compressionLevel: 3);
        var r = c.Compress(_payload, _outBuf, ZstdEndDirective.End);
        return r.BytesWritten;
    }
}
