using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;

namespace Buildalyzer;

/// <summary>Represents a (commandline) build command.</summary>
[DebuggerDisplay("{Command} {ToString()}")]
internal sealed class BuildCommand(
    string? command,
    params BuildArgument?[] args)
{
    /// <summary>The command.</summary>
    public string Command { get; } = command ?? "dotnet";

    /// <summary>The arguments for the command.</summary>
    public ImmutableArray<BuildArgument> Arguments { get; } = [.. args.OfType<BuildArgument>()];

    /// <inheritdoc />
    [Pure]
    public override string ToString() => string.Join(' ', Arguments);

    /// <summary>Creates a new build command.</summary>
    [Pure]
    public static BuildCommand Create(
        BuildEnvironment env,
        in IOPath projectFile,
        in ImmutableArray<BuildCommandProperty> properties,
        IReadOnlyCollection<string> targetsToBuild,
        LoggerConfiguration logging,
        IReadOnlyCollection<string>? binaryLogArguments = null)
    {
        Guard.NotNull(env);

        var cmd = env.MsBuildExePath;
        var msbuild = IOPath.Parse(env.MsBuildExePath);

        bool isDotNet = false; // false=MSBuild.exe, true=dotnet.exe
        if (msbuild.File() is not { } file || file.Extension.IsMatch(".dll"))
        {
            // in case of no MSBuild path or a path to the MSBuild dll, run dotnet
            cmd = env.DotnetExePath;
            isDotNet = true;
        }

        return new(
            cmd,
            [
                isDotNet && msbuild.HasValue ? BuildArgument.Path(msbuild) : null,
                BuildArgument.NoConsoleLogger,
                .. env.Arguments.Select(BuildArgument.Raw),
                env.Restore ? BuildArgument.Restore : null,
                BuildArgument.Target(targetsToBuild is { Count: > 0 } ? targetsToBuild : env.TargetsToBuild),
                BuildArgument.Property(isDotNet, properties),
                BuildArgument.Logger(isDotNet, logging),

                // MSBuild writes the binary log natively via /bl, so it's a full-fidelity, SDK-version log
                // with no in-process BinaryLogger needed. It is later read by replaying it through MSBuild
                // on the command line (see AnalyzerManager.Analyze) with the pipe logger attached.
                .. (binaryLogArguments ?? []).Select(BuildArgument.Raw),
                env.NoAutoResponse ? BuildArgument.NoAutoResponse : null,
                BuildArgument.Path(projectFile),
            ]);
    }
}
