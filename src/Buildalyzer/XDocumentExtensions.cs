using System.Xml.Linq;

namespace Buildalyzer;

internal static class XDocumentExtensions
{
    public static IEnumerable<XElement> GetDescendants(this XDocument document, string name) =>
        document.Descendants().Where(x => x.Name.LocalName.IsMatch(name));

    public static IEnumerable<XElement> GetDescendants(this XElement element, string name) =>
        element.Descendants().Where(x => x.Name.LocalName.IsMatch(name));

    public static string? GetAttributeValue(this XElement element, string name) =>
        element.Attributes().FirstOrDefault(x => x.Name.LocalName.IsMatch(name))?.Value;

    // Adds a child element with the same namespace as the parent
    public static void AddChildElement(this XElement element, string name, string value) =>
        element.Add(new XElement(XName.Get(name, element.Name.NamespaceName), value));
}
