using System.IO;
using Buildalyzer.Environment;
using Buildalyzer.IO;

namespace Buildalyzer.Tests.Environment;

[TestFixture]
public class DotNetInfoResolverFixture
{
    private DirectoryInfo _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("Buildalyzer.DotNetInfo.");

    [TearDown]
    public void TearDown() => _root.Delete(recursive: true);

    [Test]
    public void Runs_once_for_projects_under_the_same_global_json()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "global.json"), "{}");
        var resolver = new DotNetInfoResolver();

        DotNetInfo first = resolver.Resolve(Project("A"), IOPath.Parse("dotnet"), null);
        DotNetInfo second = resolver.Resolve(Project("B"), IOPath.Parse("dotnet"), null);

        first.BasePath.Should().NotBeNullOrEmpty();
        second.Should().BeSameAs(first);
    }

    [Test]
    public void Runs_again_for_a_project_under_another_global_json()
    {
        var resolver = new DotNetInfoResolver();
        DotNetInfo outside = resolver.Resolve(Project("A"), IOPath.Parse("dotnet"), null);

        IOPath project = Project("B");
        File.WriteAllText(Path.Combine(_root.FullName, "B", "global.json"), "{}");
        DotNetInfo inside = resolver.Resolve(project, IOPath.Parse("dotnet"), null);

        inside.BasePath.Should().NotBeNullOrEmpty();
        inside.Should().NotBeSameAs(outside);
    }

    private IOPath Project(string name)
    {
        DirectoryInfo directory = _root.CreateSubdirectory(name);
        return IOPath.Parse(Path.Combine(directory.FullName, $"{name}.csproj"));
    }
}
