using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EdzSplit;

/// <summary>One reference found inside a part/accessory/construction XML payload.</summary>
internal sealed class PayloadRef
{
    /// <summary>macro | groupsymbolmacro | picture | document | construction | connectionpoints | part | placement | external</summary>
    public string Kind { get; init; } = "";
    public string RawValue { get; init; } = "";
    public string Attribute { get; init; } = "";
}

internal static class PayloadRefs
{
    // EPLAN part XML uses attribute form: P_ARTICLE_MACRO="$(MD_MACROS)\Rittal\VX\VX8618250.ema"
    // Some documents store external documents as P_ARTICLE_EXTERNAL_DOCUMENT_n with URLs.
    private static readonly Regex AttributeRegex = new(
        @"(P_ARTICLE_(?:MACRO|GROUPSYMBOLMACRO|PICTUREFILE|EXTERNAL_DOCUMENT_\d+|REF_CONSTRUCTION_NAME|REF_TERMINAL_NAME|ACCESSORYID|IS_ACCESSORY))\s*=\s*"
        + "\"(?<v>[^\"]*)\"|'(?<v>[^']*)'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] MacroAttributes = { "P_ARTICLE_MACRO", "P_ARTICLE_GROUPSYMBOLMACRO" };

    public static List<PayloadRef> ExtractFromXml(string xml, string fileName)
    {
        var refs = new List<PayloadRef>();
        if (string.IsNullOrWhiteSpace(xml)) return refs;

        foreach (Match match in AttributeRegex.Matches(xml))
        {
            var attribute = match.Groups[1].Value.ToUpperInvariant();
            var value = (match.Groups["v"].Value ?? "").Trim();
            if (value.Length == 0) continue;

            if (attribute == "P_ARTICLE_MACRO") refs.Add(Ref("macro", attribute, value));
            else if (attribute == "P_ARTICLE_GROUPSYMBOLMACRO") refs.Add(Ref("groupsymbolmacro", attribute, value));
            else if (attribute == "P_ARTICLE_PICTUREFILE") refs.Add(Ref("picture", attribute, value));
            else if (attribute.StartsWith("P_ARTICLE_EXTERNAL_DOCUMENT_", StringComparison.Ordinal))
                refs.Add(Ref("externalDocument", attribute, value));
            else if (attribute == "P_ARTICLE_REF_CONSTRUCTION_NAME") refs.Add(Ref("construction", attribute, value));
            else if (attribute == "P_ARTICLE_REF_TERMINAL_NAME") refs.Add(Ref("connectionpoints", attribute, value));
            else if (attribute == "P_ARTICLE_ACCESSORYID") refs.Add(Ref("accessoryId", attribute, value));
            else if (attribute == "P_ARTICLE_IS_ACCESSORY") refs.Add(Ref("isAccessory", attribute, value));
        }

        // externalDocument values can be plain URLs or URLs followed by a localized description block.
        for (var i = refs.Count - 1; i >= 0; i--)
        {
            if (refs[i].Kind != "externalDocument") continue;
            refs[i] = Ref("externalDocument", refs[i].Attribute, FirstLine(refs[i].RawValue));
        }

        return refs;

        static PayloadRef Ref(string kind, string attribute, string value) => new() { Kind = kind, Attribute = attribute, RawValue = value };
        static string FirstLine(string value)
        {
            var cut = value.IndexOf("&#10;", StringComparison.Ordinal);
            if (cut < 0) cut = value.IndexOf('\n');
            return cut < 0 ? value.Trim() : value.Substring(0, cut).Trim();
        }
    }

    /// <summary>Accessory list payload: positions reference parts and (optionally) placements.</summary>
    public static List<(string Kind, string Value)> ExtractAccessoryListRefs(string xml)
    {
        var refs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(xml)) return refs;
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            foreach (var position in doc.Descendants("accessorylistposition"))
            {
                var partNr = (string?)position.Attribute("partnr");
                if (!string.IsNullOrWhiteSpace(partNr))
                    refs.Add(("part", partNr.Trim()));
                var placement = (string?)position.Attribute("placement");
                if (!string.IsNullOrWhiteSpace(placement))
                    refs.Add(("placement", placement.Trim()));
            }
        }
        catch (XmlException) { }
        return refs;
    }

    /// <summary>Accessory placement payload: reads the placement record name.</summary>
    public static string ExtractAccessoryPlacementName(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var element = doc.Descendants("accessoryplacement").FirstOrDefault();
            return (string?)element?.Attribute("P_PART_ACCESSORYPLACEMENT_NAME") ?? string.Empty;
        }
        catch (XmlException) { return string.Empty; }
    }

    /// <summary>Accessory list payload: reads the list record name.</summary>
    public static string ExtractAccessoryListName(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var element = doc.Descendants("accessorylist").FirstOrDefault();
            return (string?)element?.Attribute("P_PART_ACCESSORYLIST_NAME") ?? string.Empty;
        }
        catch (XmlException) { return string.Empty; }
    }

    /// <summary>Construction payload: currently has no outgoing refs we depend on (drilling positions only),
    /// but keep the hook so .fc1 contour references can be added without changing the graph.</summary>
    public static List<(string Kind, string Value)> ExtractConstructionRefs(string xml) => new();

    /// <summary>True when the value looks like an external URL rather than a file path.</summary>
    public static bool IsExternalUri(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("#", StringComparison.Ordinal);

    /// <summary>
    /// Expands EPLAN master-data variables to archive item folders.
    /// Returns null when the reference is external (URL) or not a file reference.
    /// </summary>
    public static string? ExpandMdVariables(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;
        var value = rawValue.Trim();
        if (IsExternalUri(value)) return null;

        var map = new[]
        {
            ("$(MD_MACROS)", "items\\macro"),
            ("$(MD_IMG)", "items\\picture"),
            ("$(MD_IMAGES)", "items\\picture"),
            ("$(MD_DOCUMENTS)", "items\\document"),
            ("$(MD_DOC)", "items\\document"),
            ("$(MD_MISC)", "items\\misc")
        };

        foreach (var (token, replacement) in map)
        {
            var idx = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var expanded = value.Remove(idx, token.Length).Insert(idx, replacement);
                return NormalizeArchivePath(expanded);
            }
        }

        // Plain relative path (already item-root relative) or absolute file system path —
        // the latter cannot exist in the archive; let callers treat it as unresolvable.
        return NormalizeArchivePath(value);
    }

    public static string NormalizeArchivePath(string value)
    {
        var normalized = ItemRef.NormalizeSlashes(value).TrimStart('\\');
        // Strip a leading "items\" when already archive-rooted.
        return normalized;
    }

    public static string ToArchiveItemsPath(string expanded)
        // Kept for callers that need an explicit "items\" prefix check.
        => expanded.StartsWith("items\\", StringComparison.OrdinalIgnoreCase) ? expanded : "items\\" + expanded;
}
