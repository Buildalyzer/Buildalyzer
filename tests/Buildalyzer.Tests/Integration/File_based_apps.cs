using System.IO;
using Buildalyzer;
using Buildalyzer.Construction;
using Buildalyzer.Environment;
using Buildalyzer.TestTools;

namespace File_based_apps;

public class Analyzer_Build
{
    [Test]
    public void Analyzes_the_project_the_SDK_generates()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");

        var results = ctx.Analyzer.Build(new EnvironmentOptions());

        results.Should().NotBeEmpty(ctx.Log.ToString());
        results.OverallSuccess.Should().BeTrue(ctx.Log.ToString());
        results.SelectMany(r => r.SourceFiles).Select(Path.GetFileName)
            .Should().Contain("app.cs", because: ctx.Log.ToString());
        results.SelectMany(r => r.References).Select(Path.GetFileName)
            .Should().Contain("NodaTime.dll", because: ctx.Log.ToString());
    }

    [Test]
    public void Compiles_the_entry_point_file()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");

        var results = ctx.Analyzer.Build(new EnvironmentOptions { DesignTime = false });

        results.OverallSuccess.Should().BeTrue(ctx.Log.ToString());
    }

    [Test]
    public void Leaves_no_project_behind()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");

        ctx.Analyzer.Build(new EnvironmentOptions());

        File.Exists(ctx.Analyzer.ProjectFile.Path).Should().BeFalse(ctx.Log.ToString());
        ctx.Location.Directory!.GetFiles("*.csproj").Should().BeEmpty(ctx.Log.ToString());
    }

    [Test]
    public void Replaces_a_project_an_earlier_analysis_left_behind()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");
        string path = ctx.Analyzer.ProjectFile.Path;

        try
        {
            File.WriteAllText(path, "<Project />" + System.Environment.NewLine + FileBasedApp.GeneratedProjectMarker);

            var results = ctx.Analyzer.Build(new EnvironmentOptions());

            results.OverallSuccess.Should().BeTrue(ctx.Log.ToString());
            File.Exists(path).Should().BeFalse(ctx.Log.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Leaves_a_project_it_did_not_generate_alone()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");
        string path = ctx.Analyzer.ProjectFile.Path;

        // FileBasedProgram is the SDK's property, not Buildalyzer's mark: a hand-written project can carry it too.
        const string project = "<Project><PropertyGroup><FileBasedProgram>true</FileBasedProgram></PropertyGroup></Project>";

        try
        {
            File.WriteAllText(path, project);

            ctx.Analyzer.Invoking(a => a.Build(new EnvironmentOptions()))
                .Should().Throw<InvalidOperationException>()
                .WithMessage("*did not generate*");
            File.ReadAllText(path).Should().Be(project);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class Project_file
{
    [Test]
    public void Points_back_at_the_entry_point()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");
        var projectFile = (ProjectFile)ctx.Analyzer.ProjectFile;

        projectFile.IsFileBasedApp.Should().BeTrue();
        projectFile.EntryPointFilePath.Should().Be(ctx.Location.FullName);

        // The name is the app's, not the generated project's: the SDK renamed the project it generates
        // during .NET 10 (app.csproj => app.cs.csproj) and the project GUID is derived from the name.
        projectFile.Name.Should().Be("app.cs");
    }

    [Test]
    public void Carries_the_directives_of_the_entry_point()
    {
        using var ctx = Context.ForProject("FileBasedApp/app.cs");
        var projectFile = ctx.Analyzer.ProjectFile;

        projectFile.UsesSdk.Should().BeTrue();
        projectFile.IsMultiTargeted.Should().BeFalse();
        projectFile.PackageReferences.Should().ContainSingle()
            .Which.Name.Should().Be("NodaTime");
    }
}
