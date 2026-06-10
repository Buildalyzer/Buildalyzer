A utility to perform design-time builds of .NET projects without having to think too hard about it.

![Buildalyzer Logo](https://raw.githubusercontent.com/phmonte/Buildalyzer/main/buildalyzer.png)

**NuGet**
* [Buildalyzer](https://www.nuget.org/packages/Buildalyzer/)
* [Buildalyzer.Workspaces](https://www.nuget.org/packages/Buildalyzer.Workspaces/)

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

Both packages target .NET Standard 2.0.

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


## Publish SingleFile
If your application's output is a single file, you will need to provide the path to the following DLLs:

-XenoAtom.MsBuildPipeLogger.Logger.dll
-Buildalyzer.logger.dll

Variable name: LoggerPathDll

See related issue [224](https://github.com/phmonte/Buildalyzer/issues/224)
msbuild needs the physical address of the logger, for this reason it is not possible to use single file publish without informing this route.

Remembering that if the files are in the root where the project is running, it is not necessary to inform the path.
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
