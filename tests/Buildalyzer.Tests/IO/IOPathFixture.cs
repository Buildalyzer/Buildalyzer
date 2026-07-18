using System.ComponentModel;
using System.IO;
using Buildalyzer.IO;

namespace Buildalyzer.Tests.IO;

public class IOPathFixture
{
    [Test]
    public void Root_makes_a_relative_path_absolute()
        => IOPath.Parse("some/relative/path.txt").Root().File()!.FullName
            .Should().Be(new FileInfo("some/relative/path.txt").FullName);

    [Test]
    public void Root_collapses_parent_segments()
        => IOPath.Parse(Path.Combine("a", "b", "..", "c.txt")).Root()
            .Should().Be(IOPath.Parse(Path.GetFullPath(Path.Combine("a", "c.txt"))));

    [Test]
    public void Root_of_empty_is_empty()
        => IOPath.Empty.Root().Should().Be(IOPath.Empty);

#if Is_Windows
    [Test]
    public void Is_case_insensitive_on_windows()
        => IOPath.IsCaseSensitive.Should().BeFalse();
#endif

    [Test]
    public void Is_seperator_agnostic()
        => IOPath.Parse(".\\root\\test\\somefile.txt").Should().Be(IOPath.Parse("./root/test/somefile.txt"));

    [TestCase(@"c:\Program Files\Buildalyzer")]
    public void Supports_type_conversion(string path)
        => TypeDescriptor.GetConverter(typeof(IOPath)).ConvertFromString(path)
            .Should().Be(IOPath.Parse(@"c:\Program Files\Buildalyzer"));
}
