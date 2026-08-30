using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;
using Zstandard.Native;

namespace Zstandard.Benchmarks;

/// <summary>
/// Compresses and decompresses a deterministic in-memory ZIP archive stored without
/// deflate compression — a realistic payload pattern used in containerisation pipelines,
/// asset packaging, and log shipping where files are first collected into a ZIP (store
/// mode) and then handed to a faster / stronger codec like Zstd.
/// </summary>
/// <remarks>
/// <para>
/// Payload: 20 JSON config entries + 10 C# source-style entries + one pseudo-binary
/// blob (~400 KB total; ~45 % compressible). All ZIP entries use
/// <see cref="CompressionLevel.NoCompression"/> so Zstd receives raw, varied data.
/// All benchmarks run at each library's maximum compression level (22).
/// </para>
/// <para>
/// Compared libraries (JIT .NET 10.0 only — all comparison libraries use
/// <c>[DllImport]</c> with runtime marshalling, incompatible with NativeAOT):
/// </para>
/// <list type="bullet">
///   <item><term>Zstandard.Native</term><description>this library — <c>[LibraryImport]</c>, <c>ZstdStreamCompressor.Reset()</c>, 0 B</description></item>
///   <item><term>ZstdSharp.Port</term><description>pure managed C# port — Span API, reused <c>Compressor</c>, 0 B</description></item>
///   <item><term>ZstdNet</term><description>native P/Invoke (<c>[DllImport]</c>) — <c>byte[]</c> API, allocates output per call</description></item>
///   <item><term>ImpromptuNinjas.ZStd</term><description>native P/Invoke (<c>[DllImport]</c>) — Span API, reused context, 0 B</description></item>
/// </list>
/// <para>
/// All compress benchmarks reuse a single compression context across iterations (the
/// natural high-throughput usage of every library). For Zstandard.Native this means
/// <see cref="ZstdStreamCompressor.Reset"/>, which resets the native <c>ZSTD_CCtx</c>
/// in-place and skips the per-call context allocation that
/// <see cref="ZstdCompressor.Compress"/> would incur.
/// </para>
/// </remarks>
[Config(typeof(JitBenchConfig)), MemoryDiagnoser, CategoriesColumn]
// ReSharper disable once ClassCanBeSealed.Global
public class ZipPayloadBenchmarks
{
    private static readonly byte[] ZipPayload = BuildZipPayload();

    // Payload size exposed so ThroughputColumn can compute MB/s for this fixed-payload benchmark.
    internal static readonly int PayloadBytes = ZipPayload.Length;

    // Keyed by [Benchmark(Description = "...")] value; computed at class-load time so the
    // CompressionRatioColumn can read it from the main BenchmarkDotNet process when it
    // renders the summary (child benchmark processes never write back to the parent).
    internal static readonly IReadOnlyDictionary<string, double> CompressionRatios =
        ComputeCompressionRatios();

    private byte[] _compressed = [];
    private byte[] _compressDst = [];
    private byte[] _decompressDst = [];

    private ZstdStreamCompressor? _nativeCompressor;
    private ZstdSharp.Compressor? _sharpCompressor;
    private ZstdSharp.Decompressor? _sharpDecompressor;
    private ZstdNet.Compressor? _netCompressor;
    private ZstdNet.Decompressor? _netDecompressor;
    private ImpromptuNinjas.ZStd.ZStdCompressor? _impromptuCompressor;
    private ImpromptuNinjas.ZStd.ZStdDecompressor? _impromptuDecompressor;

    [GlobalSetup]
    public void Setup()
    {
        _compressDst = new byte[ZstdCompressor.GetCompressBound(ZipPayload.Length)];
        _decompressDst = new byte[ZipPayload.Length];

        // Pre-compress at max level so the decompress benchmarks work on max-level frames.
        var written = ZstdCompressor.Compress(
            ZipPayload,
            _compressDst,
            compressionLevel: ZstdCompressor.MaxCompressionLevel);
        _compressed = [.. _compressDst[..written]];

        _nativeCompressor = new ZstdStreamCompressor(compressionLevel: ZstdCompressor.MaxCompressionLevel);

        _sharpCompressor = new ZstdSharp.Compressor(ZstdSharp.Compressor.MaxCompressionLevel);
        _sharpDecompressor = new ZstdSharp.Decompressor();

        _netCompressor = new ZstdNet.Compressor(new ZstdNet.CompressionOptions(ZstdNet.CompressionOptions.MaxCompressionLevel));
        _netDecompressor = new ZstdNet.Decompressor();

        _impromptuCompressor = new ImpromptuNinjas.ZStd.ZStdCompressor();
        _impromptuCompressor.Set(ImpromptuNinjas.ZStd.CompressionParameter.CompressionLevel, ImpromptuNinjas.ZStd.ZStdCompressor.MaximumCompressionLevel);
        _impromptuDecompressor = new ImpromptuNinjas.ZStd.ZStdDecompressor();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nativeCompressor?.Dispose();
        _sharpCompressor?.Dispose();
        _sharpDecompressor?.Dispose();
        _netCompressor?.Dispose();
        _netDecompressor?.Dispose();
        _impromptuCompressor?.Dispose();
        _impromptuDecompressor?.Dispose();
    }

    // ---- Compress ----

    [Benchmark(Baseline = true, Description = "Native.Compress"), BenchmarkCategory("Compress")]
    public int NativeCompress()
    {
        _nativeCompressor!.Reset();
        return _nativeCompressor.Compress(ZipPayload, _compressDst, ZstdEndDirective.End).BytesWritten;
    }

    [Benchmark(Description = "ZstdSharp.Compress"), BenchmarkCategory("Compress")]
    public int ZstdSharpCompress() =>
        _sharpCompressor!.Wrap(ZipPayload, _compressDst);

    /// <summary>Allocates a new <c>byte[]</c> per call — ZstdNet has no span-based API.</summary>
    [Benchmark(Description = "ZstdNet.Compress"), BenchmarkCategory("Compress")]
    public byte[] ZstdNetCompress() =>
        _netCompressor!.Wrap(ZipPayload);

    [Benchmark(Description = "Impromptu.Compress"), BenchmarkCategory("Compress")]
    public int ImpromptuCompress() =>
        (int)_impromptuCompressor!.Compress(_compressDst, ZipPayload);

    // ---- Decompress ----

    [Benchmark(Description = "Native.Decompress"), BenchmarkCategory("Decompress")]
    public int NativeDecompress() =>
        ZstdCompressor.Decompress(_compressed, _decompressDst);

    [Benchmark(Description = "ZstdSharp.Decompress"), BenchmarkCategory("Decompress")]
    public int ZstdSharpDecompress() =>
        _sharpDecompressor!.Unwrap(_compressed, _decompressDst);

    /// <summary>Allocates a new <c>byte[]</c> per call — ZstdNet has no span-based API.</summary>
    [Benchmark(Description = "ZstdNet.Decompress"), BenchmarkCategory("Decompress")]
    public byte[] ZstdNetDecompress() =>
        _netDecompressor!.Unwrap(_compressed);

    [Benchmark(Description = "Impromptu.Decompress"), BenchmarkCategory("Decompress")]
    public int ImpromptuDecompress() =>
        (int)_impromptuDecompressor!.Decompress(_decompressDst, _compressed);

    // ---- Compression-ratio helper ----

    private static Dictionary<string, double> ComputeCompressionRatios()
    {
        var src = ZipPayload;
        var dst = new byte[ZstdCompressor.GetCompressBound(src.Length)];
        var orig = (double)src.Length;

        Dictionary<string, double> ratios = new(4)
        {
            ["Native.Compress"] = GetRatio(ZstdCompressor.Compress(src, dst, ZstdCompressor.MaxCompressionLevel))
        };

        using (var c = new ZstdSharp.Compressor(ZstdSharp.Compressor.MaxCompressionLevel))
            ratios["ZstdSharp.Compress"] = GetRatio(c.Wrap(src, dst));

        using (var c = new ZstdNet.Compressor(new ZstdNet.CompressionOptions(ZstdNet.CompressionOptions.MaxCompressionLevel)))
            ratios["ZstdNet.Compress"] = GetRatio(c.Wrap(src).Length);

        using (var ic = new ImpromptuNinjas.ZStd.ZStdCompressor())
        {
            ic.Set(ImpromptuNinjas.ZStd.CompressionParameter.CompressionLevel, ImpromptuNinjas.ZStd.ZStdCompressor.MaximumCompressionLevel);
            ratios["Impromptu.Compress"] = GetRatio((int)ic.Compress(dst, src));
        }

        return ratios;

        double GetRatio(int bytesWritten) => bytesWritten / orig * 100.0;
    }

    // ---- Payload builder ----

    private static byte[] BuildZipPayload()
    {
        using var ms = new MemoryStream(512 * 1024);
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 20 JSON config entries (~4 KB each, highly compressible)
            for (var i = 0; i < 20; i++)
            {
                var entry = archive.CreateEntry($"config/settings-{i:D3}.json", CompressionLevel.NoCompression);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                w.Write(BuildJsonEntry(i));
            }

            // 10 C# source-style entries (~8 KB each, highly compressible)
            for (var i = 0; i < 10; i++)
            {
                var entry = archive.CreateEntry($"src/module-{i:D2}.cs", CompressionLevel.NoCompression);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                w.Write(BuildCodeEntry(i));
            }

            // One pseudo-binary blob (~256 KB, partially compressible: periodic zero spans + random noise)
            var blob = new byte[256 * 1024];
            new Random().NextBytes(blob);
            for (var i = 0; i < blob.Length; i += 64)
                Array.Clear(blob, i, Math.Min(16, blob.Length - i));
            var binEntry = archive.CreateEntry("assets/data.bin", CompressionLevel.NoCompression);
            using var bs = binEntry.Open();
            bs.Write(blob);
        }

        return ms.ToArray();
    }

    private static string BuildJsonEntry(int i)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("{");
        sb.AppendLine($"  \"id\": {i},");
        sb.AppendLine($"  \"name\": \"module-{i:D4}\",");
        sb.AppendLine($"  \"version\": \"1.{i % 10}.{i % 5}\",");
        sb.AppendLine("  \"settings\": {");
        for (var k = 0; k < 30; k++)
            sb.AppendLine($"    \"key_{k:D3}\": \"value_{(i * 100) + k:D6}\",");
        sb.AppendLine("    \"enabled\": true");
        sb.AppendLine("  },");
        sb.AppendLine("  \"tags\": [\"alpha\", \"beta\", \"gamma\", \"delta\"]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildCodeEntry(int i)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine($"// Module {i} — generated for benchmark payload");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine($"namespace Benchmark.Generated.Module{i:D2};");
        sb.AppendLine();
        for (var c = 0; c < 5; c++)
        {
            sb.AppendLine($"public sealed class Class{c:D2}");
            sb.AppendLine("{");
            for (var p = 0; p < 10; p++)
                sb.AppendLine($"    public int Property{p:D2} {{ get; set; }} = {(i * 100) + (c * 10) + p};");
            sb.AppendLine();
            sb.AppendLine("    public override string ToString() =>");
            sb.AppendLine($"        $\"Class{c:D2}({{Property00}}, {{Property01}}, {{Property02}})\";");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
