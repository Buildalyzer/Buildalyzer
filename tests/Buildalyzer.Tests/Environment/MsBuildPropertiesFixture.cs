using Buildalyzer.Environment;

namespace Buildalyzer.Tests.Environment;

public class MsBuildPropertiesFixture
{
    [Test]
    public void Provides_DesignTime_properties()
    {
        MsBuildProperties.DesignTime.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildingProject"] = "false",
            ["BuildProjectReferences"] = "false",
            ["SkipCompilerExecution"] = "true",
            ["DisableRarCache"] = "true",
            ["AutoGenerateBindingRedirects"] = "false",
            ["CopyBuildOutputToOutputDirectory"] = "false",
            ["CopyOutputSymbolsToOutputDirectory"] = "false",
            ["CopyDocumentationFileToOutputDirectory"] = "false",
            ["ComputeNETCoreBuildOutputFiles"] = "false",
            ["SkipCopyBuildProduct"] = "true",
            ["AddModules"] = "false",
            ["UseCommonOutputDirectory"] = "true",
            ["GeneratePackageOnBuild"] = "false",
        });
    }
}
