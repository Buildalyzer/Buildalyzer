using System.IO;

namespace Buildalyzer.Environment;

internal static class DotNetInfoParser
{
    [Pure]
    public static DotNetInfo Parse(IEnumerable<string> lines)
    {
        var header = string.Empty;

        Version? sdkVersion = null;
        string? osName = null;
        string? osPlatform = null;
        Version? osVersion = null;
        string? basePath = null;
        string? globalJson = null;
        string? rid = null;
        var sdks = new Dictionary<string, string>();
        var runtimes = new Dictionary<string, string>();

        foreach (var line in lines.Select(l => l.Trim()).Where(l => l is { Length: > 0 }))
        {
            // Update read header.
            if (Headers.Contains(line))
            {
                header = line.ToUpperInvariant();
                continue;
            }

            switch (header)
            {
                case "":
                case ".NET SDK:":
                    sdkVersion ??= Version("Version:", line);
                    break;

                case "RUNTIME ENVIRONMENT:":
                    basePath ??= BasePath(line);
                    osName ??= Label("OS Name:", line);
                    osPlatform ??= Label("OS Platform:", line);
                    osVersion ??= Version("OS Version:", line);
                    rid ??= Label("RID:", line);
                    break;

                case ".NET SDKS INSTALLED:":
                case ".NET CORE SDKS INSTALLED:":
                    AddSdk(line);
                    break;

                case ".NET RUNTIMES INSTALLED:":
                case ".NET CORE RUNTIMES INSTALLED:":
                    AddRunTime(line);
                    break;

                case "GLOBAL.JSON FILE:":
                    globalJson ??= GlobalJson(line);
                    break;
            }
        }

        return new DotNetInfo()
        {
            SdkVersion = sdkVersion,
            OSName = osName,
            OSPlatform = osPlatform,
            OSVersion = osVersion,
            RID = rid,
            BasePath = basePath,
            GlobalJson = globalJson,
            SDKs = sdks.ToImmutableDictionary(),
            Runtimes = runtimes.ToImmutableDictionary(),
        };

        void AddSdk(string line)
        {
            if (line.Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: 2 } parts)
            {
                sdks[parts[0]] = UnifyPath(Path.Combine(parts[1], parts[0]));
            }
        }
        void AddRunTime(string line)
        {
            if (line.Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: 2 } parts)
            {
                runtimes[parts[0]] = UnifyPath(parts[1]);
            }
        }
    }

    [Pure]
    private static Version? Version(string prefix, string line)
            => line.IsMatchStart(prefix) && System.Version.TryParse(line[prefix.Length..].Trim(), out var parsed)
                ? parsed
                : null;

    [Pure]
    private static string? Label(string prefix, string line)
        => line.IsMatchStart(prefix) && line[prefix.Length..].Trim() is { Length: > 0 } label
            ? label
            : null;

    [Pure]
    private static string? BasePath(string line)
    {
        if (line.IsMatchStart("Base Path:"))
        {
            var path = line[10..].Trim();

            // Make sure the base path matches the runtime architecture if on Windows
            // Note that this only works for the default installation locations under "Program Files"
            if (path.Contains(@"\Program Files\") && !System.Environment.Is64BitProcess)
            {
                string newBasePath = path.Replace(@"\Program Files\", @"\Program Files (x86)\");
                if (Directory.Exists(newBasePath))
                {
                    path = newBasePath;
                }
            }
            else if (path.Contains(@"\Program Files (x86)\") && System.Environment.Is64BitProcess)
            {
                string newBasePath = path.Replace(@"\Program Files (x86)\", @"\Program Files\");
                if (Directory.Exists(newBasePath))
                {
                    path = newBasePath;
                }
            }

            return UnifyPath(path);
        }
        else
        {
            return null;
        }
    }

    [Pure]
    private static string UnifyPath(string path) => path.Replace('\\', '/').TrimEnd('/');

    [Pure]
    private static string? GlobalJson(string line) => line.IsMatch("Not found") ? null : line;

    private static readonly HashSet<string> Headers = new(StringComparer.InvariantCultureIgnoreCase)
    {
        ".NET SDK:",
        "Runtime Environment:",
        "Host:",
        ".NET workloads installed:",
        ".NET SDKs installed:",
        ".NET Core SDKs installed:",
        ".NET runtimes installed:",
        ".NET Core runtimes installed:",
        "Other architectures found:",
        "global.json file:",
        "Learn more:",
        "Download .NET:",
    };
}
