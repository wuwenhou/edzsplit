using System.Diagnostics;
using System.Text;

namespace EdzSplit;

/// <summary>Thin wrapper around the 7-Zip CLI (`7z`). 7-Zip handles the 7z container;
/// we intentionally keep all 7z logic behind this class.</summary>
internal static class SevenZipCli
{
    public const string EnvSevenZip = "EDZSPLIT_7Z";

    private static readonly string[] DefaultLocations =
    {
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
        "/usr/bin/7z",
        "/usr/local/bin/7z"
    };

    public static string Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvSevenZip);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var name in new[] { "7z", "7z.exe", "7za", "7zr" })
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        foreach (var location in DefaultLocations)
            if (File.Exists(location))
                return location;

        throw new FileNotFoundException(
            "7-Zip (7z) was not found. Install p7zip on Linux (e.g. 'sudo apt install p7zip-full') " +
            "or 7-Zip on Windows, or point EDZSPLIT_7Z at the 7z executable.");
    }

    public static (int ExitCode, string StdOut, string StdErr) Run(string sevenZip, string arguments, string workingDir = null, int timeoutMs = 3600_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sevenZip,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDir))
            psi.WorkingDirectory = workingDir;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 7z.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"7z timed out after {timeoutMs} ms: {arguments}");
        }
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Lists archive entries with sizes and CRCs using `7z l -slt -sccUTF-8`.</summary>
    public static List<ArchiveMember> List(string sevenZip, string archive)
    {
        var (code, stdout, stderr) = Run(sevenZip, $"l -slt -sccUTF-8 \"{archive}\"", timeoutMs: 600_000);
        if (code != 0)
            throw new InvalidDataException($"7z list failed (exit {code}) for {archive}: {stderr}");

        var result = new List<ArchiveMember>();
        var archiveFull = Path.GetFullPath(archive);
        foreach (var block in SplitBlocks(stdout))
        {
            if (!TryGet(block, "Path", out var path) || string.IsNullOrWhiteSpace(path))
                continue;
            if (string.Equals(Path.GetFullPath(path), archiveFull, StringComparison.OrdinalIgnoreCase))
                continue; // the archive file itself
            var member = new ArchiveMember
            {
                Path = path,
                Size = TryGet(block, "Size", out var size) && long.TryParse(size, out var s) ? s : 0,
                PackedSize = TryGet(block, "Packed Size", out var packed) && long.TryParse(packed, out var p) ? p : 0,
                Crc = TryGet(block, "CRC", out var crc) && ulong.TryParse(crc, System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : 0
            };
            result.Add(member);
        }
        return result;
    }

    /// <summary>`7z t` — structural integrity check.</summary>
    public static (int ExitCode, string Output) Test(string sevenZip, string archive)
    {
        var (code, stdout, stderr) = Run(sevenZip, $"t -sccUTF-8 \"{archive}\"", timeoutMs: 3_600_000);
        return (code, stdout + stderr);
    }

    /// <summary>Extracts the given members into <paramref name="outDir"/> preserving archive-relative layout.
    /// Uses `-spd` so member names are treated literally. Returns 7z's exit code.</summary>
    public static (int ExitCode, string Output) ExtractMembers(string sevenZip, string archive, IEnumerable<string> members, string outDir, string workDir)
    {
        Directory.CreateDirectory(outDir);
        var listFile = Path.Combine(workDir, ".extract-list-" + Guid.NewGuid().ToString("N") + ".txt");
        // 7-Zip stores names with forward slashes on every OS; always pass those.
        File.WriteAllLines(listFile, members.Select(m => ArchiveIndex.Normalize(m).Replace('\\', '/')), Encoding.UTF8);
        try
        {
            var (code, stdout, stderr) = Run(sevenZip,
                $"x -y -spd -scsUTF-8 -sccUTF-8 -bso0 -bse0 -bsp0 \"{archive}\" \"@{listFile}\" -o\"{outDir}\"",
                timeoutMs: 3_600_000);
            return (code, stdout + stderr);
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    /// <summary>Extracts every member into <paramref name="outDir"/>.</summary>
    public static (int ExitCode, string Output) ExtractAll(string sevenZip, string archive, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var (code, stdout, stderr) = Run(sevenZip,
            $"x -y -spd -sccUTF-8 -bso0 -bse0 -bsp0 \"{archive}\" -o\"{outDir}\"",
            timeoutMs: 3_600_000);
        return (code, stdout + stderr);
    }

    public static bool IsSevenZipFile(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[6];
        if (fs.Read(header) < 6) return false;
        return header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
               header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C;
    }

    public static bool IsZipFile(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        if (fs.Read(header) < 4) return false;
        return header[0] == 0x50 && header[1] == 0x4B;
    }

    private static IEnumerable<Dictionary<string, string>> SplitBlocks(string stdout)
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(stdout ?? string.Empty);
        string line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) { yield return current; current = new(StringComparer.OrdinalIgnoreCase); }
                continue;
            }
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();
            if (key.Length > 0) current[key] = value;
        }
        if (current.Count > 0) yield return current;
    }

    private static bool TryGet(Dictionary<string, string> block, string key, out string value)
        => block.TryGetValue(key, out value);
}

internal sealed class ArchiveMember
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public long PackedSize { get; set; }
    public ulong Crc { get; set; }
}
