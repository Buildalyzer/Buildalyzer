using Buildalyzer;

namespace AnalyzerResult_specs;

public class Task_parameter_inputs
{
    // A result can see the compiler run more than once - WPF's full Build compiles the real source set in a
    // "*_wpftmp" project, attributed to the result, before the project's own CoreCompile does - and each run
    // passes the complete set, so the later one replaces the earlier rather than doubling every input.
    [Test]
    public void of_a_later_compiler_invocation_replace_the_earlier_ones()
    {
        var result = new AnalyzerResult(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "project.csproj"), new AnalyzerManager(), null);
        result.ProcessCscCommandLine("csc.dll /noconfig A.cs B.cs", coreCompile: true);

        result.AddTaskParameterInput("Sources", [new("A.cs", CompilerInputItem.NoMetadata), new("B.cs", CompilerInputItem.NoMetadata)]);
        result.AddTaskParameterInput("Sources", [new("A.cs", CompilerInputItem.NoMetadata), new("B.cs", CompilerInputItem.NoMetadata)]);

        result.SourceFiles.Select(System.IO.Path.GetFileName).Should().Equal("A.cs", "B.cs");
    }
}
