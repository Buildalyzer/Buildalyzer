using Buildalyzer;

namespace BuildCommandProperties_specs;

public class Removes
{
    [Test]
    public void null_values()
    {
        var props = BuildCommandProperties.Create(
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
}

public class Keeps
{
    /// <remarks>
    /// F# used to be excluded from <c>SkipCompilerExecution</c> because the full <c>Clean;Build</c>
    /// closure raised file-copy errors. Building the <c>Compile</c> target on a supported SDK no longer
    /// does, and skipping the compiler is what makes an F# design-time build both fast and independent
    /// of whether <c>fsc</c> itself would succeed.
    /// </remarks>
    [Test]
    public void SkipCompilerExecution_for_FSharp()
    {
        var props = BuildCommandProperties.Create(
            null,
            [
                KeyValuePair.Create("SkipCompilerExecution", "true"),
            ]);

        props.Should().BeEquivalentTo(
        [
            new BuildCommandProperty("SkipCompilerExecution", "true"),
        ]);
    }
}
