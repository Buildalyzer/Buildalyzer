using System.Globalization;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Collects how long each side of a <see cref="WorkspaceComparison"/> load takes so the two
/// project loaders (Buildalyzer's out-of-process build vs Roslyn's in-process
/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/>) can be compared for performance.
/// </summary>
internal static class LoadTimings
{
    private static readonly object Gate = new();
    private static readonly List<Sample> Samples = [];

    internal readonly record struct Sample(string Test, TimeSpan Buildalyzer, TimeSpan MSBuild);

    public static void Record(string test, TimeSpan buildalyzer, TimeSpan msbuild)
    {
        lock (Gate)
        {
            Samples.Add(new Sample(test, buildalyzer, msbuild));
        }
    }

    /// <summary>Formats the collected samples as a fixed-width comparison table.</summary>
    public static string Report()
    {
        Sample[] samples;
        lock (Gate)
        {
            samples = [.. Samples];
        }

        if (samples.Length == 0)
        {
            return "No differential load timings were recorded.";
        }

        const int nameWidth = 56;
        System.Text.StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine("Differential load timings (Buildalyzer vs MSBuildWorkspace)");
        sb.AppendLine(new string('-', nameWidth + 34));
        sb.AppendLine($"{"Test".PadRight(nameWidth)} {"Buildalyzer",12} {"MSBuild",12} {"Ratio",7}");
        sb.AppendLine(new string('-', nameWidth + 34));

        foreach (Sample s in samples.OrderByDescending(s => s.Buildalyzer))
        {
            sb.AppendLine(Row(s.Test, s.Buildalyzer, s.MSBuild, nameWidth));
        }

        sb.AppendLine(new string('-', nameWidth + 34));

        TimeSpan baTotal = new(samples.Sum(s => s.Buildalyzer.Ticks));
        TimeSpan msTotal = new(samples.Sum(s => s.MSBuild.Ticks));
        TimeSpan baMean = new(baTotal.Ticks / samples.Length);
        TimeSpan msMean = new(msTotal.Ticks / samples.Length);

        sb.AppendLine(Row($"TOTAL ({samples.Length} loads)", baTotal, msTotal, nameWidth));
        sb.AppendLine(Row("MEAN per load", baMean, msMean, nameWidth));

        return sb.ToString();
    }

    private static string Row(string name, TimeSpan buildalyzer, TimeSpan msbuild, int nameWidth)
    {
        string ratio = msbuild.Ticks == 0
            ? "n/a"
            : ((double)buildalyzer.Ticks / msbuild.Ticks).ToString("0.00", CultureInfo.InvariantCulture) + "x";
        return $"{Truncate(name, nameWidth).PadRight(nameWidth)} {Ms(buildalyzer),12} {Ms(msbuild),12} {ratio,7}";
    }

    private static string Ms(TimeSpan value) =>
        value.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms";

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}

/// <summary>
/// Prints the aggregated <see cref="LoadTimings"/> comparison once, after every test in the
/// assembly has run.
/// </summary>
[SetUpFixture]
public class LoadTimingsReport
{
    [OneTimeTearDown]
    public void Report()
    {
        string report = LoadTimings.Report();
        TestContext.Progress.WriteLine(report);

        // The runner does not always surface TestContext.Progress, so also drop the report on
        // disk at a well-known path for retrieval after the run.
        string path = Path.Combine(Path.GetTempPath(), "buildalyzer-differential-timings.txt");
        File.WriteAllText(path, report);
        TestContext.Progress.WriteLine($"Differential timings written to {path}");
    }
}
