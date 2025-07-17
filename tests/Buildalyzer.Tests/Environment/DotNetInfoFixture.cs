using Buildalyzer.Environment;

namespace Buildalyzer.Tests.Environment;

public class DotNetInfoFixture
{
    [Test]
    public void Parses_Windows_NET8()
    {
        var lines = @"
 .NET SDK:
  Version:           8.0.200
  Commit:            438cab6a9d
  Workload version:  8.0.200-manifests.e575128c
 Runtime Environment:
  OS Name:     Windows
  OS Version:  10.0.22631
  OS Platform: Windows10
  RID:         win-x64
  Base Path:   C:\Program Files\dotnet\sdk\8.0.200\
 .NET workloads installed:
 There are no installed workloads to display.
 Host:
   Version:      8.0.2
   Architecture: x64
   Commit:       1381d5ebd2
 .NET SDKs installed:
   8.0.200 [C:\Program Files\dotnet\sdk]
 .NET runtimes installed:
   Microsoft.AspNetCore.All 2.1.30 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.All]
   Microsoft.AspNetCore.App 2.1.30 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.AspNetCore.App 3.1.32 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.AspNetCore.App 5.0.17 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.AspNetCore.App 6.0.27 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.AspNetCore.App 7.0.7 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.AspNetCore.App 7.0.16 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.AspNetCore.App 8.0.2 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
   Microsoft.NETCore.App 2.1.30 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 3.1.32 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 5.0.17 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 6.0.9 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 6.0.20 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 6.0.27 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 7.0.0-rc.2.22472.3 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 7.0.7 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 7.0.16 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.NETCore.App 8.0.2 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
   Microsoft.WindowsDesktop.App 3.1.32 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
   Microsoft.WindowsDesktop.App 5.0.17 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
   Microsoft.WindowsDesktop.App 6.0.20 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
   Microsoft.WindowsDesktop.App 6.0.27 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
   Microsoft.WindowsDesktop.App 7.0.7 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
   Microsoft.WindowsDesktop.App 7.0.16 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
   Microsoft.WindowsDesktop.App 8.0.2 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
 Other architectures found:
   x86   [C:\Program Files (x86)\dotnet]
     registered at [HKLM\SOFTWARE\dotn
 et\Setup\InstalledVersions\x86\InstallLocation]
 Environment variables:
   Not set
 global.json file:
   Not found
 Learn more:
   https://aka.ms/dotnet/info
 Download .NET:
   https://aka.ms/dotnet/download";

        var info = DotNetInfo.Parse(lines);

        info.Should().BeEquivalentTo(new
        {
            SdkVersion = new Version(8, 0, 200),
            OSName = "Windows",
            OSVersion = new Version(10, 0, 22631),
            OSPlatform = "Windows10",
            RID = "win-x64",
            SDKs = new Dictionary<string, string>()
            {
                ["8.0.200"] = "C:/Program Files/dotnet/sdk/8.0.200"
            },
            Runtimes = new { Count = 25, },
            BasePath = "C:/Program Files/dotnet/sdk/8.0.200",
        });
    }

    [Test]
    public void Parses_Windows_NET_Core()
    {
        var lines = @".NET Core SDK (reflecting any global.json):
 Version:   2.1.300
 Commit:    adab45bf0c

Runtime Environment:
 OS Name:     Windows
 OS Version:  6.1.7601
 OS Platform: Windows
 RID:         win7-x64
 Base Path:   C:\Program Files\dotnet\sdk\2.1.300\

Host (useful for support):
  Version: 2.1.0
  Commit:  caa7b7e2ba

.NET Core SDKs installed:
  2.1.200 [C:\Program Files\dotnet\sdk]
  2.1.300 [C:\Program Files\dotnet\sdk]
  2.1.201 [C:\Program Files\dotnet\sdk]

.NET Core runtimes installed:
  Microsoft.AspNetCore.All 2.1.0 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.All]
  Microsoft.AspNetCore.App 2.1.0 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
  Microsoft.NETCore.App 1.0.0 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 1.0.1 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 2.0.7 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
  Microsoft.NETCore.App 2.1.0 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]

To install additional .NET Core runtimes or SDKs:
  https://aka.ms/dotnet-download";

        var info = DotNetInfo.Parse(lines);

        info.Should().BeEquivalentTo(new
        {
            SdkVersion = new Version(2, 1, 300),
            OSName = "Windows",
            OSVersion = new Version(6, 1, 7601),
            OSPlatform = "Windows",
            RID = "win7-x64",
            SDKs = new Dictionary<string, string>()
            {
                ["2.1.200"] = "C:/Program Files/dotnet/sdk/2.1.200",
                ["2.1.300"] = "C:/Program Files/dotnet/sdk/2.1.300",
                ["2.1.201"] = "C:/Program Files/dotnet/sdk/2.1.201",
            },
            Runtimes = new Dictionary<string, string>()
            {
                ["Microsoft.AspNetCore.All 2.1.0"] = "C:/Program Files/dotnet/shared/Microsoft.AspNetCore.All",
                ["Microsoft.AspNetCore.App 2.1.0"] = "C:/Program Files/dotnet/shared/Microsoft.AspNetCore.App",
                ["Microsoft.NETCore.App 1.0.0"] = "C:/Program Files/dotnet/shared/Microsoft.NETCore.App",
                ["Microsoft.NETCore.App 1.0.1"] = "C:/Program Files/dotnet/shared/Microsoft.NETCore.App",
                ["Microsoft.NETCore.App 2.0.7"] = "C:/Program Files/dotnet/shared/Microsoft.NETCore.App",
                ["Microsoft.NETCore.App 2.1.0"] = "C:/Program Files/dotnet/shared/Microsoft.NETCore.App",
            },
            BasePath = "C:/Program Files/dotnet/sdk/2.1.300",
        });
    }

    [Test]
    public void Parses_Linux()
    {
        var lines = @"
.NET Core SDK (reflecting any global.json):
Version:   2.1.401
Commit:    91b1c13032

Runtime Environment:
  OS Name:     ubuntu
  OS Version:  16.04
  OS Platform: Linux
  RID:         ubuntu.16.04-x64
  Base Path:   /usr/share/dotnet/sdk/2.1.401/

Host (useful for support):
  Version: 2.1.3
  Commit:  124038c13e

.NET Core SDKs installed:
  1.1.5 [/usr/share/dotnet/sdk]
  1.1.6 [/usr/share/dotnet/sdk]
  2.1.201 [/usr/share/dotnet/sdk]

.NET Core runtimes installed:
  Microsoft.AspNetCore.All 2.1.3 [/usr/share/dotnet/shared/Microsoft.AspNetCore.All]
  Microsoft.NETCore.App 2.1.3 [/usr/share/dotnet/shared/Microsoft.NETCore.App]

To install additional .NET Core runtimes or SDKs:
  https://aka.ms/dotnet-download";

        var info = DotNetInfo.Parse(lines);

        info.Should().BeEquivalentTo(new
        {
            SdkVersion = new Version(2, 1, 401),
            OSName = "ubuntu",
            OSVersion = new Version(16, 4),
            OSPlatform = "Linux",
            RID = "ubuntu.16.04-x64",
            SDKs = new Dictionary<string, string>()
            {
                ["1.1.5"] = "/usr/share/dotnet/sdk/1.1.5",
                ["1.1.6"] = "/usr/share/dotnet/sdk/1.1.6",
                ["2.1.201"] = "/usr/share/dotnet/sdk/2.1.201",
            },
            Runtimes = new Dictionary<string, string>()
            {
                ["Microsoft.AspNetCore.All 2.1.3"] = "/usr/share/dotnet/shared/Microsoft.AspNetCore.All",
                ["Microsoft.NETCore.App 2.1.3"] = "/usr/share/dotnet/shared/Microsoft.NETCore.App",
            },
            BasePath = "/usr/share/dotnet/sdk/2.1.401",
        });
    }
}
