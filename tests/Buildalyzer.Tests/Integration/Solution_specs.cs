using Buildalyzer.Environment;
using Buildalyzer.TestTools;

namespace Solution_specs;

public class Resolves
{
    private static readonly EnvironmentPreference[] Preferences =
   [
#if Is_Windows
       EnvironmentPreference.Framework,
#endif
        EnvironmentPreference.Core
   ];

    [Test]
    public void Project_GUID_from_SLN([ValueSource(nameof(Preferences))] EnvironmentPreference preference)
    {
        using var ctx = Context.ForSolution("TestProjects.sln");

        ctx.Manager.Projects.Should().HaveCount(30);

        var analyzer = ctx.Manager.Projects.First(x => x.Key.EndsWith("SdkNetStandardProject.csproj")).Value;

        var results = analyzer.Build(new EnvironmentOptions { Preference = preference });

        results.Single().ProjectGuid.Should().Be("016713d9-b665-4272-9980-148801a9b88f");
    }
}
