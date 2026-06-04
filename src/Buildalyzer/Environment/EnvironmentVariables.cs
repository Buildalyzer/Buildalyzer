namespace Buildalyzer.Environment;

public static class EnvironmentVariables
{
#pragma warning disable SA1310 // Field names should not contain underscore
#pragma warning disable CA1707 // Remove the underscores from member name
    public const string DOTNET_CLI_UI_LANGUAGE = nameof(DOTNET_CLI_UI_LANGUAGE);
    public const string MSBUILD_EXE_PATH = nameof(MSBUILD_EXE_PATH);
    public const string COREHOST_TRACE = nameof(COREHOST_TRACE);
    public const string DOTNET_HOST_PATH = nameof(DOTNET_HOST_PATH);
    public const string DOTNET_INFO_WAIT_TIME = nameof(DOTNET_INFO_WAIT_TIME);
#pragma warning restore CA1707
#pragma warning restore SA1310 // Field names should not contain underscore
    public const string MSBUILDDISABLENODEREUSE = nameof(MSBUILDDISABLENODEREUSE);
    public const string MSBuildExtensionsPath = nameof(MSBuildExtensionsPath);
    public const string MSBuildSDKsPath = nameof(MSBuildSDKsPath);
    public const string LoggerPathDll = nameof(LoggerPathDll);
}
