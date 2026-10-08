A utility to perform design-time builds of .NET projects without having to think too hard about it.

![Buildalyzer Logo](https://raw.githubusercontent.com/phmonte/Buildalyzer/main/buildalyzer.png)

**NuGet**
* [Buildalyzer](https://www.nuget.org/packages/Buildalyzer/)
* [Buildalyzer.Workspaces](https://www.nuget.org/packages/Buildalyzer.Workspaces/)
* [Buildalyzer.CompilerLogs](https://www.nuget.org/packages/Buildalyzer.CompilerLogs/)

**GitHub**
* [Buildalyzer](https://github.com/phmonte/Buildalyzer)

**Donations**

If you found this library useful, consider [sponsoring more of it on GitHub](https://github.com/sponsors/phmonte). I promise to use it on something totally frivolous and unrelated.

[![Sponsor](https://img.shields.io/github/sponsors/phmonte?style=social&logo=github-sponsors)](https://github.com/sponsors/phmonte)

**Sponsors**

![AWS Logo](https://raw.githubusercontent.com/phmonte/Buildalyzer/main/aws.png)

[Amazon Web Services (AWS)](https://aws.amazon.com/) generously sponsors this project. [Go check out](https://aws.amazon.com/developer/language/net) what they have to offer for .NET developers (it's probably more than you think).

---

## What Is It?

Buildalyzer lets you run MSBuild from your own code and returns information about the project. By default, it runs a [design-time build](https://daveaglick.com/posts/running-a-design-time-build-with-msbuild-apis) which is higher performance than a normal build because it doesn't actually try to compile the project. You can use it to perform analysis of MSBuild projects, get project properties, or create a Roslyn Workspace using [Buildalyzer.Workspaces](https://www.nuget.org/packages/Buildalyzer.Workspaces/). It runs MSBuild out-of-process and therefore should work anywhere, anytime, and on any platform you can build the project yourself manually on the command line.


```csharp
AnalyzerManager manager = new AnalyzerManager();
IProjectAnalyzer analyzer = manager.GetProject(@"C:\MyCode\MyProject.csproj");
IAnalyzerResults results = analyzer.Build();
string[] sourceFiles = results.First().SourceFiles;
```

These blog posts might also help explain the motivation behind the project and how it works:
* [Running A Design-Time Build With MSBuild APIs](https://daveaglick.com/posts/running-a-design-time-build-with-msbuild-apis)
* [MSBuild Loggers And Logging Events](https://daveaglick.com/posts/msbuild-loggers-and-logging-events)

## Architecture

Buildalyzer runs the build in a **separate process** and streams the results back over a pipe. Your code drives an `AnalyzerManager`; `ProjectAnalyzer` shells out to `dotnet msbuild` for a design-time build, passing an MSBuild logger (`BuildalyzerLogger`, built on [XenoAtom.MsBuildPipeLogger](https://www.nuget.org/packages/XenoAtom.MsBuildPipeLogger)) whose events are written back over an anonymous pipe. Buildalyzer's `EventProcessor` turns those events into an `AnalyzerResult` per target framework, and `Buildalyzer.Workspaces` maps each result into a Roslyn `AdhocWorkspace`.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/architecture-roundtrip-dark.svg">
  <img alt="Buildalyzer architecture: an out-of-process design-time build streamed back over a pipe and turned into a Roslyn workspace" src="docs/architecture-roundtrip-light.svg">
</picture>


## Installation

Buildalyzer is [available on NuGet](https://www.nuget.org/packages/Buildalyzer/) and can be installed via the commands below:

```
$ Install-Package Buildalyzer
```
or via the .NET Core CLI:

```
$ dotnet add package Buildalyzer
```

Buildalyzer.Workspaces is [available on NuGet](https://www.nuget.org/packages/Buildalyzer.Workspaces/) and can be installed via the commands below:

```
$ Install-Package Buildalyzer.Workspaces
```
or via the .NET Core CLI:

```
$ dotnet add package Buildalyzer.Workspaces
```

Buildalyzer.CompilerLogs (see [Compiler Logs](#compiler-logs)) is installed the same way. Buildalyzer targets .NET 8, Buildalyzer.Workspaces targets .NET 8 and .NET 10, and Buildalyzer.CompilerLogs targets .NET 9 and .NET 10.

## Usage

There are two main classes in Buildalyzer: `AnalyzerManager` and `ProjectAnalyzer`.

The `AnalyzerManager` class coordinates loading each individual project and consolidates information from a solution file if provided.

The `ProjectAnalyzer` class figures out how to configure MSBuild and uses it to load and compile the project in *design-time* mode. Using a design-time build lets us get information about the project such as resolved references and source files without actually having to call the compiler.

To get a `ProjectAnalyzer` you first create an `AnalyzerManager` and then call `GetProject()`:

```csharp
AnalyzerManager manager = new AnalyzerManager();
IProjectAnalyzer analyzer = manager.GetProject(@"C:\MyCode\MyProject.csproj");
```

You can add all projects in a solution to the `AnalyzerManager` by passing the solution path as the first argument of the `AnalyzerManager` constructor. This will parse the solution file and execute `GetProject()` for each of the projects that it finds.

Calling `GetProject()` again for the same project path will return the existing `ProjectAnalyzer`. You can iterate all the existing project analyzers with the `IReadOnlyDictionary<string, ProjectAnalyzer>` property `AnalyzerManager.Projects`.

To build the project, which triggers evaluation of the specified MSBuild tasks and targets but stops short of invoking the compiler by default in Buildalyzer, call `Build()`. This method has a number of overloads that lets you customize the build process by specifying target frameworks, build targets, and more.

### File-based apps

A [file-based app](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/sdk#file-based-apps) - a single `.cs` file you run with `dotnet run app.cs` - has no project file. Pass the file itself and Buildalyzer analyzes the project the .NET SDK generates for it, directives (`#:package`, `#:sdk`, `#:property`) and all:

```csharp
AnalyzerManager manager = new AnalyzerManager();
IProjectAnalyzer analyzer = manager.GetProject(@"C:\MyCode\app.cs");
```

This is the same rule Roslyn's `MSBuildWorkspace` uses: a path that exists, does not carry a project file extension, and either ends in `.cs` or starts with a `#!` shebang is a file-based app. It needs the .NET 10 SDK or later, and the generated project is written next to the entry point file (where the SDK itself would put it) for the duration of each build, then removed.

## Results

Calling `ProjectAnalyzer.Build()` (or an overload) will return an `AnalyzerResults` object, which is a collection of `AnalyzerResult` objects for each of the target frameworks that were built. It will usually only contain a single `AnalyzerResult` unless the project is multi-targeted.

`AnalyzerResult` contains several properties and methods with the results from the build:

**`AnalyzerResult.TargetFramework`** - The target framework of this particular result (each result consists of data from a particular target framework build).

**`AnalyzerResult.SourceFiles`** - The full path of all resolved source files in the project.

**`AnalyzerResult.References`** - The full path of all resolved references in the project.

**`AnalyzerResult.ProjectReferences`** - The full path of the project file for all resolved project references in the project.

**`AnalyzerResult.Properties`** - A `IReadOnlyDictionary<string, string>` containing all MSBuild properties from the project.

**`AnalyzerResult.GetProperty(string)`** - Gets the value of the specified MSBuild property.

**`AnalyzerResult.Items`** - A `IReadOnlyDictionary<string, ProjectItem[]>` containing all MSBuild items from the project (the `ProjectItem` class contains the item name/specification as `ProjectItem.ItemSpec` and all it's metadata in a `IReadOnlyDictionary<string, string>` as `ProjectItem.Metadata`).

## Adjusting MSBuild Properties

Buildalyzer sets some MSBuild properties to make loading and compilation work the way it needs to (for example, to trigger a design-time build). You can view these properties with the `IReadOnlyDictionary<string, string>` property `ProjectAnalyzer.GlobalProperties`.

If you want to change the configured properties before loading or compiling the project, there are two options:

* `AnalyzerManager.SetGlobalProperty(string key, string value)` and `AnalyzerManager.RemoveGlobalProperty(string key)`. This will set the global properties for all projects loaded by this `AnalyzerManager`.

* `ProjectAnalyzer.SetGlobalProperty(string key, string value)` and `ProjectAnalyzer.RemoveGlobalProperty(string key)`. This will set the global properties for just this project.

Be careful though, you may break the ability to load, compile, or interpret the project if you change the MSBuild properties.


## Single-file and Native AOT publishing

Buildalyzer itself is trim- and Native-AOT-compatible: it runs MSBuild out of process and reads the
results over a pipe, so its own code needs no reflection. Two things need attention when you publish
your application as a single file or with `PublishAot`.

**The logger assemblies have to be real files.** MSBuild loads them in the build process, so they
cannot live inside your single-file bundle or native image:

* `Buildalyzer.Logger.dll`
* `XenoAtom.MsBuildPipeLogger.Logger.dll`

If they sit next to your executable, Buildalyzer finds them and there is nothing to configure. If you
deploy them somewhere else, point the `LoggerPathDll` environment variable at
`Buildalyzer.Logger.dll`. See related issue [224](https://github.com/phmonte/Buildalyzer/issues/224).

**A Native AOT publish does not copy them for you.** Both assemblies are compiled into the native
image and no longer emitted as files, so ask for them explicitly in your application's project file:

```xml
<Target Name="PublishBuildalyzerLogger" AfterTargets="ComputeResolvedFilesToPublishList">
  <ItemGroup>
    <ResolvedFileToPublish Include="@(ReferenceCopyLocalPaths)"
                           Condition="'%(Extension)' == '.dll' and ('%(Filename)' == 'Buildalyzer.Logger' or '%(Filename)' == 'XenoAtom.MsBuildPipeLogger.Logger')">
      <RelativePath>%(Filename)%(Extension)</RelativePath>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </ResolvedFileToPublish>
  </ItemGroup>
</Target>
```

Two further caveats. `Buildalyzer.Workspaces` is **not** AOT-compatible - Roslyn composes its host
services through MEF, which fails at runtime in a native image - so an AOT application can read
project structure, source files, references and compiler command lines, but cannot create a Roslyn
workspace. And an AOT-published application still shells out to `dotnet msbuild`, so the .NET SDK
must be installed on the machine that runs it.

## Binary Log Files

Buildalyzer can also read [MSBuild binary log files](http://msbuildlog.com/):

```csharp
AnalyzerManager manager = new AnalyzerManager();
IAnalyzerResults results = manager.Analyze(@"C:\MyCode\MyProject.binlog");
string[] sourceFiles = results.First().SourceFiles;
```

This is useful if you already have a binary log file and want to analyze it with Buildalyzer the same way you would build results.

## Logging

Buildalyzer uses the `Microsoft.Extensions.Logging` framework for logging MSBuild output. When you create an `AnayzerManager` you can specify an `ILoggerFactory` that Buildalyzer should use to create loggers. By default, the `ProjectAnalyzer` will log MSBuild output to the provided logger.

You can also log to a `StringWriter` using `AnalyzerManagerOptions`:

```csharp
StringWriter log = new StringWriter();
AnalyzerManagerOptions options = new AnalyzerManagerOptions
{
    LogWriter = log
};
AnalyzerManager manager = new AnalyzerManager(path, options);
// ...
// check log.ToString() after build for any error messages
```

## Roslyn Workspaces

The extension library `Buildalyzer.Workspaces` adds extension methods to the Buildalyzer `ProjectAnalyzer` that make it easier to take Buildalyzer output and create a Roslyn `AdhocWorkspace` from it:

```csharp
using Buildalyzer.Workspaces;
using Microsoft.CodeAnalysis;
// ...

AnalyzerManager manager = new AnalyzerManager();
IProjectAnalyzer analyzer = manager.GetProject(@"C:\MyCode\MyProject.csproj");
AdhocWorkspace workspace = analyzer.GetWorkspace();
```

You can also create your own workspace and add Buildalyzer projects to it:

```csharp
using Buildalyzer.Workspaces;
using Microsoft.CodeAnalysis;
// ...

AnalyzerManager manager = new AnalyzerManager();
IProjectAnalyzer analyzer = manager.GetProject(@"C:\MyCode\MyProject.csproj");
AdhocWorkspace workspace = new AdhocWorkspace();
Project roslynProject = analyzer.AddToWorkspace(workspace);
```

In both cases, Buildalyzer will attempt to resolve project references within the Roslyn workspace so the Roslyn projects will correctly reference each other.

## Compiler Logs

The extension library `Buildalyzer.CompilerLogs` writes a [compiler log](https://github.com/jaredpar/complog) (`.complog`) from a set of build results. A compiler log packages every input the compiler saw (sources, references, analyzers, generated files, options) into one portable file that the `complog` tooling can inspect, replay, diff and export, which makes it a convenient way to hand a compilation to someone else or to keep one for later analysis:

```csharp
using Buildalyzer.CompilerLogs;
// ...

AnalyzerManager manager = new AnalyzerManager();
IAnalyzerResults results = manager.GetProject(@"C:\MyCode\MyProject.csproj").Build();
CompilerLogCreateResult created = results.TryCreateCompilerLog(@"C:\MyCode\MyProject.complog");
```

One compilation is written per built target framework. `TryCreateCompilerLog` reports the results it could not include (a result that never reached the compiler has no compilation to record); `CreateCompilerLog` throws instead. The package requires .NET 9 or later.
