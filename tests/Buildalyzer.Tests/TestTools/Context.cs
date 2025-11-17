using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using Buildalyzer.Environment;

namespace Buildalyzer.TestTools;

public abstract class Context : IDisposable
{
    [Pure]
    public static ProjectFileTestContext ForProject(string path) => new(GetProjectPath(path));

    [Pure]
    public static SolutionFileTestContext ForSolution(string path, Action<AnalyzerManagerOptions>? options = null)
        => new(GetProjectPath(path), options);

    private static FileInfo GetProjectPath(string file)
    {
        var location = new FileInfo(typeof(Context).Assembly.Location).Directory!;
        return new FileInfo(Path.Combine(
            location.FullName,
            "..",
            "..",
            "..",
            "..",
            "projects",
            file));
    }

    [Pure]
    public abstract BuildAnalysis Build(EnvironmentOptions? options = null);

    protected Context(FileInfo location, Func<Context, AnalyzerManager> manager)
    {
        Location = location;
        Manager = manager?.Invoke(this)!;
        DebugMode(ref InDebugMode);
    }

    public AnalyzerManager Manager { get; }

    public TextWriter Log => IsDisposed ? throw new ObjectDisposedException(GetType().FullName) : log;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly TextWriter log = new StringWriter();

    public FileInfo Location { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            if (InDebugMode)
            {
                Console.WriteLine(Log.ToString());
            }
            Log.Dispose();

            IsDisposed = true;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool IsDisposed;

    /// <summary>Ensures that the analysis is done ignoring previous results.</summary>
    protected void DeleteSubDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.Combine(Location.Directory!.FullName, path));

        if (directory.Exists)
        {
            try
            {
                directory.Delete(true);
                Log.WriteLine($"Deleted all files at {directory}");
            }
            catch (Exception x)
            {
                Log.WriteLine(x);
            }
        }
    }

    /// <summary>Sets <paramref name="inDebugMode"/> to true when run in DEBUG mode.</summary>
    [Conditional("DEBUG")]
    private void DebugMode(ref bool inDebugMode) => inDebugMode = true;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly bool InDebugMode;
}
