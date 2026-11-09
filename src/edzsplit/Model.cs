using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EdzSplit;

internal sealed class ItemRef
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Locator { get; set; } = "";

    /// <summary>Archive member path for this item ("items\&lt;type&gt;\&lt;locator&gt;").</summary>
    public string MemberPath
    {
        get
        {
            var locator = NormalizeSlashes(Locator).TrimStart('\\');
            return $"items\\{Type}\\{locator}";
        }
    }

    public static string NormalizeSlashes(string value) => (value ?? string.Empty).Replace('/', '\\');

    public ItemRef Clone() => new() { Type = Type, Name = Name, Locator = Locator };
}

internal sealed class Package
{
    public string Type { get; init; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ItemRef> Items { get; init; } = new();

    public Package Clone()
        => new()
        {
            Type = Type,
            Key = Key,
            Name = Name,
            Items = Items.Select(i => i.Clone()).ToList()
        };
}

internal sealed class ManifestModel
{
    public string Version { get; set; } = "2.0";
    public List<Package> Packages { get; } = new();

    public IEnumerable<ItemRef> AllItems => Packages.SelectMany(p => p.Items);

    public static ManifestModel Parse(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var root = doc.Root ?? throw new InvalidDataException("manifest.xml: missing root element.");
        if (!string.Equals(root.Name.LocalName, "manifest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("manifest.xml: root element is not <manifest>.");

        var model = new ManifestModel { Version = (string)root.Attribute("version") ?? "2.0" };
        foreach (var packageElement in root.Elements("packages").Elements("package"))
        {
            var package = new Package
            {
                Type = (string)packageElement.Attribute("type") ?? "",
                Key = (string)packageElement.Attribute("key") ?? "",
                Name = (string)packageElement.Attribute("name") ?? ""
            };
            foreach (var itemElement in packageElement.Elements("items").Elements("item"))
            {
                package.Items.Add(new ItemRef
                {
                    Type = (string)itemElement.Attribute("type") ?? "",
                    Name = (string)itemElement.Attribute("name") ?? "",
                    Locator = (string)itemElement.Attribute("locator") ?? ""
                });
            }
            model.Packages.Add(package);
        }
        return model;
    }

    /// <summary>Re-serializes the model as EDZ manifest.xml. Keeps UTF-8 and the EPLAN declaration.</summary>
    public string ToXml()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = " ",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
            NewLineChars = "\r\n"
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartElement("manifest");
            writer.WriteAttributeString("version", Version);
            writer.WriteStartElement("packages");
            foreach (var package in Packages)
            {
                writer.WriteStartElement("package");
                writer.WriteAttributeString("type", package.Type);
                writer.WriteAttributeString("key", package.Key);
                writer.WriteAttributeString("name", package.Name);
                writer.WriteStartElement("items");
                foreach (var item in package.Items)
                {
                    writer.WriteStartElement("item");
                    writer.WriteAttributeString("type", item.Type);
                    writer.WriteAttributeString("name", item.Name);
                    writer.WriteAttributeString("locator", item.Locator);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
