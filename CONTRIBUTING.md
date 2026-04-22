# Contributing to Buildalyzer

Thank you for your interest in contributing! This document covers how to contribute code and how to publish a new release.

## Development

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (6.0, 8.0, and 10.0)
- Any IDE with C# support (Visual Studio, Rider, VS Code)

### Building

```bash
dotnet build
```

### Running tests

```bash
dotnet test
```

## Releasing a new version

Releases are published to NuGet automatically via Azure Pipelines. The pipeline triggers on any push to a `release/*` branch.

### 1. Handle breaking changes (if any)

If the release introduces intentional breaking changes to a public API, regenerate the API compatibility suppression file before tagging:

```bash
dotnet build --configuration Release /p:EnablePackageValidation=true /p:ApiCompatGenerateSuppressionFile=true
```

Review the changes in `src/Buildalyzer/CompatibilitySuppressions.xml` and commit the file. Each entry documents an intentional breaking change — this is what reviewers should inspect before merging.

> Breaking changes require a **major version bump** following [Semantic Versioning](https://semver.org).

### 2. Update the baseline version

In `src/Buildalyzer/Buildalyzer.csproj` and `src/Buildalyzer.Workspaces/Buildalyzer.Workspaces.csproj`, update `PackageValidationBaselineVersion` to the version being released:

```xml
<PackageValidationBaselineVersion>9.0.0</PackageValidationBaselineVersion>
```

Commit this change.

### 3. Create and push a git tag

The pipeline reads the version from the latest git tag:

```bash
git tag 9.0.0
git push origin 9.0.0
```

### 4. Push to a release branch

The pipeline only triggers on `release/*` branches:

```bash
git push origin main:release/9.0.0
```

This creates a `release/9.0.0` branch from `main` and triggers the pipeline, which will build, pack, and publish the NuGet packages automatically.

### 5. After publishing

Delete the `CompatibilitySuppressions.xml` file so the next development cycle starts clean:

```bash
git rm src/Buildalyzer/CompatibilitySuppressions.xml
git commit -m "chore: clear compatibility suppressions after release"
```
