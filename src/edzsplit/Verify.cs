namespace EdzSplit;

internal sealed class VerifyResult
{
    public bool Ok;
    public List<string> Errors = new();
    public List<string> Warnings = new();
    public int Packages;
    public int Items;
    public int Missing;
    public string? ManifestVersion;
}

internal static class Verify
{
    /// <summary>Structural verification: container integrity, manifest parse, all item refs resolvable, no path traversal.</summary>
    public static VerifyResult Run(string edz, bool strict = false)
    {
        var result = new VerifyResult();
        var sevenZip = SevenZipCli.Find();
        var archive = Path.GetFullPath(edz);
        if (!File.Exists(archive))
        {
            result.Errors.Add("File not found: " + archive);
            return result;
        }

        if (!SevenZipCli.IsSevenZipFile(archive) && !SevenZipCli.IsZipFile(archive))
        {
            result.Errors.Add("Not a 7z or ZIP container: " + archive);
            return result;
        }

        // 1) Container test.
        var (code, output) = SevenZipCli.Test(sevenZip, archive);
        if (code != 0)
        {
            result.Errors.Add("7z test failed: " + Tail(output, 1500));
            return result;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "edzsplit-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var members = SevenZipCli.List(sevenZip, archive);
            var index = ArchiveIndex.FromMembers(members);

            // 2) Path traversal guard on entry names.
            foreach (var member in members)
            {
                var path = ArchiveIndex.Normalize(member.Path);
                if (Path.IsPathRooted(path) || path.StartsWith('\\') || path.Split('\\').Any(seg => seg == ".."))
                    result.Errors.Add("Unsafe entry path: " + member.Path);
            }
            if (result.Errors.Count > 0) return result;

            // 3) Manifest presence + parse.
            var manifestText = ReadManifest(archive, index, workDir, sevenZip);
            if (manifestText == null)
            {
                result.Errors.Add("manifest.xml missing.");
                return result;
            }

            ManifestModel model;
            try { model = ManifestModel.Parse(manifestText); }
            catch (Exception ex) { result.Errors.Add("manifest.xml parse failed: " + ex.Message); return result; }

            result.Packages = model.Packages.Count;
            result.Items = model.AllItems.Count();
            result.ManifestVersion = model.Version;

            // 4) Item refs resolve (with repair report).
            foreach (var item in model.AllItems)
            {
                if (index.TryResolve(item.MemberPath, out _, out var repaired))
                {
                    if (repaired)
                        result.Warnings.Add($"Repaired reference: {item.MemberPath}");
                }
                else
                {
                    result.Missing++;
                    result.Errors.Add($"Missing item: {item.Type}/{item.Name} -> {item.MemberPath}");
                }
            }

            // 5) EPLAN-specific sanity: a parts EDZ carries at least one package type we recognize.
            var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "part", "manufacturer", "construction", "connectionpoints", "accessorylist", "accessoryplacement"
            };
            foreach (var package in model.Packages)
                if (!knownTypes.Contains(package.Type))
                    result.Warnings.Add($"Unknown package type: {package.Type}");

            result.Ok = result.Errors.Count == 0 && (!strict || result.Warnings.Count == 0);
            return result;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static string? ReadManifest(string archive, ArchiveIndex index, string workDir, string sevenZip)
    {
        var direct = index.Members
            .Where(m => string.Equals(ArchiveIndex.Normalize(m.Path), "manifest.xml", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Path)
            .ToArray();
        var candidates = direct.Length > 0
            ? direct
            : index.Members
                .Where(m => string.Equals(Path.GetFileName(ArchiveIndex.Normalize(m.Path)), "manifest.xml", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Path)
                .ToArray();
        if (candidates.Length == 0) return null;

        var (code, _) = SevenZipCli.ExtractMembers(sevenZip, archive, candidates, workDir, workDir);
        if (code != 0) return null;
        var path = ArchiveIndex.DiskPath(workDir, candidates[0]);
        return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null;
    }

    private static string Tail(string text, int max)
        => text.Length <= max ? text : text.Substring(text.Length - max);
}
