namespace Buildalyzer.Environment;

public enum EnvironmentPreference
{
    /// <summary>
    /// This will prefer the .NET Core SDK if it's available and will
    /// use the .NET Framework build tools if the project type is known
    /// not to support the .NET Core SDK or the .NET Core SDK can't be found.
    /// </summary>
    Core,

    /// <summary>
    /// This will prefer the .NET Framework build tools if they're available and will
    /// use the .NET Code SDK if the project type is known
    /// not to support the .NET Framework build tools or the .NET Framework build tools can't be found.
    /// </summary>
    Framework
}
