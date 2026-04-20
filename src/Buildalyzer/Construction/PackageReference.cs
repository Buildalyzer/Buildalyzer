using System.Xml.Linq;

namespace Buildalyzer.Construction;

public class PackageReference : IPackageReference
{
    public string Name { get; }
    public string Version { get; }

    internal PackageReference(XElement packageReferenceElement)
    {
        Name = packageReferenceElement.GetAttributeValue("Include") ?? packageReferenceElement.GetAttributeValue("Update");
        var versionElement = packageReferenceElement.DescendantsAndSelf()
            .FirstOrDefault(x => x.Name.LocalName == "Version");
        Version = packageReferenceElement.GetAttributeValue("Version") ?? versionElement?.Value;
    }
}
