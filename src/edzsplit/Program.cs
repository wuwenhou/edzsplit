using System.Text;
using System.Text.Json;
using EdzSplit;

namespace EdzSplit;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitPartial = 3;
    private const int ExitFailed = 4;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return ExitUsage;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "inspect" => CmdInspect(rest),
                "split" => CmdSplit(rest),
                "verify" => CmdVerify(rest),
                "case-a" => CmdCaseA(rest),
                "selftest" => SelfTest.Run(),
                "version" or "--version" => PrintVersion(),
                _ => Unknown(command)
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return ExitUsage;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("fatal: " + ex.GetType().Name + ": " + ex.Message);
            return ExitFailed;
        }
    }

    // ------------------------------------------------------------- inspect

    private static int CmdInspect(string[] args)
    {
        var json = HasFlag(args, "--json");
        var positional = Positional(args);
        if (positional.Count == 0) throw new UsageException("inspect requires <archive>");
        var archive = Path.GetFullPath(positional[0]);
        if (!File.Exists(archive)) throw new UsageException("Archive not found: " + archive);

        var sevenZip = SevenZipCli.Find();
        var members = SevenZipCli.List(sevenZip, archive);
        var index = ArchiveIndex.FromMembers(members);

        var zip = SevenZipCli.IsZipFile(archive);
        var sevenZ = SevenZipCli.IsSevenZipFile(archive);
        var workDir = Path.Combine(Path.GetTempPath(), "edzsplit-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string manifestText = null;
            try
            {
                var extract = SevenZipCli.ExtractMembers(sevenZip, archive, new[] { "manifest.xml" }, workDir, workDir);
                var path = Path.Combine(workDir, "manifest.xml");
                if (extract.ExitCode == 0 && File.Exists(path)) manifestText = File.ReadAllText(path, Encoding.UTF8);
            }
            catch { }

            ManifestModel model = null;
            if (manifestText != null)
            {
                try { model = ManifestModel.Parse(manifestText); } catch { }
            }

            var nestedEdz = members.Count(m => m.Path.EndsWith(".edz", StringComparison.OrdinalIgnoreCase));

            var stats = new Dictionary<string, object?>
            {
                ["archive"] = archive,
                ["container"] = sevenZ ? "7z" : zip ? "zip" : "unknown",
                ["fileSizeBytes"] = new FileInfo(archive).Length,
                ["entries"] = members.Count,
                ["totalUncompressedBytes"] = members.Sum(m => m.Size),
                ["nestedEdzEntries"] = nestedEdz,
                ["hasManifest"] = model != null,
                ["manifestVersion"] = model?.Version,
                ["packages"] = model?.Packages.Count,
                ["items"] = model?.AllItems.Count()
            };

            Dictionary<string, int> packageTypes = null, itemNames = null;
            var direct = 0; var repaired = 0; var missing = 0; var missingUnique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (model != null)
            {
                packageTypes = model.Packages.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count());
                itemNames = model.AllItems.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.Count());
                stats["packageTypes"] = packageTypes;
                stats["itemNames"] = itemNames;

                foreach (var item in model.AllItems)
                {
                    if (index.TryResolve(item.MemberPath, out _, out var rep))
                    {
                        if (rep) repaired++; else direct++;
                    }
                    else
                    {
                        missing++;
                        missingUnique.Add(item.MemberPath);
                    }
                }
                stats["refs"] = new { direct, repaired, missing, missingUnique = missingUnique.Count };
                stats["missingUniqueSamples"] = missingUnique.Take(20).ToArray();
                stats["partPackages"] = model.Packages.Count(p => string.Equals(p.Type, "part", StringComparison.OrdinalIgnoreCase));
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
                return ExitOk;
            }

            Console.WriteLine("Archive   : " + archive);
            Console.WriteLine("Container : " + stats["container"] + "  (" + new FileInfo(archive).Length + " bytes)");
            Console.WriteLine("Entries   : " + members.Count + "   (uncompressed " + members.Sum(m => m.Size) + " bytes)");
            Console.WriteLine("Nested EDZ: " + nestedEdz);
            if (model == null)
            {
                Console.WriteLine("Manifest  : MISSING or unreadable");
                return ExitFailed;
            }
            Console.WriteLine("Manifest  : v" + model.Version);
            Console.WriteLine("Packages  : " + model.Packages.Count + "  (" + string.Join(", ", packageTypes!.Select(kv => kv.Key + "=" + kv.Value)) + ")");
            Console.WriteLine("Items     : " + model.AllItems.Count());
            Console.WriteLine("Refs      : direct=" + direct + " repaired=" + repaired + " missing=" + missing
                              + " (unique missing=" + missingUnique.Count + ")");
            if (nestedEdz > 0 && model.Packages.Count == 0)
                Console.WriteLine("Hint      : archive looks like Case A (EDZ files inside an archive) — use `edzsplit case-a`.");
            return ExitOk;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------- split

    private static int CmdSplit(string[] args)
    {
        var options = new SplitOptions();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException("Missing value for " + a);
            switch (a)
            {
                case "--out": options.OutDir = Next(); break;
                case "--only": options.OnlyPartNumbers.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--from-file": options.OnlyFromFile = Next(); break;
                case "--closure":
                    options.Closure = Next().ToLowerInvariant() switch
                    {
                        "none" => ClosureMode.None,
                        "related" => ClosureMode.Related,
                        "full" => ClosureMode.Full,
                        var other => throw new UsageException("Unknown closure mode: " + other)
                    };
                    break;
                case "--fix-refs":
                    var mode = Next().ToLowerInvariant();
                    if (mode is not ("auto" or "never")) throw new UsageException("--fix-refs must be auto or never");
                    options.FixRefs = mode;
                    break;
                case "--verify-structural": options.VerifyStructural = true; break;
                case "--no-verify": options.VerifyStructural = false; break;
                case "--resume": options.Resume = true; break;
                case "--keep-cache": options.KeepCache = true; break;
                case "--keep-going": options.KeepGoing = true; break;
                case "--fail-fast": options.KeepGoing = false; break;
                case "--max-closure-ratio": options.MaxClosureRatio = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--dry-run": options.DryRun = true; break;
                case "--log": options.LogPath = Next(); break;
                case "--workdir": options.WorkDir = Next(); break;
                case "--jobs": _ = Next(); break; // reserved; v1 processes sequentially
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new UsageException("Unknown option: " + a);
                    positional.Add(a);
                    break;
            }
        }

        if (positional.Count == 0) throw new UsageException("split requires <archive>");
        if (string.IsNullOrWhiteSpace(options.OutDir)) throw new UsageException("--out <dir> is required");
        options.Archive = Path.GetFullPath(positional[0]);

        var report = Splitter.Run(options);

        Console.WriteLine();
        Console.WriteLine("=== Split summary ===");
        Console.WriteLine("Planned : " + report.PartsPlanned);
        Console.WriteLine("Written : " + report.PartsWritten + "  (" + report.TotalBytesWritten + " bytes)");
        Console.WriteLine("Resume  : " + report.PartsSkippedResume + " skipped");
        Console.WriteLine("Failed  : " + report.PartsFailed);
        foreach (var failure in report.Failed.Take(20))
            Console.WriteLine("  failed: " + failure);
        if (report.Warnings.Count > 0)
            Console.WriteLine("Warnings: " + report.Warnings.Count + " (see log for details)");

        return report.ExitCode;
    }

    // ------------------------------------------------------------- verify

    private static int CmdVerify(string[] args)
    {
        var strict = HasFlag(args, "--strict");
        var positional = Positional(args);
        if (positional.Count == 0) throw new UsageException("verify requires <file.edz>");

        var failures = 0;
        foreach (var file in positional)
        {
            var result = Verify.Run(file, strict);
            Console.WriteLine("EDZ     : " + Path.GetFullPath(file));
            Console.WriteLine("  OK    : " + result.Ok);
            Console.WriteLine("  Pkgs  : " + result.Packages + "   Items: " + result.Items +
                              "   Missing: " + result.Missing + "   Manifest v" + result.ManifestVersion);
            foreach (var warning in result.Warnings.Take(10)) Console.WriteLine("  warn  : " + warning);
            foreach (var error in result.Errors.Take(20)) Console.WriteLine("  error : " + error);
            if (!result.Ok) failures++;
        }
        return failures == 0 ? ExitOk : ExitFailed;
    }

    // ------------------------------------------------------------- case-a

    private static int CmdCaseA(string[] args)
    {
        var positional = new List<string>();
        string outDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException("Missing value for " + a);
            switch (a)
            {
                case "--out": outDir = Next(); break;
                case "--verify": break; // verification always runs
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new UsageException("Unknown option: " + a);
                    positional.Add(a);
                    break;
            }
        }
        if (positional.Count == 0) throw new UsageException("case-a requires <archive>");
        if (string.IsNullOrWhiteSpace(outDir)) throw new UsageException("--out <dir> is required");
        var archive = Path.GetFullPath(positional[0]);
        Directory.CreateDirectory(outDir);

        var sevenZip = SevenZipCli.Find();
        var members = SevenZipCli.List(sevenZip, archive);
        var nested = members.Where(m => m.Path.EndsWith(".edz", StringComparison.OrdinalIgnoreCase)).ToList();
        if (nested.Count == 0)
        {
            Console.Error.WriteLine("No nested .edz entries found — not a Case A archive.");
            return ExitUsage;
        }

        Console.WriteLine("Case A archive: " + archive);
        Console.WriteLine("Nested EDZ     : " + nested.Count);

        var extract = SevenZipCli.ExtractAll(sevenZip, archive, outDir);
        if (extract.ExitCode != 0)
        {
            Console.Error.WriteLine("7z extraction failed: " + extract.Output);
            return ExitFailed;
        }

        var okCount = 0;
        var badCount = 0;
        foreach (var member in nested)
        {
            var path = Path.Combine(outDir, ArchiveIndex.Normalize(member.Path));
            if (!File.Exists(path))
            {
                Console.WriteLine("MISSING: " + member.Path);
                badCount++;
                continue;
            }
            var result = Verify.Run(path);
            if (result.Ok) { okCount++; Console.WriteLine("OK    : " + member.Path); }
            else { badCount++; Console.WriteLine("BAD   : " + member.Path + " (" + string.Join("; ", result.Errors.Take(3)) + ")"); }
        }
        Console.WriteLine($"Extracted {nested.Count} EDZ files to {outDir}: ok={okCount}, bad={badCount}");
        return badCount == 0 ? ExitOk : ExitPartial;
    }

    // ------------------------------------------------------------- misc

    private static int PrintVersion()
    {
        Console.WriteLine("edzsplit " + typeof(Program).Assembly.GetName().Version);
        return ExitOk;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine("Unknown command: " + command);
        PrintUsage();
        return ExitUsage;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"edzsplit — split a multi-device EPLAN EDZ into one EDZ per part.

USAGE
  edzsplit inspect <archive> [--json]
      Show container, entries, manifest, package/item counts, reference repair stats.

  edzsplit split <archive> --out <dir> [options]
      Options:
        --only <p1,p2>          Split only the listed part numbers.
        --from-file <file>      Part numbers to split, one per line ('#' comments).
        --closure <mode>        none | related | full   (default: full)
        --fix-refs <mode>       auto | never            (default: auto)
        --verify-structural     Verify each output EDZ (default)
        --no-verify             Skip per-output structural verification
        --resume                Skip parts already recorded as done in <out>/.edzsplit-state.jsonl
        --keep-cache            Keep the intermediate extraction cache (debugging)
        --keep-going            Continue after individual part failures (default)
        --fail-fast             Stop at the first failed part
        --max-closure-ratio <r> Warn when a closure exceeds r * source bytes (0..1)
        --dry-run               Plan only; write nothing
        --log <path>            Also append JSONL events to <path>
        --workdir <dir>         Override the intermediate work/cache directory

  edzsplit verify <file.edz>... [--strict]
      Structural verification: 7z integrity, manifest graph, reference resolution, traversal guard.

  edzsplit case-a <archive> --out <dir>
      Case A: archive already contains nested .edz files — extract and verify them.

  edzsplit selftest
      Build a synthetic two-part EDZ, split it, verify the outputs (no EPLAN needed).

EXIT CODES
  0 ok · 2 usage error · 3 finished with warnings/quarantine · 4 failed

Each command prints JSONL events to stdout; pipe to a file for logs/evidence.");
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static List<string> Positional(string[] args)
        => args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
}

internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
