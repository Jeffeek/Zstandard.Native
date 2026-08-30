using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNetVisualizer;

namespace Zstandard.Benchmarks;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        foreach (var summary in summaries)
            await SaveVisualizationAsync(summary);
    }

    private static Task SaveVisualizationAsync(Summary summary)
    {
        var benchType = summary.BenchmarksCases.FirstOrDefault()?.Descriptor.Type;
        if (benchType is null)
            return Task.CompletedTask;

        if (benchType == typeof(CompressionBenchmarks))
            return SaveCompressionReportAsync(summary);

        if (benchType == typeof(ZipPayloadBenchmarks))
            return SaveZipReportAsync(summary);

        return Task.CompletedTask;
    }

    private static Task SaveCompressionReportAsync(Summary summary)
    {
        // Pivot JIT vs AOT side-by-side, grouped by Method + params.
        var options = new JoinReportHtmlOptions
        {
            Title = "Zstandard.Native — JIT vs NativeAOT (.NET 10)",
            MainColumn = "Method",
            GroupByColumns = ["Method"],
            PivotColumn = "Job",
            StatisticColumns = ["Mean", "Allocated"],
            ColumnsOrder = ["JIT 10.0", "AOT 10.0"],
            DividerMode = RenderTableDividerMode.SeparateTables,
            HtmlWrapMode = HtmlDocumentWrapMode.RichDataTables,
            Theme = Theme.Dark
        };

        return summary.JoinReportsAndSaveAsHtmlAndImageAsync(
            DirectoryHelper.GetPathRelativeToProjectDirectory(@"Reports\compression-dark.html"),
            DirectoryHelper.GetPathRelativeToProjectDirectory(@"Reports\compression-dark.png"),
            options);
    }

    private static Task SaveZipReportAsync(Summary summary)
    {
        var options = new ReportHtmlOptions
        {
            Title = "Zstd Library Comparison — ZIP payload, max level (JIT .NET 10)",
            GroupByColumns = ["Categories"],
            SpectrumColumns = ["Mean", "Allocated"],
            DividerMode = RenderTableDividerMode.SeparateTables,
            HtmlWrapMode = HtmlDocumentWrapMode.RichDataTables,
            Theme = Theme.Dark
        };

        return summary.SaveAsHtmlAndImageAsync(
            DirectoryHelper.GetPathRelativeToProjectDirectory(@"Reports\zip-comparison-dark.html"),
            DirectoryHelper.GetPathRelativeToProjectDirectory(@"Reports\zip-comparison-dark.png"),
            options);
    }
}
