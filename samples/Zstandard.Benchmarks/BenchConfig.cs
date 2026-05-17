using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using Perfolizer.Horology;

namespace Zstandard.Benchmarks;

/// <summary>
/// Six-job config: JIT and NativeAOT for .NET 8, .NET 9, and .NET 10.
/// </summary>
public sealed class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.Mean, StatisticColumn.StdDev, StatisticColumn.P95);
        AddColumn(new ThroughputColumn());
        AddVersionGroup("8.0", CoreRuntime.Core80, NativeAotRuntime.Net80);
        AddVersionGroup("9.0", CoreRuntime.Core90, NativeAotRuntime.Net90);
        AddVersionGroup("10.0", CoreRuntime.Core10_0, NativeAotRuntime.Net10_0);

        SummaryStyle = SummaryStyle.Default.WithTimeUnit(TimeUnit.Microsecond);
    }

    private void AddVersionGroup(string version, CoreRuntime coreRuntime, NativeAotRuntime nativeAotRuntime)
    {
        AddJob(Job.Default
            .WithRuntime(coreRuntime)
            .WithId($"JIT {version}"));

        AddJob(Job.Default
            .WithRuntime(nativeAotRuntime)
            .WithId($"AOT {version}"));
    }
}

/// <summary>
/// Reports MB/s based on a <c>[Params]</c>-bound <c>PayloadSize</c> property on the bench class.
/// </summary>
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
        var sizeParam = benchmarkCase.Parameters.Items
            .FirstOrDefault(p => string.Equals(p.Name, "PayloadSize", StringComparison.Ordinal));
        if (sizeParam?.Value is not int size || size <= 0)
        {
            return "—";
        }

        var report = summary[benchmarkCase];
        var meanNs = report?.ResultStatistics?.Mean;
        if (meanNs is null or <= 0)
        {
            return "—";
        }

        var mbPerSec = size / (meanNs.Value / 1_000_000_000d) / 1_000_000d;
        return mbPerSec.ToString("F1");
    }
}
