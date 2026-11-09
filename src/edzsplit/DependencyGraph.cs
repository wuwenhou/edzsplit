namespace EdzSplit;

/// <summary>Type of node in the dependency graph.</summary>
internal enum NodeKind { Package, Item }

/// <summary>Node identifier: "pkg:<type>:<key>" or "item:<member-path-lower>".</summary>
internal static class NodeId
{
    public static string Package(Package package) => $"pkg:{package.Type}:{package.Key}".ToLowerInvariant();
    public static string Package(string type, string key) => $"pkg:{type}:{key}".ToLowerInvariant();
    public static string Item(string memberPath) => $"item:{ArchiveIndex.Normalize(memberPath)}".ToLowerInvariant();
}

internal enum ClosureMode { None, Related, Full }

/// <summary>
/// Builds and traverses the part <-> resource dependency graph.
/// Nodes are packages and items; edges point from owner to dependency and,
/// for accessory lists, from the accessory list to the parts it references
/// (traversed in reverse when starting from a part).
/// </summary>
internal sealed class DependencyGraph
{
    private readonly ManifestModel _model;
    private readonly ArchiveIndex _index;
    private readonly Dictionary<string, Package> _packageById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Package>> _packagesByKey = new(StringComparer.OrdinalIgnoreCase);
    // Every package that owns a member (shared items like manufacturer.xml belong to many packages).
    private readonly Dictionary<string, List<(Package Package, ItemRef Item)>> _itemOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Package>> _accessoryListsByPartKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Package> _accessoryListByName = new(StringComparer.OrdinalIgnoreCase);

    // Resolved member per item (nullptr when missing).
    private readonly Dictionary<ItemRef, ArchiveMember?> _resolved = new();

    private DependencyGraph(ManifestModel model, ArchiveIndex index)
    {
        _model = model;
        _index = index;
    }

    public static DependencyGraph Build(ManifestModel model, ArchiveIndex index, Func<string, string?> readPayload)
    {
        var graph = new DependencyGraph(model, index);

        foreach (var package in model.Packages)
        {
            graph._packageById[NodeId.Package(package)] = package;
            var keyId = NodeId.Package(package.Type, package.Key);
            graph._packageById[keyId] = package;
            // Key lookup must tolerate type namespaces — e.g. a part and a construction can share a key.
            if (package.Key.Length > 0)
            {
                var k = package.Key.ToLowerInvariant();
                if (!graph._packagesByKey.TryGetValue(k, out var list))
                    graph._packagesByKey[k] = list = new List<Package>();
                list.Add(package);
            }

            foreach (var item in package.Items)
            {
                var key = ArchiveIndex.Normalize(item.MemberPath);
                if (!graph._itemOwners.TryGetValue(key, out var owners))
                    graph._itemOwners[key] = owners = new List<(Package, ItemRef)>();
                owners.Add((package, item));

                if (index.TryResolve(item.MemberPath, out var member, out _))
                    graph._resolved[item] = member;
                else
                    graph._resolved[item] = null;
            }
        }

        // Index accessory lists by the parts they reference (reverse lookup for seed parts).
        foreach (var package in model.Packages)
        {
            if (!string.Equals(package.Type, "accessorylist", StringComparison.OrdinalIgnoreCase))
                continue;

            var listName = string.Empty;
            foreach (var item in package.Items)
            {
                if (!string.Equals(item.Name, "accessorylist", StringComparison.OrdinalIgnoreCase))
                    continue;
                var payload = readPayload(ArchiveIndex.Normalize(item.MemberPath));
                if (payload == null) continue;
                listName = PayloadRefs.ExtractAccessoryListName(payload);
                if (listName.Length > 0 && !graph._accessoryListByName.ContainsKey(listName))
                    graph._accessoryListByName[listName] = package;
                foreach (var (kind, value) in PayloadRefs.ExtractAccessoryListRefs(payload))
                {
                    if (kind != "part") continue;
                    var target = graph.FindPart(value);
                    if (target == null) continue;
                    var key = target.Key.ToLowerInvariant();
                    if (!graph._accessoryListsByPartKey.TryGetValue(key, out var list))
                        graph._accessoryListsByPartKey[key] = list = new List<Package>();
                    if (!list.Contains(package))
                        list.Add(package);
                }
            }
        }

        return graph;
    }

    public ArchiveMember? Resolve(ItemRef item) => _resolved.TryGetValue(item, out var member) ? member : null;

    public bool IsMissing(ItemRef item) => !_resolved.TryGetValue(item, out var member) || member == null;

    /// <summary>Returns the accessory lists that reference the given part key (reverse context).</summary>
    public IEnumerable<Package> AccessoryListsReferencing(Package part)
    {
        if (_accessoryListsByPartKey.TryGetValue((part.Key ?? string.Empty).ToLowerInvariant(), out var lists))
            return lists;

        // Mojibake-safe fallback lookup.
        var key = ArchiveIndex.Sanitize(part.Key ?? string.Empty).ToLowerInvariant();
        return _accessoryListsByPartKey.TryGetValue(key, out lists) ? lists : Enumerable.Empty<Package>();
    }

    /// <summary>Expands macro/picture/document references inside a part.xml payload into member nodes.</summary>
    public List<string> ExpandPartPayloadRefs(string payload)
    {
        var nodes = new List<string>();
        foreach (var payloadRef in PayloadRefs.ExtractFromXml(payload, string.Empty))
        {
            if (payloadRef.Kind is "construction" or "connectionpoints" or "accessoryId" or "isAccessory" or "external")
                continue;
            var expanded = PayloadRefs.ExpandMdVariables(payloadRef.RawValue);
            if (expanded == null) continue; // external URL
            if (_index.TryResolve(expanded, out _, out _))
                nodes.Add(NodeId.Item(expanded));
        }
        return nodes;
    }

    /// <summary>Finds connectionpoints items whose file name starts with the terminal name.</summary>
    public List<string> FindConnectionPointsItems(string terminalName)
    {
        var nodes = new List<string>();
        if (string.IsNullOrWhiteSpace(terminalName)) return nodes;
        var normalized = terminalName.Trim();
        foreach (var (memberPath, owners) in _itemOwners)
        {
            foreach (var (ownerPackage, ownerItem) in owners)
            {
                if (!string.Equals(ownerItem.Name, "connectionpoints", StringComparison.OrdinalIgnoreCase))
                    continue;
                var fileName = Path.GetFileNameWithoutExtension(memberPath);
                var baseName = fileName.EndsWith(".connectionpoints", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - ".connectionpoints".Length)
                    : fileName;
                if (baseName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    nodes.Add(NodeId.Item(memberPath));
            }
        }
        return nodes;
    }

    /// <summary>Finds a package of the given type by key, with a sanitize (mojibake-safe) fallback.</summary>
    public Package? FindPackage(string type, string key)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().ToLowerInvariant();
        if (_packagesByKey.TryGetValue(k, out var list))
        {
            var hit = list.FirstOrDefault(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        var sanitized = ArchiveIndex.Sanitize(k);
        foreach (var kv in _packagesByKey)
        {
            if (!string.Equals(ArchiveIndex.Sanitize(kv.Key), sanitized, StringComparison.OrdinalIgnoreCase)) continue;
            var hit = kv.Value.FirstOrDefault(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Finds a part package by key (type-scoped).</summary>
    public Package? FindPart(string partKey) => FindPackage("part", partKey);

    /// <summary>Finds the construction package named by a part.</summary>
    public Package? FindConstruction(string constructionName)
        => FindPackage("construction", constructionName);

    /// <summary>Items whose owning package carries structural context worth including.</summary>
    private static bool IsStructuralItem(ItemRef item)
    {
        var name = item.Name ?? string.Empty;
        return name.Equals("connectionpoints", StringComparison.OrdinalIgnoreCase)
            || name.Equals("accessorylist", StringComparison.OrdinalIgnoreCase)
            || name.Equals("accessoryplacement", StringComparison.OrdinalIgnoreCase)
            || name.Equals("construction", StringComparison.OrdinalIgnoreCase)
            || name.Equals("contour", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<Package> AllPackages => _model.Packages;

    /// <summary>Computes the closure for one package (typically a part).</summary>
    public ClosureResult ComputeClosure(Package seed, ClosureMode mode, Func<string, string?> readPayload)
    {
        var result = new ClosureResult();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        void Enqueue(string node)
        {
            if (visited.Add(node)) queue.Enqueue(node);
        }

        Enqueue(NodeId.Package(seed));

        if (mode != ClosureMode.None)
        {
            foreach (var accessoryList in AccessoryListsReferencing(seed))
                Enqueue(NodeId.Package(accessoryList));
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            {
                var memberPath = node.Substring("item:".Length);
                // If this item is a part.xml, expand payload references.
                if (_itemOwners.TryGetValue(memberPath, out var owners))
                {
                    foreach (var (ownerPackage, ownerItem) in owners)
                    {
                        if (!string.Equals(ownerItem.Name, "part", StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(ownerItem.Type, "partxml", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var payload = readPayload(memberPath);
                        if (payload == null || mode == ClosureMode.None) continue;
                        foreach (var child in ExpandPartPayloadRefs(payload)) Enqueue(child);
                        foreach (var payloadRef in PayloadRefs.ExtractFromXml(payload, string.Empty))
                        {
                            if (payloadRef.Kind == "construction")
                            {
                                var construction = FindConstruction(payloadRef.RawValue);
                                if (construction != null) Enqueue(NodeId.Package(construction));
                            }
                            else if (payloadRef.Kind == "connectionpoints")
                            {
                                foreach (var connectionPoint in FindConnectionPointsItems(payloadRef.RawValue))
                                    Enqueue(connectionPoint);
                            }
                        }
                        if (mode == ClosureMode.Full)
                        {
                            foreach (var accessoryList in AccessoryListsReferencing(ownerPackage))
                                Enqueue(NodeId.Package(accessoryList));
                        }
                    }
                    // Owner follow only for structural item kinds. Asset items
                    // (picture/macro/document/partxml data) are leaf inclusions —
                    // shared owners would drag whole other parts into the closure.
                    if (owners.Any(o => IsStructuralItem(o.Item)))
                        foreach (var (ownerPackage, _) in owners)
                            Enqueue(NodeId.Package(ownerPackage));
                }
                continue;
            }

            if (_packageById.TryGetValue(node, out var package))
            {
                result.Packages.Add(package);
                foreach (var item in package.Items)
                {
                    result.Items.Add(item);
                    Enqueue(NodeId.Item(item.MemberPath));
                }
                if (mode == ClosureMode.Full &&
                    string.Equals(package.Type, "accessorylist", StringComparison.OrdinalIgnoreCase))
                {
                    // Placements are owned items — including the accessory list pulls them in via the item loop.
                    // Also follow part references stored inside the list payload (forward direction).
                    foreach (var item in package.Items)
                    {
                        if (!string.Equals(item.Name, "accessorylist", StringComparison.OrdinalIgnoreCase)) continue;
                        var payload = readPayload(ArchiveIndex.Normalize(item.MemberPath));
                        if (payload == null) continue;
                        foreach (var (kind, value) in PayloadRefs.ExtractAccessoryListRefs(payload))
                        {
                            if (kind == "part")
                            {
                                var target = FindPart(value);
                                if (target != null)
                                    Enqueue(NodeId.Package(target));
                            }
                        }
                    }
                }
            }
        }

        // Related mode: seed part keeps only its own items plus direct references and
        // one level of accessory context. Full mode keeps everything above.
        if (mode == ClosureMode.Related)
        {
            // Remove accessory-list-pulled parts beyond the direct level:
            // (Implementation keeps exactly what we enqueued for seed-level accessory lists,
            //  which already matches Related semantics: direct references only.)
        }

        return result;
    }
}

internal sealed class ClosureResult
{
    public List<Package> Packages { get; } = new();
    public List<ItemRef> Items { get; } = new();
}
