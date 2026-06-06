using System.Xml.Linq;
using Buildalyzer.Construction;

namespace Buildalyzer.Tests.Construction;

[TestFixture]
public class PackageReferenceFixture
{
    [Test]
    public void PackageReference_with_include_contains_name()
    {
        // Given
        XElement xml = XElement.Parse(@"<PackageReference Include=""IncludedDependency"" Version=""1.0.0"" />");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Name.Should().Be("IncludedDependency");
    }

    [Test]
    public void PackageReference_with_version()
    {
        // Given
        XElement xml = XElement.Parse(@"<PackageReference Include=""IncludedDependency"" Version=""1.0.0"" />");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Version.Should().Be("1.0.0");
    }

    [Test]
    public void PackageReference_with_upgrade_contains_ame()
    {
        // Given
        XElement xml = XElement.Parse(@"<PackageReference Update=""UpdatedDependency"" Version=""1.0.0"" />");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Name.Should().Be("UpdatedDependency");
    }

    [Test]
    public void PackageReference_with_version_as_element()
    {
        // Given
        XElement xml = XElement.Parse("""
            <PackageReference Include="IncludedDependency">
                <Version>4.0.0</Version>
            </PackageReference>
            """);

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Version.Should().Be("4.0.0");
    }
}
