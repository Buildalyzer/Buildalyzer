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

`RoslynProjectExtensions` normalises each side to order-independent, case-insensitive sets so
they can be diffed with `BeEquivalentTo`: source documents, additional documents, metadata
references, analyzer references, project references, preprocessor symbols, plus scalar
compilation/parse facts (output kind, `AllowUnsafe`, overflow checks, nullable context,
platform, effective language version, warning level, deterministic).

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
