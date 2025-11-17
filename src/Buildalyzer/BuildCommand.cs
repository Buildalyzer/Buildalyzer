using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;

namespace Buildalyzer;

/// <summary>Represents a (commandline) build command.</summary>
[DebuggerDisplay("{Command} {ToString()}")]
public sealed class BuildCommand(
    string? command,
    params BuildArgument?[] args)
{
    /// <summary>The command.</summary>
    public string Command { get; } = command ?? "dotnet";

    /// <summary>The agruments for the command.</summary>
    public ImmutableArray<BuildArgument> Arguments { get; } = [.. args.OfType<BuildArgument>()];

    /// <inheritdoc />
    [Pure]
    public override string ToString() => string.Join(' ', Arguments);

    /// <summary>Creates a new build command.</summary>
    [Pure]
    public static BuildCommand Create(
        BuildEnvironment env,
        IOPath projectFile,
        ImmutableArray<BuildCommandProperty> properties,
        LoggerConfiguration logging)
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
                BuildArgument.Target(env.TargetsToBuild),
                BuildArgument.Property(isDotNet, properties),
                BuildArgument.Logger(isDotNet, logging),
                env.NoAutoResponse ? BuildArgument.NoAutoResponse : null,
                BuildArgument.Path(projectFile),
            ]);
    }
}
