using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using Perfolizer.Horology;

namespace Zstandard.Benchmarks;

/// <summary>
/// Two-job config: JIT .NET 10.0 and NativeAOT .NET 10.0.
/// </summary>
// ReSharper disable once MemberCanBeInternal
public sealed class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.Mean, StatisticColumn.StdDev, StatisticColumn.P95);
        AddColumn(new ThroughputColumn());
        AddJob(Job.ShortRun.WithRuntime(CoreRuntime.Core10_0).WithId("JIT 10.0"));
        AddJob(Job.ShortRun.WithRuntime(NativeAotRuntime.Net10_0).WithId("AOT 10.0"));
        SummaryStyle = SummaryStyle.Default.WithTimeUnit(TimeUnit.Microsecond);
    }
}

/// <summary>
/// Single-job config: JIT .NET 10.0 only. Used for cross-library comparison benchmarks
/// that include packages incompatible with NativeAOT (e.g. those using <c>[DllImport]</c>
/// with runtime marshalling).
/// </summary>
// ReSharper disable once MemberCanBeInternal
public sealed class JitBenchConfig : ManualConfig
{
    public JitBenchConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.Mean, StatisticColumn.StdDev, StatisticColumn.P95);
        AddColumn(new ThroughputColumn());
        AddColumn(new CompressionRatioColumn());
        AddJob(Job.ShortRun.WithRuntime(CoreRuntime.Core10_0).WithId("JIT 10.0"));
        SummaryStyle = SummaryStyle.Default.WithTimeUnit(TimeUnit.Microsecond);
    }
}

/// <summary>
/// Reports MB/s based on a <c>[Params]</c>-bound <c>PayloadSize</c> property on the bench class.
/// </summary>
// ReSharper disable once MemberCanBeInternal
public sealed class ThroughputColumn : IColumn
{
    public string Id => nameof(ThroughputColumn);
    public string ColumnName => "MB/s";
    public string Legend => "Throughput in megabytes per second (decimal MB).";
    public UnitType UnitType => UnitType.Dimensionless;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkDotNet.Running.BenchmarkCase b) => false;

    public string GetValue(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        // Primary: [Params] int PayloadSize on the benchmark class.
        // Fallback: ZipPayloadBenchmarks.PayloadBytes for fixed-payload benchmarks.
        var sizeParam = benchmarkCase.Parameters.Items
            .FirstOrDefault(static p => string.Equals(p.Name, "PayloadSize", StringComparison.Ordinal));
        var size = sizeParam?.Value is int s and > 0 ? s : ZipPayloadBenchmarks.PayloadBytes;
        if (size <= 0)
            return "—";

        var report = summary[benchmarkCase];
        var meanNs = report?.ResultStatistics?.Mean;
        if (meanNs is null or <= 0)
            return "—";

        var mbPerSec = size / (meanNs.Value / 1_000_000_000d) / 1_000_000d;
        return mbPerSec.ToString("F1");
    }
}

/// <summary>
/// Shows the compressed output size as a percentage of the original input size for
/// compress benchmarks (e.g. 57.3 % means the output is 57.3 % of the input).
/// Reads from <see cref="ZipPayloadBenchmarks.CompressionRatios"/>, which is computed
/// once at class-load time so it is available in the main BenchmarkDotNet process when
/// the summary table is rendered.
/// </summary>
// ReSharper disable once MemberCanBeInternal
public sealed class CompressionRatioColumn : IColumn
{
    public string Id => nameof(CompressionRatioColumn);
    public string ColumnName => "CmpRatio";
    public string Legend => "Compressed size as a percentage of the original input (lower = better compression).";
    public UnitType UnitType => UnitType.Dimensionless;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 1;
    public bool IsNumeric => false;
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkDotNet.Running.BenchmarkCase b) => false;

    public string GetValue(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var desc = benchmarkCase.Descriptor.WorkloadMethodDisplayInfo;
        return ZipPayloadBenchmarks.CompressionRatios.TryGetValue(desc, out var ratio)
            ? $"{ratio:F1}%"
            : "—";
    }
}
