﻿using Buildalyzer.Environment;
using NUnit.Framework;
using Shouldly;

namespace Buildalyzer.Tests.Environment;

[TestFixture]
public class EnvironmentFactoryFixture
{
    // From https://docs.microsoft.com/en-us/dotnet/standard/frameworks
    // .NET Core/.NET 5 and up
    [TestCase("netcoreapp1.0", false)]
    [TestCase("netcoreapp1.1", false)]
    [TestCase("netcoreapp2.0", false)]
    [TestCase("netcoreapp2.1", false)]
    [TestCase("netcoreapp2.2", false)]
    [TestCase("netcoreapp3.0", false)]
    [TestCase("netcoreapp3.1", false)]
    [TestCase("net5", false)] // This isn't an official TFM but we'll check it anyway
    [TestCase("net5.0", false)]
    [TestCase("net5.0-android", false)]
    [TestCase("net5.0-ios", false)]
    [TestCase("net5.0-macos", false)]
    [TestCase("net5.0-tvos", false)]
    [TestCase("net5.0-watchos", false)]
    [TestCase("net5.0-windows", false)]
    [TestCase("net6", false)] // This isn't an official TFM but we'll check it anyway
    [TestCase("net6.0", false)]
    [TestCase("net6.0-android", false)]
    [TestCase("net6.0-ios", false)]
    [TestCase("net6.0-macos", false)]
    [TestCase("net6.0-tvos", false)]
    [TestCase("net6.0-watchos", false)]
    [TestCase("net6.0-windows", false)]
    [TestCase("netstandard1.0", false)]
    [TestCase("netstandard1.1", false)]
    [TestCase("netstandard1.2", false)]
    [TestCase("netstandard1.3", false)]
    [TestCase("netstandard1.4", false)]
    [TestCase("netstandard1.5", false)]
    [TestCase("netstandard1.6", false)]
    [TestCase("netstandard2.0", false)]
    [TestCase("netstandard2.1", false)]

    // .NET Framework
    [TestCase("net11", true)]
    [TestCase("net20", true)]
    [TestCase("net35", true)]
    [TestCase("net40", true)]
    [TestCase("net403", true)]
    [TestCase("net45", true)]
    [TestCase("net451", true)]
    [TestCase("net452", true)]
    [TestCase("net46", true)]
    [TestCase("net461", true)]
    [TestCase("net462", true)]
    [TestCase("net47", true)]
    [TestCase("net471", true)]
    [TestCase("net472", true)]
    [TestCase("net48", true)]

    // Should treat the more exotic TFMs as .NET Framework
    [TestCase("netcore", true)]
    [TestCase("netcore45", true)]
    [TestCase("netcore451", true)]
    [TestCase("netmf", true)]
    [TestCase("sl4", true)]
    [TestCase("sl5", true)]
    [TestCase("wp", true)]
    [TestCase("wp7", true)]
    [TestCase("wp75", true)]
    [TestCase("wp8", true)]
    [TestCase("wp81", true)]
    [TestCase("wpa81", true)]
    [TestCase("uap", true)]
    [TestCase("uap10.0", true)]
    public void IsFrameworkTargetFrameworkForTfm(string targetFramework, bool expected)
    {
        // Given, When
        bool result = EnvironmentFactory.IsFrameworkTargetFramework(targetFramework);

        // Then
        result.ShouldBe(expected);
    }
}
