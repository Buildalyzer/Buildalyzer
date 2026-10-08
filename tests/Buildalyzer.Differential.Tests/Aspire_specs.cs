using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// The Buildalyzer-versus-MSBuildWorkspace comparison for an Aspire AppHost solution: an AppHost
/// (<c>Aspire.AppHost.Sdk</c>) that references an ASP.NET Core API. AppHosts generate project-metadata
/// sources before <c>CoreCompile</c> and run extra validation targets against their project references,
/// so they are a good check that the whole solution - every project, every generated document, every
/// reference - loads the same way through both loaders.
/// </summary>
/// <remarks>
/// Explicit and <c>RealWorld</c>-tagged like the OSS repositories: restoring the AppHost pulls the Aspire
/// dashboard and orchestration packages for the current RID (large, network). Run with
/// <c>dotnet test --filter "FullyQualifiedName~Aspire_specs"</c>.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Explicit("Restores the Aspire AppHost SDK and its dashboard/orchestration packages over the network; slow.")]
[Category("RealWorld")]
public class Aspire_specs
{
    private const string AspireVersion = "13.5.3";

    [Test]
    public async Task AppHost_solution_matches_reference()
    {
        using ProjectFixture fixture = new();
        string root = fixture.Root.FullName;

        string apiPath = Path.Combine(root, "Api", "Api.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(apiPath)!);
        File.WriteAllText(apiPath, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Api", "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/", () => "Hello");
            app.Run();
            """);

        string appHostPath = Path.Combine(root, "AppHost", "AppHost.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(appHostPath)!);
        File.WriteAllText(appHostPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="{AspireVersion}" />
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsAspireHost>true</IsAspireHost>
                <UserSecretsId>buildalyzer-differential-aspire</UserSecretsId>
                <!-- ASPIRE010 warns that the Aspire CLI bundle is not in use; irrelevant to a design-time build. -->
                <NoWarn>$(NoWarn);ASPIRE010</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.AppHost" Version="{AspireVersion}" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Api/Api.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "AppHost", "Program.cs"), """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddProject<Projects.Api>("api");
            builder.Build().Run();
            """);

        string solutionPath = Path.Combine(root, "Aspire.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Project Path="Api/Api.csproj" />
              <Project Path="AppHost/AppHost.csproj" />
            </Solution>
            """);
        fixture.Restore(solutionPath);

        using MSBuildWorkspace msbuild = MSBuildWorkspace.Create();
        List<string> failures = [];
        Solution reference;
        using (msbuild.RegisterWorkspaceFailedHandler(e => failures.Add(e.Diagnostic.Message)))
        {
            reference = await msbuild.OpenSolutionAsync(solutionPath);
        }

        SafeStringWriter log = new();
        AnalyzerManager manager = new(solutionPath, new AnalyzerManagerOptions { LogWriter = log });
        using AdhocWorkspace workspace = manager.GetWorkspace();

        failures.Should().BeEmpty();
        log.ToString().Should().NotContain("No compiler invocation was captured");
        workspace.CurrentSolution.Projects.Select(p => p.Name).Should().BeEquivalentTo(["Api", "AppHost"]);

        // Program.cs, the SDK-generated AssemblyInfo/AssemblyAttributes/GlobalUsings, and Aspire's two
        // generated project-metadata sources.
        workspace.CurrentSolution.Projects.Single(p => p.Name == "AppHost").Documents.Should().HaveCount(6);
        workspace.CurrentSolution.Shape().Should().BeEquivalentTo(reference.Shape(), log.ToString());
    }
}
