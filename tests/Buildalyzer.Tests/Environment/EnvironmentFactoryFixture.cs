using System.IO;
using Buildalyzer.Environment;
using Buildalyzer.TestTools;

namespace Buildalyzer.Tests.Environment;

[TestFixture]
public class EnvironmentFactoryFixture
{
    [Test]
    public void Default_TargetsToBuild_is_Compile_only()
    {
        new EnvironmentOptions().TargetsToBuild.Should().Equal("Compile");
    }

    [Test]
    public void GetBuildEnvironment_sets_NonExistentFile_under_default_targets()
    {
        // Without this workaround CoreCompile gets cached out and Csc never
        // fires its command-line event — see #344. Previously this property
        // was only set when "Clean" was in TargetsToBuild; it now needs to
        // be set unconditionally because "Clean" is no longer the default.
        using var ctx = Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");

        BuildEnvironment env = ctx.Analyzer.EnvironmentFactory.GetBuildEnvironment(new EnvironmentOptions());

        env.GlobalProperties.Should().ContainKey(MsBuildProperties.NonExistentFile);
    }

    [Test]
    public void GetBuildEnvironment_names_the_global_json_that_pins_a_missing_SDK()
    {
        // `dotnet --info` reports no SDK base path here, only the runtimes. The first runtime's directory
        // used to stand in for it, so every build ran a nonexistent shared/Microsoft.AspNetCore.App/MSBuild.dll.
        DirectoryInfo root = Directory.CreateTempSubdirectory("Buildalyzer.MissingSdk.");
        try
        {
            string globalJson = Path.Combine(root.FullName, "global.json");
            File.WriteAllText(globalJson, """{ "sdk": { "version": "2.0.999" } }""");
            string projectPath = Path.Combine(root.FullName, "Project.csproj");
            File.WriteAllText(projectPath, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");

            IProjectAnalyzer analyzer = new AnalyzerManager().GetProject(projectPath)!;

            analyzer.Invoking(a => a.EnvironmentFactory.GetBuildEnvironment(new EnvironmentOptions()))
                .Should().Throw<InvalidOperationException>()
                .WithMessage($"*{globalJson}*not installed*");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // From https://docs.microsoft.com/en-us/dotnet/standard/frameworks
    // .NET Core/.NET 5 and up
    [TestCase("netcoreapp1.0", false)]
    [TestCase("netcoreapp1.1", false)]
    [TestCase("netcoreapp2.0", false)]
    [TestCase("netcoreapp2.1", false)]
    [TestCase("netcoreapp2.2", false)]
    [TestCase("netcoreapp3.0", false)]
    [TestCase("netcoreapp3.1", false)]
    [TestCase("net5", false)] // This isn't an official TFM but we'll check it anyway
    [TestCase("net5.0", false)]
    [TestCase("net5.0-android", false)]
    [TestCase("net5.0-ios", false)]
    [TestCase("net5.0-macos", false)]
    [TestCase("net5.0-tvos", false)]
    [TestCase("net5.0-watchos", false)]
    [TestCase("net5.0-windows", false)]
    [TestCase("net6", false)] // This isn't an official TFM but we'll check it anyway
    [TestCase("net6.0", false)]
    [TestCase("net6.0-android", false)]
    [TestCase("net6.0-ios", false)]
    [TestCase("net6.0-macos", false)]
    [TestCase("net6.0-tvos", false)]
    [TestCase("net6.0-watchos", false)]
    [TestCase("net6.0-windows", false)]

    // Dotted double-digit majors. Note these collide with the packed .NET Framework monikers
    // below ("net10" is Framework 1.0, "net10.0" is .NET 10), so the dot matters.
    [TestCase("net10.0", false)]
    [TestCase("net10.0-windows", false)]
    [TestCase("net11.0", false)]
    [TestCase("net11.0-windows", false)]
    [TestCase("netstandard1.0", false)]
    [TestCase("netstandard1.1", false)]
    [TestCase("netstandard1.2", false)]
    [TestCase("netstandard1.3", false)]
    [TestCase("netstandard1.4", false)]
    [TestCase("netstandard1.5", false)]
    [TestCase("netstandard1.6", false)]
    [TestCase("netstandard2.0", false)]
    [TestCase("netstandard2.1", false)]

    // .NET Framework
    [TestCase("net10", true)]
    [TestCase("net11", true)]
    [TestCase("net20", true)]
    [TestCase("net35", true)]
    [TestCase("net40", true)]
    [TestCase("net403", true)]
    [TestCase("net45", true)]
    [TestCase("net451", true)]
    [TestCase("net452", true)]
    [TestCase("net46", true)]
    [TestCase("net461", true)]
    [TestCase("net462", true)]
    [TestCase("net47", true)]
    [TestCase("net471", true)]
    [TestCase("net472", true)]
    [TestCase("net48", true)]

    // Should treat the more exotic TFMs as .NET Framework
    [TestCase("netcore", true)]
    [TestCase("netcore45", true)]
    [TestCase("netcore451", true)]
    [TestCase("netmf", true)]
    [TestCase("sl4", true)]
    [TestCase("sl5", true)]
    [TestCase("wp", true)]
    [TestCase("wp7", true)]
    [TestCase("wp75", true)]
    [TestCase("wp8", true)]
    [TestCase("wp81", true)]
    [TestCase("wpa81", true)]
    [TestCase("uap", true)]
    [TestCase("uap10.0", true)]
    public void IsFrameworkTargetFrameworkForTfm(string targetFramework, bool expected)
    {
        // Given, When
        bool result = EnvironmentFactory.IsFrameworkTargetFramework(targetFramework);

        // Then
        result.Should().Be(expected);
    }
}
