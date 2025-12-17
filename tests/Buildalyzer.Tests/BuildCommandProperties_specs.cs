using Buildalyzer;
using Buildalyzer.IO;

namespace BuildCommandProperties_specs;

public class Removes
{
    [Test]
    public void null_values()
    {
        var props = BuildCommandProperties.Create(
            IOPath.Empty,
            null,
            [
                KeyValuePair.Create("add", "value"),
                KeyValuePair.Create("delete", "a value"),
                KeyValuePair.Create("delete", "an update"),
                KeyValuePair.Create("delete", (string?)null),
            ]);

        props.Should().BeEquivalentTo(
        [
            new BuildCommandProperty("add", "value"),
        ]);
    }

    [Test]
    public void SkipCompilerExecution_for_FSharp()
    {
        var props = BuildCommandProperties.Create(
            IOPath.Parse("project.fsproj"),
            null,
            [
                KeyValuePair.Create("SkipCompilerExecution", "true"),
            ]);

        props.Should().BeEmpty();
    }
}
