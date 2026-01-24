using System.Xml.Linq;
using Buildalyzer.Construction;
using Shouldly;

namespace Buildalyzer.Tests.Construction;

[TestFixture]
public class PackageReferenceFixture
{
    [Test]
    public void PackageReferenceWithIncludeShouldContainName()
    {
        // Given
        XElement xml = XElement.Parse(@"<PackageReference Include=""IncludedDependency"" Version=""1.0.0"" />");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Name.ShouldBe("IncludedDependency");
    }

    [Test]
    public void PackageReferenceWithVersionShouldContainVersion()
    {
        // Given
        XElement xml = XElement.Parse(@"<PackageReference Include=""IncludedDependency"" Version=""1.0.0"" />");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Version.ShouldBe("1.0.0");
    }

    [Test]
    public void PackageReferenceWithUpgradeShouldContainName()
    {
        // Given
        XElement xml = XElement.Parse(@"<PackageReference Update=""UpdatedDependency"" Version=""1.0.0"" />");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Name.ShouldBe("UpdatedDependency");
    }

    [Test]
    public void PackageReferenceWithVersionChildElementShouldContainVersion()
    {
        // Given
        XElement xml = XElement.Parse(@"
            <PackageReference Include=""IncludedDependency"">
                <Version>4.0.0</Version>
            </PackageReference>
        ");

        // When
        PackageReference packageReference = new PackageReference(xml);

        // Then
        packageReference.Version.ShouldBe("4.0.0");
    }
}