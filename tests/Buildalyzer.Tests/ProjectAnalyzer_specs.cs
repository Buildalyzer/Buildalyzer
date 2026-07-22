using Buildalyzer;

namespace ProjectAnalyzer_specs;

public class Suffixed_binary_log_paths
{
    [TestCase("project.binlog", "net8.0", "project.net8.0.binlog")]
    [TestCase(@"C:\logs\project.binlog", "net8.0", @"C:\logs\project.net8.0.binlog")]
    [TestCase("project.binlog", "restore", "project.restore.binlog")]
    [TestCase("my project.binlog", "net8.0", "my project.net8.0.binlog")]
    public void append_suffix_before_the_extension(string path, string suffix, string expected)
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(path, suffix)
            .Should().Be(expected);

    [Test]
    public void leave_empty_paths_untouched()
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(string.Empty, "net8.0")
            .Should().Be(string.Empty);
}
