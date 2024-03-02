﻿using Buildalyzer.IO;
using FluentAssertions;

namespace Buildalyzer.Tests.IO;

public class IOPathFixture
{
#if Is_Windows
    [Test]
    public void Is_case_insensitive_on_windows()
        => IOPath.IsCaseSensitive.Should().BeFalse();
#endif

    [Test]
    public void is_seperator_agnostic()
        => IOPath.Parse(".\\root\\test\\somefile.txt").Should().Be(IOPath.Parse("./root/test/somefile.txt"));

    [TestCase(@"c:\Program Files\Buildalyzer")]
    public void supports_type_conversion(IOPath path)
        => path.Should().Be(IOPath.Parse(@"c:\Program Files\Buildalyzer"));
}
