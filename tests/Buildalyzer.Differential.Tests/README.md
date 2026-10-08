# Buildalyzer.Differential.Tests

Differential tests that validate Buildalyzer against Roslyn's own project loader.

Each test:

1. **Authors** a real SDK-style project on disk with
   [`MSBuild.ProjectCreation`](https://github.com/jeffkl/MSBuildProjectCreation) in a
   throw-away temp directory (outside the repo, so it inherits none of the repo's
   `Directory.Build.*` or analyzer configuration).
2. **Restores** it so the reference loader has an assets file to build against.
3. **Loads** it two ways and compares the resulting Roslyn `Project`s:
   - **Buildalyzer** — `AnalyzerManager.GetProject(path).GetWorkspace()` (an out-of-process
     design-time build read over a pipe).
   - **MSBuildWorkspace** — Roslyn's own in-process loader, treated as the **reference**.

`MSBuildWorkspace` is the ground truth: if the two disagree, that is a Buildalyzer gap.

## Why this works after the "drop the MSBuild engine" change

Buildalyzer core no longer references `Microsoft.Build` in-process — it shells out to
`dotnet` and reads build events over a pipe. That means this test process is free to host
`MSBuildWorkspace` + `Microsoft.Build.Locator` without any assembly-load conflict with
Buildalyzer itself.

`MSBuildRegistration` (a `[ModuleInitializer]`) registers the SDK's MSBuild with
`MSBuildLocator` before any `Microsoft.Build.*` type loads, and captures the exact SDK it
picked. `ProjectFixture` then pins that same SDK via a `global.json`, so the out-of-process
build (Buildalyzer) and the in-process build (MSBuildWorkspace) run on identical MSBuild.

The `Microsoft.Build.*` package references in the `.csproj` are `ExcludeAssets="runtime"`
(compile-time only) — MSBuildLocator requires that those assemblies are resolved from the SDK
at runtime rather than copied next to the test binary.

## What is compared

`RoslynProjectExtensions` normalises each side to order-independent sets of **full paths** so
they can be diffed with `BeEquivalentTo`: source documents, additional documents, analyzer
config documents, metadata references, analyzer references, project references and the
projects in the whole loaded graph. Full paths rather than file names, because both loaders run
against the same checkout and a name-only comparison would hide a document or reference
resolved from the wrong directory. The paths are compared exactly as each loader reports them
(no `Path.GetFullPath` on the way in): MSBuildWorkspace hands out canonical paths, so a `../`
spelling of the same file on the Buildalyzer side is a real divergence, not noise. The
`*Names()` variants exist for "contains X" checks.

`ProjectShape` goes further: `project.Shape()` flattens *everything* observable about a Roslyn
project into plain values so one `BeEquivalentTo` reports every difference at once:

- identity: name (including Roslyn's `Name(tfm)` convention for multi-targeted projects),
  assembly name, language, project path, output and reference-assembly paths, default namespace;
- documents of all three kinds with their name, path, logical folders and source-code kind;
- project references with target flavour, aliases and `EmbedInteropTypes`;
- metadata references with path, display, aliases, `EmbedInteropTypes` and kind;
- analyzer references with display and full path (unresolved ones included);
- every scalar compilation option (output kind, module/main type, optimisation, overflow,
  platform, warnings, concurrency, determinism, signing, metadata import, specific diagnostic
  options, plus C# unsafe/nullable and the VB `Option *`, root namespace and global imports);
- every parse option (kind, documentation mode, features, language version, preprocessor
  symbols, and VB's valued symbols).

`solution.Shape()` does the same for every project in a workspace, so a graph can be compared
end to end, including which flavour of a multi-targeted dependency each consumer was wired to.

## Adding a scenario

Add a `[Test]` to `Differential_specs.cs`:

```csharp
using ProjectFixture fixture = new();
string projectPath = fixture.AddProject(
    "MyScenario",
    p => p.Property("TargetFramework", "net10.0").ItemPackageReference("Some.Package", "1.0.0"),
    new Dictionary<string, string> { ["Class1.cs"] = "public class Class1 { }" });
fixture.Restore(projectPath);

using WorkspaceComparison comparison = await WorkspaceComparison.LoadAsync(projectPath);
comparison.Buildalyzer.MetadataReferenceNames()
    .Should().BeEquivalentTo(comparison.MSBuild.MetadataReferenceNames());
```

These tests perform two design-time builds each, so they are slower than unit tests; the
fixture is `[NonParallelizable]`.

## Real-world scenarios

`RealWorld_specs.cs` runs the same Buildalyzer-vs-MSBuildWorkspace comparison against actual
open-source projects cloned from GitHub (`OssRepositoryFixture`), rather than projects authored
in a temp directory. It answers "does Buildalyzer agree with Roslyn on the messy project files
people really ship?".

Each case shallow-clones a repository pinned to a release tag (so runs are reproducible),
replaces the repo's `global.json` with the SDK the test run selected (so both loaders build on
identical MSBuild), restores one project, and compares a single target framework — chosen to be
one that restores on any OS under a current SDK (`net8.0`, `netstandard2.0`), never a
Windows-only flavour. Source documents, metadata references, analyzer references and project
references must all match the reference; each case also dumps a set-difference report to the
test output for triage.

These are `[Explicit]` and tagged `[Category("RealWorld")]` because they need the network and
are slow. Run them on demand:

```bash
dotnet test --filter "TestCategory=RealWorld"          # all of them
dotnet test --filter "FullyQualifiedName~Serilog"      # a single repository
```

To add a repository, append a `Repo` record to `RealWorld_specs.Repositories` with its clone
URL, a tag, the project to load and the framework to compare.

`Aspire_specs.cs` is in the same category: it authors an Aspire AppHost solution (AppHost +
referenced API) with the fixture and compares the whole solution graph, since AppHosts generate
sources before `CoreCompile` and validate their project references with extra targets. It is
explicit because restoring the AppHost pulls the Aspire dashboard and orchestration packages.
