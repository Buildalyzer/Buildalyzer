using Buildalyzer.Environment;

namespace Buildalyzer.Tests.Environment;

[TestFixture]
public class BuildEnvironmentFixture
{
    [TestCase(true)]
    [TestCase(false)]
    public void Keeps_its_own_global_properties_alongside_the_design_time_ones(bool designTime)
    {
        var env = new BuildEnvironment(designTime, restore: true, ["Compile"], "MSBuild.dll", "dotnet", []);

        env.GlobalProperties.Should().Contain(MsBuildProperties.ProvideCommandLineArgs, "true");
        env.GlobalProperties.Should().Contain(MsBuildProperties.GenerateResourceMSBuildArchitecture, "CurrentArchitecture");

        if (designTime)
        {
            env.GlobalProperties.Should().Contain(MsBuildProperties.DesignTime);
        }
        else
        {
            env.GlobalProperties.Should().NotContainKey(MsBuildProperties.DesignTimeBuild);
        }
    }

    [Test]
    public void Keeps_design_time_properties_when_cloned()
    {
        var env = new BuildEnvironment(true, restore: true, ["Compile"], "MSBuild.dll", "dotnet", [])
            .WithRestore(false)
            .WithTargetsToBuild("Build");

        env.GlobalProperties.Should().Contain(MsBuildProperties.ProvideCommandLineArgs, "true");
        env.GlobalProperties.Should().Contain(MsBuildProperties.DesignTime);
    }
}
