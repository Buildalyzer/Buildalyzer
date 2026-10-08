using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Buildalyzer.Workspaces;

/// <summary>
/// Reconstructs the compiler command line that the <c>Csc</c>/<c>Vbc</c> task would have produced from a
/// project's evaluated properties, for builds that stopped before the compiler task ran (issue #341).
/// </summary>
/// <remarks>
/// <para>
/// The switches are then handed to Roslyn's own command-line parser, so the resulting options follow the
/// compiler's semantics exactly rather than a hand-maintained property-to-option mapping: numeric warning
/// ids become <c>CSxxxx</c>, <c>/nowarn</c> overrides <c>/warnaserror</c>, the module name derives from
/// the output file, unknown language versions are diagnosed the same way, and so on. Only the switches
/// that shape <see cref="CompilationOptions"/> and <see cref="ParseOptions"/> are reconstructed; sources,
/// references and analyzers are recovered from evaluated items separately.
/// </para>
/// <para>
/// The property-to-switch binding mirrors <c>Microsoft.CSharp.Core.targets</c> /
/// <c>Microsoft.VisualBasic.Core.targets</c> (the <c>CoreCompile</c> task invocations) and the
/// <c>ManagedCompiler</c>/<c>Csc</c>/<c>Vbc</c> tasks' <c>AddResponseFileCommands</c>. Values that are only
/// computed by targets - most notably the SDK's implicit framework defines (<c>NET10_0</c>,
/// <c>NET5_0_OR_GREATER</c>, ...) - are recomputed here from the evaluated properties the targets read.
/// </para>
/// </remarks>
internal static class EvaluatedCommandLine
{
    private static readonly char[] ListSeparators = [';', ','];

    /// <summary>
    /// Parses the reconstructed command line, or returns <c>null</c> for a language Roslyn's parsers do not
    /// cover.
    /// </summary>
    /// <remarks>
    /// Language-specific parsing stays in local functions so the parser assembly for the other language is
    /// never loaded for a project that does not use it.
    /// </remarks>
    public static CommandLineArguments? Parse(IAnalyzerResult result, string languageName, string? projectDirectory)
    {
        if (languageName == LanguageNames.CSharp)
        {
            CommandLineArguments ParseCSharp() => CSharpCommandLineParser.Default.Parse(CSharpArguments(result), projectDirectory, sdkDirectory: null);
            return ParseCSharp();
        }

        if (languageName == LanguageNames.VisualBasic)
        {
            CommandLineArguments ParseVisualBasic() => VisualBasicCommandLineParser.Default.Parse(VisualBasicArguments(result), projectDirectory, sdkDirectory: null);
            return ParseVisualBasic();
        }

        return null;
    }

    private static List<string> CSharpArguments(IAnalyzerResult result)
    {
        List<string> arguments = CommonArguments(result);

        // Csc: DefineConstants="$(DefineConstants)" - after AddImplicitDefineConstants has appended the
        // implicit framework defines. Csc's GetDefineConstantsSwitch drops anything that is not a valid
        // identifier, which Roslyn's parser does as well.
        IEnumerable<string> defines = Split(result.GetProperty("DefineConstants")).Concat(ImplicitDefineConstants(result));
        if (IsTrue(result.GetProperty("DisableDiagnosticTracing")))
        {
            defines = defines.Where(d => d != "TRACE");
        }

        AddList(arguments, "/define:", defines, ';');
        AddFlag(arguments, "/unsafe", result.GetProperty("AllowUnsafeBlocks"));
        AddFlag(arguments, "/checked", result.GetProperty("CheckForOverflowUnderflow"));
        AddValue(arguments, "/nullable:", result.GetProperty("Nullable"));
        AddValue(arguments, "/warn:", result.GetProperty("WarningLevel"));

        // Csc: InterceptorsNamespaces="$(InterceptorsNamespaces)" and its older spelling
        // InterceptorsPreviewNamespaces (which the SDK composes at evaluation time for its own generators);
        // the task forwards whichever is set - the current name first - as the InterceptorsNamespaces feature.
        string? interceptors = result.GetProperty("InterceptorsNamespaces") is { Length: > 0 } current
            ? current
            : result.GetProperty("InterceptorsPreviewNamespaces");
        AddValue(arguments, "/features:InterceptorsNamespaces=", interceptors);

        // Csc: DisabledWarnings="$(NoWarn)", after CoreCompile in Microsoft.CSharp.Core.targets has appended
        // the warnings MSBuild itself reports (1701/1702 assembly-version unification), the strong-name
        // warning .NET Core ignores (8002), and inside Visual Studio the "no sources" warning (2008).
        List<string> noWarn = [.. Split(result.GetProperty("NoWarn"))];
        if (result.GetProperty("TargetFrameworkVersion") is not ("v1.0" or "v1.1"))
        {
            noWarn.AddRange(["1701", "1702"]);
        }

        if (IsTrue(result.GetProperty("BuildingInsideVisualStudio"))
            && Version.TryParse(result.GetProperty("VisualStudioVersion"), out Version? visualStudioVersion)
            && visualStudioVersion > new Version(10, 0))
        {
            noWarn.Add("2008");
        }

        if (result.GetProperty("TargetFrameworkIdentifier") == ".NETCoreApp")
        {
            noWarn.Add("8002");
        }

        AddList(arguments, "/nowarn:", noWarn, ',');
        return arguments;
    }

    private static List<string> VisualBasicArguments(IAnalyzerResult result)
    {
        List<string> arguments = CommonArguments(result);

        // Vbc: DefineConstants="$(FinalDefineConstants)" - CONFIG/DEBUG/TRACE/_MyType/PLATFORM plus the
        // user's DefineConstants, composed at evaluation time, with the implicit framework defines appended
        // as "<SYMBOL>=-1" by AddImplicitDefineConstants.
        IEnumerable<string> defines = Split(result.GetProperty("FinalDefineConstants"), ',')
            .Concat(ImplicitDefineConstants(result).Select(d => $"{d}=-1"));
        AddList(arguments, "/define:", defines, ',');

        AddFlag(arguments, "/optionexplicit", result.GetProperty("OptionExplicit"));
        AddFlag(arguments, "/optioninfer", result.GetProperty("OptionInfer"));
        AddFlag(arguments, "/optionstrict", result.GetProperty("OptionStrict"));
        AddValue(arguments, "/optionstrict:", result.GetProperty("OptionStrictType"));
        AddValue(arguments, "/optioncompare:", result.GetProperty("OptionCompare")?.ToLowerInvariant());
        AddValue(arguments, "/rootnamespace:", result.GetProperty("RootNamespace"));
        AddFlag(arguments, "/removeintchecks", result.GetProperty("RemoveIntegerChecks"));

        // Vbc: DisabledWarnings="$(NoWarn)" after CoreCompile in Microsoft.VisualBasic.Core.targets has
        // appended the strong-name warning .NET Core ignores (BC41997); NoWarnings="$(_NoWarnings)", which
        // the same target derives from WarningLevel 0 (all warnings off).
        List<string> noWarn = [.. Split(result.GetProperty("NoWarn"))];
        if (result.GetProperty("TargetFrameworkIdentifier") == ".NETCoreApp")
        {
            noWarn.Add("BC41997");
        }

        AddList(arguments, "/nowarn:", noWarn, ',');
        if (result.GetProperty("WarningLevel")?.Trim() == "0")
        {
            arguments.Add("/nowarn");
        }

        // Vbc: Imports="@(Import)".
        if (result.Items.TryGetValue("Import", out IProjectItem[] imports) && imports.Length > 0)
        {
            arguments.Add("/imports:" + string.Join(",", imports.Select(i => i.ItemSpec)));
        }

        return arguments;
    }

    // The switches ManagedCompiler emits for both compilers. Order matters only for the warning switches:
    // a bare /warnaserror resets the specific ones, so it goes first, exactly as the task emits it. NoWarn
    // is per language, since each compiler's CoreCompile target appends its own ids before the task runs.
    private static List<string> CommonArguments(IAnalyzerResult result)
    {
        List<string> arguments = [];
        AddValue(arguments, "/target:", result.GetProperty("OutputType")?.ToLowerInvariant());

        // OutputAssembly="@(IntermediateAssembly)": only the file name matters here, it is what the module
        // name derives from.
        AddValue(arguments, "/out:", result.GetProperty("TargetFileName"));
        AddValue(arguments, "/main:", result.GetProperty("StartupObject"));
        AddValue(arguments, "/platform:", Platform(result));
        AddValue(arguments, "/keyfile:", result.GetProperty("KeyOriginatorFile"));
        AddFlag(arguments, "/delaysign", result.GetProperty("DelaySign"));
        AddFlag(arguments, "/publicsign", result.GetProperty("PublicSign"));
        AddFlag(arguments, "/optimize", result.GetProperty("Optimize"));
        AddFlag(arguments, "/deterministic", result.GetProperty("Deterministic"));
        AddFlag(arguments, "/warnaserror", result.GetProperty("TreatWarningsAsErrors"));
        AddList(arguments, "/warnaserror+:", Split(result.GetProperty("WarningsAsErrors")), ',');
        AddList(arguments, "/warnaserror-:", Split(result.GetProperty("WarningsNotAsErrors")), ',');
        AddValue(arguments, "/langversion:", result.GetProperty("LangVersion"));
        AddValue(arguments, "/moduleassemblyname:", result.GetProperty("ModuleAssemblyName"));

        // DocumentationFile="@(DocFileItem)": the compiler diagnoses doc comments when asked to emit them.
        // GenerateDocumentationFile sets DocumentationFile at evaluation time in the SDK, but a project may
        // also set either directly; only the presence of the switch matters for the options.
        if (result.GetProperty("DocumentationFile") is { Length: > 0 } documentationFile)
        {
            arguments.Add("/doc:" + documentationFile);
        }
        else if (IsTrue(result.GetProperty("GenerateDocumentationFile")))
        {
            arguments.Add("/doc:" + Path.ChangeExtension(result.GetProperty("TargetFileName") ?? "documentation.dll", ".xml"));
        }

        // Features="$(Features)": one /features: switch per semicolon-separated entry.
        foreach (string feature in Split(result.GetProperty("Features")))
        {
            arguments.Add("/features:" + feature);
        }

        return arguments;
    }

    // Csc/Vbc: Platform="$(PlatformTarget)", combined with Prefer32Bit into "anycpu32bitpreferred".
    private static string? Platform(IAnalyzerResult result)
    {
        string? platform = result.GetProperty("PlatformTarget");
        bool prefer32Bit = IsTrue(result.GetProperty("Prefer32Bit"));
        return prefer32Bit && (string.IsNullOrEmpty(platform) || platform.Equals("anycpu", StringComparison.OrdinalIgnoreCase))
            ? "anycpu32bitpreferred"
            : platform;
    }

    /// <summary>
    /// The conditional-compilation symbols the SDK's <c>AddImplicitDefineConstants</c> target (and the
    /// <c>Generate*DefineConstants</c> targets it depends on, in <c>Microsoft.NET.Sdk.BeforeCommon.targets</c>)
    /// would append to <c>DefineConstants</c>: the framework (<c>NET</c>, <c>NET10_0</c>, <c>NETCOREAPP</c>,
    /// <c>NETSTANDARD2_0</c>, <c>NET472</c>, ...), the target platform (<c>WINDOWS</c>,
    /// <c>WINDOWS10_0_19041</c>), and the <c>_OR_GREATER</c> set for every supported version at or below the
    /// target's. Targets never run in an evaluation-only result, so these are recomputed from the same
    /// evaluated properties and items the targets read.
    /// </summary>
    internal static IEnumerable<string> ImplicitDefineConstants(IAnalyzerResult result)
    {
        if (IsTrue(result.GetProperty("DisableImplicitFrameworkDefines")))
        {
            yield break;
        }

        string? identifier = result.GetProperty("TargetFrameworkIdentifier");
        string? version = result.GetProperty("TargetFrameworkVersion")?.TrimStart('v', 'V');
        if (string.IsNullOrEmpty(identifier) || identifier == ".NETPortable" || !Version.TryParse(version, out Version? targetVersion))
        {
            yield break;
        }

        bool isNetCoreApp = identifier == ".NETCoreApp";
        bool isNetFramework = identifier == ".NETFramework";
        bool isNet5OrGreater = isNetCoreApp && targetVersion.Major >= 5;

        // GenerateTargetFrameworkDefineConstants
        string upperIdentifier = identifier.Replace(".", string.Empty).ToUpperInvariant();
        string versionlessDefine = isNet5OrGreater ? "NET" : upperIdentifier;
        string definePrefix = isNet5OrGreater || isNetFramework ? "NET" : upperIdentifier;
        string versionDefine = version!.Replace('.', '_');
        if (isNetFramework)
        {
            versionDefine = versionDefine.Replace("_", string.Empty);
        }

        yield return versionlessDefine;
        yield return definePrefix + versionDefine;
        if (isNet5OrGreater)
        {
            yield return upperIdentifier;
        }

        // GenerateTargetPlatformDefineConstants
        string? platformIdentifier = result.GetProperty("TargetPlatformIdentifier");
        bool hasPlatform = isNet5OrGreater && !string.IsNullOrEmpty(platformIdentifier);
        string upperPlatform = platformIdentifier?.ToUpperInvariant() ?? string.Empty;
        if (hasPlatform)
        {
            string platformVersion = result.GetProperty("EffectiveTargetPlatformVersion") is { Length: > 0 } effective
                ? effective
                : result.GetProperty("TargetPlatformVersion") ?? string.Empty;
            yield return upperPlatform;
            yield return upperPlatform + platformVersion.Replace('.', '_');
        }

        // GenerateNETCompatibleDefineConstants
        string? supportedItemType = identifier switch
        {
            ".NETCoreApp" => "_NETCoreAppVersionsForDefines",
            ".NETFramework" => "SupportedNETFrameworkTargetFramework",
            ".NETStandard" => "SupportedNETStandardTargetFramework",
            _ => null,
        };
        if (supportedItemType is not null && result.Items.TryGetValue(supportedItemType, out IProjectItem[] supported))
        {
            foreach (Version supportedVersion in supported
                .Select(item => item.ItemSpec)
                .Select(spec => spec[(spec.IndexOf("Version=v", StringComparison.Ordinal) + "Version=v".Length)..])
                .Select(v => Version.TryParse(v, out Version? parsed) ? parsed : null)
                .OfType<Version>()
                .Where(v => v <= targetVersion))
            {
                string formatted = isNetFramework
                    ? supportedVersion.ToString().Replace(".", string.Empty)
                    : supportedVersion.ToString().Replace('.', '_');
                string prefix = isNetCoreApp && supportedVersion.Major < 5 ? "NETCOREAPP" : definePrefix;
                yield return $"{prefix}{formatted}_OR_GREATER";
            }
        }

        // GeneratePlatformCompatibleDefineConstants
        if (hasPlatform
            && Version.TryParse(result.GetProperty("TargetPlatformVersion"), out Version? targetPlatformVersion)
            && result.Items.TryGetValue("SdkSupportedTargetPlatformVersion", out IProjectItem[] supportedPlatforms))
        {
            HashSet<string> seen = [];
            foreach (IProjectItem item in supportedPlatforms)
            {
                // Prefer the normalized version when the SDK provides one (drops the trailing ".1" of
                // CsWinRT 3.0 platform versions) so both variants share a define.
                string spec = item.Metadata.TryGetValue("NormalizedSupportedTargetPlatformVersion", out string normalized) && normalized.Length > 0
                    ? normalized
                    : item.ItemSpec;
                if (Version.TryParse(spec, out Version? platformVersion)
                    && platformVersion <= targetPlatformVersion
                    && seen.Add(spec))
                {
                    yield return $"{upperPlatform}{spec.Replace('.', '_')}_OR_GREATER";
                }
            }
        }
    }

    private static void AddValue(List<string> arguments, string switchName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add(switchName + value!.Trim());
        }
    }

    private static void AddList(List<string> arguments, string switchName, IEnumerable<string> values, char separator)
    {
        string joined = string.Join(separator, values);
        if (joined.Length > 0)
        {
            arguments.Add(switchName + joined);
        }
    }

    // A "+"/"-" switch is emitted only when the property has a boolean value, as the task does.
    private static void AddFlag(List<string> arguments, string switchName, string? value)
    {
        if (TryParseBool(value, out bool enabled))
        {
            arguments.Add(switchName + (enabled ? "+" : "-"));
        }
    }

    private static IEnumerable<string> Split(string? value, params char[] separators)
        => (value ?? string.Empty).Split(separators.Length > 0 ? separators : ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsTrue(string? value) => TryParseBool(value, out bool result) && result;

    // MSBuild converts task bool parameters from more than "true"/"false": "on"/"off" and "yes"/"no" are
    // accepted too (OptionStrict is conventionally "On"/"Off").
    private static bool TryParseBool(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true":
            case "on":
            case "yes":
                result = true;
                return true;
            case "false":
            case "off":
            case "no":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
