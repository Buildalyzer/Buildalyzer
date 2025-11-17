using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Logging;

namespace BuildCommand_specs;

public class Creates
{
    [Test]
    public void Argument_with_arguments()
    {
        var env = new BuildEnvironment(
            designTime: true,
            restore: true,
            ["Clean", "Build"],
            @"C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            "dotnet",
            []);

        var path = IOPath.Parse("projects/LegacyFrameworkProject/LegacyFrameworkProject.csproj");
        var logger = new LoggerConfiguration
        {
            ClientHandle = "1980",
            LoggerPath = IOPath.Parse("logger/somelogger.dll"),
        };

        var command = BuildCommand.Create(
            env,
            path,
            [
                new("CopyBuildOutputToOutputDirectory", "false"),
                new("ResolveNuGetPackages", "true"),
            ],
            logger);

        command.ToString().Should().Be(
            """
            /noconsolelogger /restore /target:Clean;Build /property:CopyBuildOutputToOutputDirectory="false";ResolveNuGetPackages="true" /l:BuildalyzerLogger,"logger\somelogger.dll";1980;True /noAutoResponse "projects\LegacyFrameworkProject\LegacyFrameworkProject.csproj"
            """);
    }
}
