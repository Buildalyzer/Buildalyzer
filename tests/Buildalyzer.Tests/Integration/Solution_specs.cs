using Buildalyzer.Environment;
using Buildalyzer.TestTools;

namespace Solution_specs;

public class Resolves
{
    [Test]
    public void Project_GUID_from_SLN([ValueSource(nameof(Preferences))] EnvironmentPreference preference)
    {
        using var ctx = Context.ForSolution("TestProjects.sln");

        ctx.Manager.Projects.Should().HaveCount(30);

        var analyzer = ctx.Manager.Projects.First(x => x.Key.EndsWith("SdkNetStandardProject.csproj")).Value;

        var results = analyzer.Build(new EnvironmentOptions { Preference = preference });

        results.Single().ProjectGuid.Should().Be("016713d9-b665-4272-9980-148801a9b88f");
    }

    /// <remarks>
    /// Builds a lot of projects that should all succeed.
    /// </remarks>
    [Test]
    public void Project_builds()
    {
        using var ctx = Context.ForSolution("LotsOfProjects/LotsOfProjects.sln");

        var builds = ctx.Manager.Projects.Values
            .AsParallel()
            .Select(x => x.Build())
            .ToArray();

        builds.Should().HaveCount(50);
        builds.Should().AllSatisfy(b => b.OverallSuccess.Should().BeTrue());
    }

    private static readonly EnvironmentPreference[] Preferences =
    [
#if Is_Windows
       EnvironmentPreference.Framework,
#endif
        EnvironmentPreference.Core
    ];
}

public class Filters
{
    [Test]
    public void Projects()
    {
        using var ctx = Context.ForSolution(
            "TestProjects.sln",
            o => o.ProjectFilter = x => x.AbsolutePath.Contains("Core"));

        ctx.Manager.Projects.Should().HaveCount(6);
    }
}

public class Handles
{

    [Test]
    public static void Duplicate_project_references()
    {
        using var ctx = Context.ForSolution("DuplicateProjectReferences/MainProject/MainProject.sln");

        var results = ctx.Manager.Projects.Values
            .AsParallel()
            .Select(x => x.Build().Single())
            .ToList();

        results.Should().AllSatisfy(
            res => res.ProjectReferences.Should().HaveCountLessThanOrEqualTo(1));
    }

    [Test]
    public void Project_files_only()
    {
        using var ctx = Context.ForSolution("TestProjects.sln");

        ctx.Manager.Projects.Keys.Should().AllSatisfy(
            path => path.Should().NotContain("TestEmptySolutionFolder"));
    }
}
