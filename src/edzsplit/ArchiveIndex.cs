namespace EdzSplit;

/// <summary>
/// Archive member index with the two repair grades we need:
///   1. exact (case-insensitive) match,
///   2. slash-normalized match,
///   3. non-ASCII -> '_' sanitize match (EKF mojibake pattern: manifest keeps Cyrillic,
///      archive entry stores '_').
/// </summary>
internal sealed class ArchiveIndex
{
    private readonly Dictionary<string, ArchiveMember> _byPath;
    private readonly Dictionary<string, ArchiveMember> _bySanitized;

    public IReadOnlyCollection<ArchiveMember> Members => _byPath.Values;

    private ArchiveIndex(Dictionary<string, ArchiveMember> byPath)
    {
        _byPath = byPath;
        _bySanitized = new Dictionary<string, ArchiveMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in byPath.Values)
        {
            var sanitized = Sanitize(member.Path);
            // First come, first served: deterministic and collision-aware enough for a fallback.
            _bySanitized.TryAdd(sanitized, member);
        }
    }

    public static ArchiveIndex FromMembers(IEnumerable<ArchiveMember> members)
    {
        // Tolerate duplicates (first wins) — a malformed archive should not crash analysis.
        var map = new Dictionary<string, ArchiveMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
            map.TryAdd(Normalize(member.Path), member);
        return new(map);
    }

    public static string Normalize(string path)
        => ItemRef.NormalizeSlashes(path ?? string.Empty).TrimStart('\\');

    /// <summary>Maps an archive member path to a real filesystem path under <paramref name="root"/>, cross-platform.</summary>
    public static string DiskPath(string root, string memberPath)
    {
        var segments = Normalize(memberPath).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(new[] { root }.Concat(segments).ToArray());
    }

    public static string Sanitize(string path)
    {
        var normalized = Normalize(path);
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            builder.Append(ch < 128 ? ch : '_');
        return builder.ToString();
    }

    public bool TryResolve(string memberPath, out ArchiveMember member, out bool repaired)
    {
        member = null!;
        repaired = false;
        var normalized = Normalize(memberPath);

        if (_byPath.TryGetValue(normalized, out var exact))
        {
            member = exact;
            return true;
        }

        var sanitized = Sanitize(normalized);
        if (_bySanitized.TryGetValue(sanitized, out var loose))
        {
            member = loose;
            repaired = true;
            return true;
        }

        return false;
    }

    public bool Contains(string memberPath) => TryResolve(memberPath, out _, out _);

    /// <summary>Resolves a reference like the part XML uses for macros/pictures/documents.</summary>
    public bool TryResolveReference(string reference, out ArchiveMember member, out bool repaired)
        => TryResolve(reference, out member, out repaired);
}
