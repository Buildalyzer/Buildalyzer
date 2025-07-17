using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.TestTools;
using FluentAssertions;
using Shouldly;

namespace Buildalyzer.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class SimpleProjectsFixture
{
    // Places the log file in C:/Temp
    private const bool BinaryLog = false;

    private static readonly EnvironmentPreference[] Preferences =
    [
#if Is_Windows
        EnvironmentPreference.Framework,
#endif
        EnvironmentPreference.Core
    ];

    private static readonly string[] ProjectFiles =
    [
#if Is_Windows
        @"LegacyFrameworkProject\LegacyFrameworkProject.csproj",
        @"LegacyFrameworkProjectWithReference\LegacyFrameworkProjectWithReference.csproj",
        @"LegacyFrameworkProjectWithPackageReference\LegacyFrameworkProjectWithPackageReference.csproj",
        @"SdkFrameworkProject\SdkFrameworkProject.csproj",
        @"SdkMultiTargetingProject\SdkMultiTargetingProject.csproj",
#endif
        @"SdkNetCore2Project\SdkNetCore2Project.csproj",
        @"SdkNetCore31Project\SdkNetCore31Project.csproj",
        @"SdkNet5Project\SdkNet5Project.csproj",
        @"SdkNet6Project\SdkNet6Project.csproj",
        @"SdkNet6Exe\SdkNet6Exe.csproj",
        @"SdkNet6SelfContained\SdkNet6SelfContained.csproj",
        @"SdkNet6ImplicitUsings\SdkNet6ImplicitUsings.csproj",
        @"SdkNet7Project\SdkNet7Project.csproj",
        @"SdkNet8CS12FeaturesProject\SdkNet8CS12FeaturesProject.csproj",
        @"SdkNet8Alias\SdkNet8Alias.csproj",
        @"SdkNetCore2ProjectImport\SdkNetCore2ProjectImport.csproj",
        @"SdkNetCore2ProjectWithReference\SdkNetCore2ProjectWithReference.csproj",
        @"SdkNetCore2ProjectWithImportedProps\SdkNetCore2ProjectWithImportedProps.csproj",
        @"SdkNetCore2ProjectWithAnalyzer\SdkNetCore2ProjectWithAnalyzer.csproj",
        @"SdkNetStandardProject\SdkNetStandardProject.csproj",
        @"SdkNetStandardProjectImport\SdkNetStandardProjectImport.csproj",
        @"SdkNetStandardProjectWithPackageReference\SdkNetStandardProjectWithPackageReference.csproj",
        @"SdkNetStandardProjectWithConstants\SdkNetStandardProjectWithConstants.csproj",
        @"ResponseFile\ResponseFile.csproj",

        // Using Buildalyzer against Functions projects is currently not supported
        // the Functions build tooling does some extra compilation and magic that
        // doesn't work with the default targets Buildlyzer sets (especially for design time builds)
        // In general, Buildalyzer is not good at analyzing any project that makes extensive use
        // of custom build tooling and tasks/targets because the behavior and log output is not consistent
        // See https://github.com/daveaglick/Buildalyzer/issues/210
        // @"FunctionApp\FunctionApp.csproj",
    ];

    [Test]
    public void Builds_DesignTime(
        [ValueSource(nameof(Preferences))] EnvironmentPreference preference,
        [ValueSource(nameof(ProjectFiles))] string projectFile)
    {
        using var ctx = Context.ForProject(projectFile);

        var options = new EnvironmentOptions
        {
            Preference = preference,
            DesignTime = true,
        };

        var results = ctx.Analyzer.Build(options);

        results.Should().NotBeEmpty();
        results.OverallSuccess.Should().BeTrue();
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeTrue());
    }

    [Test]
    public void Collects_BuildEventArguments()
    {
        using var ctx = Context.ForProject(@"SdkNet6Project\SdkNet6Project.csproj");

        var results = ctx.Analyzer.Build(new EnvironmentOptions());

        results.BuildEventArguments.Should().HaveCount(18);

        var summeries = BuildEventHandlers.Default.Handle(results.BuildEventArguments);

        var summary = summeries.Single(s => s.SourceFiles.Any());
        var result = results.Single();

        summary.Should().BeEquivalentTo(
        new
        {
            result.TargetFramework,
            result.Succeeded,
            SourceFiles = result.SourceFiles.Select(IOPath.Parse).ToImmutableArray(),
            AdditionalFiles = result.AdditionalFiles.Select(IOPath.Parse).ToImmutableArray(),
        });
    }

    [Test]
    public void Collects_Build_with_errors()
    {
        using var ctx = Context.ForProject(@"BuildWithError\BuildWithError.csproj");
        var results = ctx.Analyzer.Build(new EnvironmentOptions { DesignTime = false });
        var summeries = BuildEventHandlers.Default.Handle(results.BuildEventArguments);
        var summary = summeries.Single(s => s.SourceFiles.Any());
        var result = results.Single();

        summary.Should().BeEquivalentTo(
        new
        {
            result.TargetFramework,
            result.Succeeded,
            SourceFiles = result.SourceFiles.Select(IOPath.Parse).ToImmutableArray(),
            AdditionalFiles = result.AdditionalFiles.Select(IOPath.Parse).ToImmutableArray(),
            Errors = new { Count = 3 },
        });
    }

    [Test]
    public void BuildsProject(
        [ValueSource(nameof(Preferences))] EnvironmentPreference preference,
        [ValueSource(nameof(ProjectFiles))] string projectFile)
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference,
            DesignTime = false
        };

        // When
        DeleteProjectDirectory(projectFile, "obj");
        DeleteProjectDirectory(projectFile, "bin");
        IAnalyzerResults results = analyzer.Build(options);

        // Then
        results.Count.ShouldBeGreaterThan(0, log.ToString());
        results.OverallSuccess.ShouldBeTrue(log.ToString());
        results.ShouldAllBe(x => x.Succeeded, log.ToString());
    }

    [Test]
    public void GetsSourceFiles(
        [ValueSource(nameof(Preferences))] EnvironmentPreference preference,
        [ValueSource(nameof(ProjectFiles))] string projectFile)
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference
        };

        // When
        IAnalyzerResults results = analyzer.Build(options);

        // Then
        // If this is the multi-targeted project, use the net462 target
        IReadOnlyList<string> sourceFiles = results.Count == 1 ? results.First().SourceFiles : results["net462"].SourceFiles;
        sourceFiles.ShouldNotBeNull(log.ToString());
        new[]
        {
            "AssemblyAttributes",
            analyzer.ProjectFile.OutputType?.Equals("exe", StringComparison.OrdinalIgnoreCase) ?? false ? "Program" : "Class1",
            "AssemblyInfo"
        }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
    }

    [Test]
    public void GetsReferences(
        [ValueSource(nameof(Preferences))] EnvironmentPreference preference,
        [ValueSource(nameof(ProjectFiles))][NotNull] string projectFile)
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference
        };

        // When
        IAnalyzerResults results = analyzer.Build(options);
        IEnumerable<string> references = results.SelectMany(r => r.References.Select(Path.GetFileName));

        // Then
        references.Should().Contain("mscorlib.dll", because: log.ToString());

        if (projectFile.Contains("PackageReference"))
        {
            references.Should().Contain("NodaTime.dll", because: log.ToString());
        }
    }

    [Test]
    public void GetsSourceFilesFromBinaryLog(
        [ValueSource(nameof(Preferences))] EnvironmentPreference preference,
        [ValueSource(nameof(ProjectFiles))] string projectFile)
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference
        };
        string binLogPath = Path.ChangeExtension(Path.GetTempFileName(), ".binlog");
        analyzer.AddBinaryLogger(binLogPath);

        try
        {
            // When
            analyzer.Build(options);
            IAnalyzerResults results = analyzer.Manager.Analyze(binLogPath);

            // Then
            // If this is the multi-targeted project, use the net462 target
            IReadOnlyList<string> sourceFiles = results.Count == 1 ? results.First().SourceFiles : results["net462"].SourceFiles;
            sourceFiles.ShouldNotBeNull(log.ToString());
            new[]
            {
            "AssemblyAttributes",
            analyzer.ProjectFile.OutputType?.Equals("exe", StringComparison.OrdinalIgnoreCase) ?? false ? "Program" : "Class1",
            "AssemblyInfo"
            }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
        }
        finally
        {
            if (File.Exists(binLogPath))
            {
                File.Delete(binLogPath);
            }
        }
    }

    [Test]
    [Platform("win")]
    public void WpfControlLibraryGetsSourceFiles()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"WpfCustomControlLibrary1\WpfCustomControlLibrary1.csproj", log);

        // When
        IAnalyzerResults results = analyzer.Build();

        // Then
        IReadOnlyList<string> sourceFiles = results.SingleOrDefault()?.SourceFiles;
        sourceFiles.ShouldNotBeNull(log.ToString());
        new[]
        {
            "CustomControl1.cs",
            "AssemblyInfo.cs",
            "Resources.Designer.cs",
            "Settings.Designer.cs",
            "GeneratedInternalTypeHelper.g.cs"
        }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x)), log.ToString());
    }

    [Test]
    [Platform("win")]
    public void AzureFunctionSourceFiles()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"AzureFunctionProject\AzureFunctionProject.csproj", log);

        // When
        IAnalyzerResults results = analyzer.Build();

        // Then
        IReadOnlyList<string> sourceFiles = results.SingleOrDefault()?.SourceFiles;
        sourceFiles.ShouldNotBeNull(log.ToString());
        new[]
        {
            "Program",
            "TestFunction",
            "AssemblyAttributes",
            "AssemblyInfo"
        }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
    }

    [Test]
    public void MultiTargetingBuildAllTargetFrameworksGetsSourceFiles()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkMultiTargetingProject\SdkMultiTargetingProject.csproj", log);

        // When
        IAnalyzerResults results = analyzer.Build();

        // Then
        // Multi-targeting projects product an extra result with an empty target framework that holds some MSBuild properties (I.e. the "outer" build)
        results.Count.ShouldBe(3);
        results.TargetFrameworks.ShouldBe(new[] { "net462", "netstandard2.0", string.Empty }, ignoreOrder: false, log.ToString());
        results[string.Empty].SourceFiles.ShouldBeEmpty();
        new[]
        {
            "AssemblyAttributes",
            "Class1",
            "AssemblyInfo"
        }.ShouldBeSubsetOf(results["net462"].SourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
        new[]
        {
            "AssemblyAttributes",
            "Class2",
            "AssemblyInfo"
        }.ShouldBeSubsetOf(results["netstandard2.0"].SourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
    }

    [Test]
    public void SolutionDirShouldEndWithDirectorySeparator()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkMultiTargetingProject\SdkMultiTargetingProject.csproj", log);

        analyzer.SolutionDirectory.ShouldEndWith(Path.DirectorySeparatorChar.ToString());
    }

    [Test]
    public void MultiTargetingBuildFrameworkTargetFrameworkGetsSourceFiles()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkMultiTargetingProject\SdkMultiTargetingProject.csproj", log);

        // When
        IAnalyzerResults results = analyzer.Build("net462");

        // Then
        IReadOnlyList<string> sourceFiles = results.First(x => x.TargetFramework == "net462").SourceFiles;
        sourceFiles.ShouldNotBeNull(log.ToString());
        new[]
        {
            "AssemblyAttributes",
            "Class1",
            "AssemblyInfo"
        }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
    }

    [Test]
    public void MultiTargetingBuildCoreTargetFrameworkGetsSourceFiles()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkMultiTargetingProject\SdkMultiTargetingProject.csproj", log);

        // When
        IAnalyzerResults results = analyzer.Build("netstandard2.0");

        // Then
        IReadOnlyList<string> sourceFiles = results.First(x => x.TargetFramework == "netstandard2.0").SourceFiles;
        sourceFiles.ShouldNotBeNull(log.ToString());
        new[]
        {
            "AssemblyAttributes",
            "AssemblyInfo",
            "Class2"
        }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
    }

    [Test]
    public void SdkProjectWithPackageReferenceGetsReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkNetStandardProjectWithPackageReference\SdkNetStandardProjectWithPackageReference.csproj", log);

        // When
        IReadOnlyList<string> references = analyzer.Build().First().References;

        // Then
        references.ShouldNotBeNull(log.ToString());
        references.ShouldContain(x => x.EndsWith("NodaTime.dll"), log.ToString());
    }

    [Test]
    public void SdkProjectWithPackageReferenceGetsPackageReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkNetStandardProjectWithPackageReference\SdkNetStandardProjectWithPackageReference.csproj", log);

        // When
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> packageReferences = analyzer.Build().First().PackageReferences;

        // Then
        packageReferences.ShouldNotBeNull(log.ToString());
        packageReferences.Keys.ShouldContain("NodaTime", log.ToString());
    }

    [Test]
    public void SdkProjectWithProjectReferenceGetsReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkNetCore2ProjectWithReference\SdkNetCore2ProjectWithReference.csproj", log);

        // When
        IEnumerable<string> references = analyzer.Build().First().ProjectReferences;

        // Then
        references.ShouldNotBeNull(log.ToString());
        references.ShouldContain(x => x.EndsWith("SdkNetStandardProjectWithPackageReference.csproj"), log.ToString());
        references.ShouldContain(x => x.EndsWith("SdkNetStandardProject.csproj"), log.ToString());
    }

    [Test]
    public void SdkProjectWithDefineContstantsGetsPreprocessorSymbols()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"SdkNetStandardProjectWithConstants\SdkNetStandardProjectWithConstants.csproj", log);

        // When
        IEnumerable<string> preprocessorSymbols = analyzer.Build().First().PreprocessorSymbols;

        // Then
        preprocessorSymbols.ShouldNotBeNull(log.ToString());
        preprocessorSymbols.ShouldContain("DEF2", log.ToString());
        preprocessorSymbols.ShouldContain("NETSTANDARD2_0", log.ToString());

        // If this test runs on .NET 5 or greater, the NETSTANDARD2_0_OR_GREATER preprocessor symbol should be added. Can't test on lower SDK versions

#if NETSTANDARD2_0_OR_GREATER
        preprocessorSymbols.ShouldContain("NETSTANDARD2_0_OR_GREATER", log.ToString());
#endif
    }

    [Test]
    [Platform("win")]
    public void LegacyFrameworkProjectWithPackageReferenceGetsReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"LegacyFrameworkProjectWithPackageReference\LegacyFrameworkProjectWithPackageReference.csproj", log);

        // When
        IReadOnlyList<string> references = analyzer.Build().First().References;

        // Then
        references.ShouldNotBeNull(log.ToString());
        references.ShouldContain(x => x.EndsWith("NodaTime.dll"), log.ToString());
    }

    [Test]
    public void LegacyFrameworkProjectWithPackageReferenceGetsPackageReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"LegacyFrameworkProjectWithPackageReference\LegacyFrameworkProjectWithPackageReference.csproj", log);

        // When
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> packageReferences = analyzer.Build().First().PackageReferences;

        // Then
        packageReferences.ShouldNotBeNull(log.ToString());
        packageReferences.Keys.ShouldContain("NodaTime", log.ToString());
    }

    [Test]
    public void LegacyFrameworkProjectWithProjectReferenceGetsReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"LegacyFrameworkProjectWithReference\LegacyFrameworkProjectWithReference.csproj", log);

        // When
        IEnumerable<string> references = analyzer.Build().First().ProjectReferences;

        // Then
        references.ShouldNotBeNull(log.ToString());
        references.ShouldContain(x => x.EndsWith("LegacyFrameworkProject.csproj"), log.ToString());
        references.ShouldContain(x => x.EndsWith("LegacyFrameworkProjectWithPackageReference.csproj"), log.ToString());
    }

    [Test]
    public void GetsProjectsInSolution()
    {
        // Given
        StringWriter log = new StringWriter();

        // When
        AnalyzerManager manager = new AnalyzerManager(
            GetProjectPath("TestProjects.sln"),
            new AnalyzerManagerOptions
            {
                LogWriter = log
            });

        // Then
        ProjectFiles.Select(x => GetProjectPath(x)).ShouldBeSubsetOf(manager.Projects.Keys, log.ToString());
    }

    [Test]
    public void FiltersProjectsInSolution()
    {
        // Given
        StringWriter log = new StringWriter();

        // When
        AnalyzerManager manager = new AnalyzerManager(
            GetProjectPath("TestProjects.sln"),
            new AnalyzerManagerOptions
            {
                LogWriter = log,
                ProjectFilter = x => x.AbsolutePath.Contains("Core")
            });

        // Then
        ProjectFiles.Select(x => GetProjectPath(x)).Where(x => x.Contains("Core")).ShouldBe(manager.Projects.Keys, true, log.ToString());
    }

    [Test]
    public void IgnoreSolutionItemsThatAreNotProjects()
    {
        // Given / When
        AnalyzerManager manager = new AnalyzerManager(GetProjectPath("TestProjects.sln"));

        // Then
        manager.Projects.Any(x => x.Value.ProjectFile.Path.Contains("TestEmptySolutionFolder")).ShouldBeFalse();
    }

    [Test]
    public void GetsProjectGuidFromSolution([ValueSource(nameof(Preferences))] EnvironmentPreference preference)
    {
        // Given
        AnalyzerManager manager = new AnalyzerManager(
            GetProjectPath("TestProjects.sln"));
        IProjectAnalyzer analyzer = manager.Projects.First(x => x.Key.EndsWith("SdkNetStandardProject.csproj")).Value;
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference
        };

        // When
        DeleteProjectDirectory(analyzer.ProjectFile.Path, "obj");
        DeleteProjectDirectory(analyzer.ProjectFile.Path, "bin");
        IAnalyzerResults results = analyzer.Build(options);

        // Then
        results.First().ProjectGuid.ToString().ShouldBe("016713d9-b665-4272-9980-148801a9b88f");
    }

    [Test]
    public void GetsProjectGuidFromProject([ValueSource(nameof(Preferences))] EnvironmentPreference preference)
    {
        // Given
        const string projectFile = @"SdkNetCore2Project\SdkNetCore2Project.csproj";
        IProjectAnalyzer analyzer = new AnalyzerManager()
            .GetProject(GetProjectPath(projectFile));
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference
        };

        // When
        DeleteProjectDirectory(projectFile, "obj");
        DeleteProjectDirectory(projectFile, "bin");
        IAnalyzerResults results = analyzer.Build(options);

        // Then
        // The generated GUIDs are based on subpath and can also change between MSBuild versions,
        // so this may need to be updated periodically
        results.First().ProjectGuid.ToString().ShouldBe("1ff50b40-c27b-5cea-b265-29c5436a8a7b");
    }

    [Test]
    public void BuildsProjectWithoutLogger([ValueSource(nameof(Preferences))] EnvironmentPreference preference)
    {
        // Given
        const string projectFile = @"SdkNetCore2Project\SdkNetCore2Project.csproj";
        IProjectAnalyzer analyzer = new AnalyzerManager()
            .GetProject(GetProjectPath(projectFile));
        EnvironmentOptions options = new EnvironmentOptions
        {
            Preference = preference
        };

        // When
        DeleteProjectDirectory(projectFile, "obj");
        DeleteProjectDirectory(projectFile, "bin");
        IAnalyzerResults results = analyzer.Build(options);

        // Then
        results.Count.ShouldBeGreaterThan(0);
        results.OverallSuccess.ShouldBeTrue();
        results.ShouldAllBe(x => x.Succeeded);
    }

    [Test]
    public void BuildsFSharpProject()
    {
        // Given
        const string projectFile = @"FSharpProject\FSharpProject.fsproj";
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);

        // When
        DeleteProjectDirectory(projectFile, "obj");
        DeleteProjectDirectory(projectFile, "bin");
        IAnalyzerResults results = analyzer.Build();

        // Then
        results.Count.ShouldBeGreaterThan(0, log.ToString());
        results.First().SourceFiles.ShouldNotBeNull();
        results.OverallSuccess.ShouldBeTrue(log.ToString());
        results.ShouldAllBe(x => x.Succeeded, log.ToString());
    }

    [Test]
    public void BuildsVisualBasicProject()
    {
        // Given
        const string projectFile = @"VisualBasicProject\VisualBasicNetConsoleApp.vbproj";
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);

        // When
        DeleteProjectDirectory(projectFile, "obj");
        DeleteProjectDirectory(projectFile, "bin");
        IAnalyzerResults results = analyzer.Build();

        // Then
        results.Count.ShouldBeGreaterThan(0, log.ToString());
        results.OverallSuccess.ShouldBeTrue(log.ToString());
        results.ShouldAllBe(x => x.Succeeded, log.ToString());

        IAnalyzerResult result = results.First();
        result.PackageReferences.Count.ShouldBeGreaterThan(0);
        result.PackageReferences.ShouldContain(x => x.Key == "BouncyCastle.NetCore");
        result.SourceFiles.Length.ShouldBeGreaterThan(0);
        result.SourceFiles.ShouldContain(x => x.Contains("Program.vb"));
        result.References.Length.ShouldBeGreaterThan(0);
        result.References.ShouldContain(x => x.Contains("BouncyCastle.Crypto.dll"));
    }

    [Test]
    public void BuildsLotsOfProjects()
    {
        // Given
        StringWriter log = new StringWriter();
        AnalyzerManager manager = new AnalyzerManager(
            GetProjectPath(@"LotsOfProjects\LotsOfProjects.sln"),
            new AnalyzerManagerOptions
            {
                LogWriter = log
            });
        List<IProjectAnalyzer> projects = manager.Projects.Values.ToList();

        // When
        List<IAnalyzerResults> analyzerResults = projects
            .AsParallel()
            .Select(x => x.Build())
            .ToList();

        // Then
        analyzerResults.Count.ShouldBe(50);
    }

    // To produce different versions, create a global.json and then run `dotnet clean` and `dotnet build -bl:SdkNetCore31Project-vX.binlog` from the source project folder
    [TestCase("SdkNetCore31Project-v9.binlog", 9)]
    [TestCase("SdkNetCore31Project-v14.binlog", 14)]
    public void GetsSourceFilesFromBinLogFile(string path, int expectedVersion)
    {
        // Verify this is the expected version
        path = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(typeof(SimpleProjectsFixture).Assembly.Location),
                "..",
                "..",
                "..",
                "..",
                "binlogs",
                path))
            .Replace('\\', Path.DirectorySeparatorChar);
        EnvironmentOptions options = new EnvironmentOptions();
        using (Stream stream = File.OpenRead(path))
        {
            using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
            {
                using (BinaryReader reader = new BinaryReader(gzip))
                {
                    reader.ReadInt32().ShouldBe(expectedVersion);
                }
            }
        }

        // Given
        StringWriter log = new StringWriter();
        AnalyzerManager analyzerManager = new AnalyzerManager(
            new AnalyzerManagerOptions
            {
                LogWriter = log
            });

        // When
        IAnalyzerResults analyzerResults = analyzerManager.Analyze(path);
        IReadOnlyList<string> sourceFiles = analyzerResults.First().SourceFiles;

        // Then
        sourceFiles.ShouldNotBeNull(log.ToString());
        new[]
        {
        "AssemblyAttributes",
        "Class1",
        "AssemblyInfo"
        }.ShouldBeSubsetOf(sourceFiles.Select(x => Path.GetFileName(x).Split('.').TakeLast(2).First()), log.ToString());
    }

    [Test]
    public static void DuplicateProjectReferences()
    {
        // Given
        StringWriter log = new StringWriter();
        AnalyzerManager manager = new AnalyzerManager(
            GetProjectPath(@"DuplicateProjectReferences\MainProject\MainProject.sln"),
            new AnalyzerManagerOptions
            {
                LogWriter = log
            });
        List<IProjectAnalyzer> projects = manager.Projects.Values.ToList();

        // When
        List<IAnalyzerResults> analyzerResults = projects
            .AsParallel()
            .Select(x => x.Build())
            .ToList();

        // Then
        analyzerResults.ForEach(v =>
        {
            IAnalyzerResult result = v.Results.FirstOrDefault();
            result.ProjectReferences.Count().ShouldBeLessThanOrEqualTo(1);
        });
    }

    [Test]
    public void Resolves_additional_files_for_Razor_project()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"RazorClassLibraryTest\RazorClassLibraryTest.csproj", log);

        // When + then
        analyzer.Build().First().AdditionalFiles.Select(Path.GetFileName)
            .Should().BeEquivalentTo(
            "_Imports.razor",
            "Component1.razor");
    }

    [Test]
    public void Resolves_additional_files()
    {
        // Given
        StringWriter log = new StringWriter();
        IProjectAnalyzer analyzer = GetProjectAnalyzer(@"ProjectWithAdditionalFile\ProjectWithAdditionalFile.csproj", log);

        // When + then
        analyzer.Build().First().AdditionalFiles.Select(Path.GetFileName)
            .Should().BeEquivalentTo("message.txt");
    }

    private static IProjectAnalyzer GetProjectAnalyzer(string projectFile, System.IO.StringWriter log)
    {
        IProjectAnalyzer analyzer = new AnalyzerManager(
            new AnalyzerManagerOptions
            {
                LogWriter = log
            })
            .GetProject(GetProjectPath(projectFile));

#pragma warning disable 0162
        if (BinaryLog)
        {
            analyzer.AddBinaryLogger(Path.Combine(@"C:\Temp\", Path.ChangeExtension(Path.GetFileName(projectFile), ".core.binlog")));
        }
#pragma warning restore 0162

        return analyzer;
    }

    private static string GetProjectPath(string file)
    {
        string path = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(typeof(SimpleProjectsFixture).Assembly.Location),
                "..",
                "..",
                "..",
                "..",
                "projects",
                file));

        return path.Replace('\\', Path.DirectorySeparatorChar);
    }

    private static void DeleteProjectDirectory(string projectFile, string directory)
    {
        string path = Path.Combine(Path.GetDirectoryName(GetProjectPath(projectFile)), directory);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}