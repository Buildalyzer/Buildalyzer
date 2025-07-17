using Buildalyzer;

namespace Project_GUID_specs;

public class Creates
{
    [TestCase(null, /*....*/ "1b4db7eb-4057-5ddf-91e0-36dec72071f5")]
    [TestCase("", /*......*/ "1b4db7eb-4057-5ddf-91e0-36dec72071f5")]
    [TestCase("ABCDEFGHIJK", "2a738916-9f0a-5c81-a8fa-cc64ba606458")]
    [TestCase("Buildalyzer", "74397281-1b33-5316-aad1-c7ef52552d75")]
    public void SHA1_based_GUID(string? name, Guid projectId)
        => ProjectGuid.Create(name).Should().Be(projectId);
}
