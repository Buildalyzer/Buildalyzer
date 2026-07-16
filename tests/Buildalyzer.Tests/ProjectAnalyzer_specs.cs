using Buildalyzer;

namespace ProjectAnalyzer_specs;

public class Suffixed_binary_log_paths
{
    [TestCase("project.binlog", "net8.0", "project.net8.0.binlog")]
    [TestCase(@"C:\logs\project.binlog;ProjectImports=Embed", "net8.0", @"C:\logs\project.net8.0.binlog;ProjectImports=Embed")]
    [TestCase("ProjectImports=None;LogFile=project.binlog", "net462", "ProjectImports=None;LogFile=project.net462.binlog")]
    [TestCase("LogFile=\"my project.binlog\"", "net8.0", "LogFile=\"my project.net8.0.binlog\"")]
    [TestCase("project.binlog", "restore", "project.restore.binlog")]
    public void rewrite_only_the_log_file_segment(string parameters, string suffix, string expected)
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(parameters, suffix)
            .Should().Be(expected);

    [TestCase("", "net8.0")]
    [TestCase("ProjectImports=None", "net8.0")]
    [TestCase("OmitInitialInfo;ProjectImports=ZipFile", "net8.0")]
    public void leave_parameters_without_a_log_file_untouched(string parameters, string suffix)
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(parameters, suffix)
            .Should().Be(parameters);
}
