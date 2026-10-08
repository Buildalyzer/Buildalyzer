using System.IO;
using System.Text.Json;
using Buildalyzer.Construction;
using Buildalyzer.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Buildalyzer.Environment;

/// <summary>Asks the .NET SDK for the project behind a file-based app.</summary>
/// <remarks>
/// The generated project is not something that can be composed here: the SDK expands the file-level
/// directives (<c>#:package</c>, <c>#:sdk</c>, <c>#:property</c>) by evaluating the project it is building
/// up, repeatedly, against a real MSBuild. Roslyn does that in process because its build host already has
/// an MSBuild to evaluate with; Buildalyzer has none by design (see the note in Buildalyzer.csproj), so it
/// asks the SDK instead, over the same hidden <c>dotnet run-api</c> stdin/stdout protocol that IDEs use to
/// see the project behind a file-based program.
/// </remarks>
internal sealed class VirtualProjectResolver(ILoggerFactory? factory)
{
    /// <summary>The <c>run-api</c> contract version this resolver was written against.</summary>
    private const int SupportedVersion = 1;

    /// <summary>
    /// The evaluation is a project load, not a build (no restore, no targets), but it does start a
    /// dotnet host and resolve the SDK, which is slow on a cold machine.
    /// </summary>
    private static readonly TimeSpan WaitTime = TimeSpan.FromMinutes(2);

    private readonly ILogger Logger = (factory ?? NullLoggerFactory.Instance).CreateLogger<VirtualProjectResolver>();

    /// <summary>Resolves the project the SDK would build the file-based app at <paramref name="entryPointFilePath"/> against.</summary>
    [Pure]
    public VirtualProject Resolve(IOPath entryPointFilePath, string dotnetExePath)
    {
        using var document = JsonDocument.Parse(Execute(entryPointFilePath, dotnetExePath));
        var response = document.RootElement;

        if (response.TryGetProperty("Version", out var version)
            && version.TryGetInt32(out int number)
            && number != SupportedVersion)
        {
            Logger.LogWarning(
                "The .NET SDK reports version {Version} of the file-based app API; {Supported} is supported. "
                + "The project generated for {EntryPoint} may not be interpreted correctly.",
                number,
                SupportedVersion,
                entryPointFilePath);
        }

        if (response.TryGetProperty("$type", out var type) && type.GetString() == "Error")
        {
            Logger.LogDebug("{Details}", Text(response, "Details"));
            throw new InvalidOperationException(
                $"The .NET SDK could not generate a project for the file-based app {entryPointFilePath}: {Text(response, "Message")}");
        }

        // Every diagnostic is an error: the SDK collects them through an error reporter and fails
        // `dotnet build app.cs` on them, so the project on offer here is not one worth building.
        if (Diagnostics(response) is { Length: > 0 } diagnostics)
        {
            throw new InvalidOperationException(
                $"The file-based app {entryPointFilePath} could not be loaded:{System.Environment.NewLine}{diagnostics}");
        }

        var content = Text(response, "Content") is { Length: > 0 } project
            ? project
            : throw new InvalidOperationException(
                $"The .NET SDK returned no project for the file-based app {entryPointFilePath}.");

        return new VirtualProject(
            entryPointFilePath,
            ProjectPath(response, entryPointFilePath),
            content,
            dotnetExePath);
    }

    /// <remarks>
    /// The path is the SDK's to choose - it moved from <c>app.csproj</c> to <c>app.cs.csproj</c> during
    /// .NET 10, and <c>ProjectPath</c> was added to the response at the same time - so it is taken from
    /// the response whenever the SDK reports it, and only guessed (at the older convention) when it does
    /// not. Nothing downstream depends on the name, but keeping the SDK's own keeps a project this
    /// analysis leaves behind recognizable to the tooling that would find it.
    /// </remarks>
    [Pure]
    private static IOPath ProjectPath(JsonElement response, IOPath entryPointFilePath)
        => Text(response, "ProjectPath") is { Length: > 0 } path
        ? IOPath.Parse(path)
        : IOPath.Parse(System.IO.Path.ChangeExtension(entryPointFilePath.ToString(), ".csproj"));

    [Pure]
    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

    [Pure]
    private static string Diagnostics(JsonElement response)
    {
        if (!response.TryGetProperty("Diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        // Indexed rather than enumerated: JsonElement's array enumerator is disposable, which the
        // IDisposable analyzer flags even though foreach disposes it.
        List<string> messages = [];
        for (int index = 0; index < diagnostics.GetArrayLength(); index++)
        {
            if (Text(diagnostics[index], "Message") is { Length: > 0 } message)
            {
                messages.Add(message);
            }
        }

        return string.Join(System.Environment.NewLine, messages);
    }

    [Pure]
    private string Execute(IOPath entryPointFilePath, string dotnetExePath)
    {
        // The run-api protocol is a request per line on stdin, a response per line on stdout; closing
        // stdin after the single request is what ends the process. The request is written by hand
        // (JsonEncodedText escapes the path) rather than serialized: JsonSerializer without a type info
        // resolver falls back to reflection, which is off in a trimmed or AOT-published host.
        var request = new StringBuilder()
            .Append("{\"$type\":\"GetProject\",\"EntryPointFileFullPath\":\"")
            .Append(JsonEncodedText.Encode(entryPointFilePath.ToString()))
            .Append("\"}")
            .ToString();

        var environmentVariables = new Dictionary<string, string?>
        {
            // The SDK evaluates the generated project with its own MSBuild. A host that has registered
            // MSBuildLocator points these at its in-process MSBuild instead, which is not the MSBuild
            // this project will be built with either (a null value unsets them, see ProcessRunner).
            [EnvironmentVariables.MSBUILD_EXE_PATH] /*......*/ = null,
            [EnvironmentVariables.MSBuildExtensionsPath] /*.*/ = null,
            [EnvironmentVariables.MSBuildSDKsPath] /*.......*/ = null,
            [EnvironmentVariables.COREHOST_TRACE] /*........*/ = "0",
            [EnvironmentVariables.DOTNET_NOLOGO] /*.........*/ = "1",
        };

        // global.json may select the SDK, so run where the entry point file lives - the same directory
        // the build will run in.
        using var processRunner = new ProcessRunner(
            dotnetExePath,
            "run-api",
            entryPointFilePath.File()!.Directory!.FullName,
            environmentVariables,
            factory,
            standardInput: request);

        processRunner.Start();

        if (!processRunner.WaitForExit((int)WaitTime.TotalMilliseconds))
        {
            // Disposing the runner only closes the handle; a wedged SDK would be left running.
            processRunner.Kill();
            throw new InvalidOperationException(
                $"`{dotnetExePath} run-api` did not respond within {WaitTime.TotalSeconds:0} seconds "
                + $"for the file-based app {entryPointFilePath}.");
        }

        // The first-run experience and other CLI chatter share stdout with the response, so take the
        // line that is JSON rather than the last line written.
        return processRunner.Data.Output.FirstOrDefault(line => line.TrimStart().StartsWith('{'))
            ?? throw new InvalidOperationException(
                $"`{dotnetExePath} run-api` could not load the file-based app {entryPointFilePath}. "
                + "File-based apps require the .NET 10 SDK or later."
                + Message(processRunner.Data.Error));
    }

    [Pure]
    private static string Message(ImmutableArray<string> error)
        => error.IsDefaultOrEmpty
        ? string.Empty
        : System.Environment.NewLine + string.Join(System.Environment.NewLine, error);
}
