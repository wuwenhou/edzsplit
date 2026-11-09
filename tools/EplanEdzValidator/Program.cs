using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Eplan.EplApi.MasterData;

namespace EdzSplit.Validator
{
    /// <summary>
    /// Test-only EPLAN oracle. Creates/opens a SCRATCH parts database,
    /// imports EDZ files and asserts the results.
    ///
    /// Hard safety rules:
    ///   1. The database path must be under %TEMP%\edzsplit-oracle unless
    ///      --allow-outside-temp is passed.
    ///   2. A path containing "ESS_part001" is always rejected
    ///      (main Oumijia/EPLAN database guard).
    /// </summary>
    internal static class Program
    {
        private const string MainDbGuardToken = "ESS_part001";
        private const string DefaultApiDir = @"C:\Program Files\EPLAN\Platform\2022.0.3\Bin";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                RegisterResolver();
                var parsed = ParseArgs(args);
                if (parsed == null || parsed.Command == null)
                {
                    PrintUsage();
                    return 2;
                }

                GuardDatabasePath(parsed);

                switch (parsed.Command)
                {
                    case "create": return CmdCreate(parsed);
                    case "open-check": return CmdOpenCheck(parsed);
                    case "list": return CmdList(parsed);
                    case "import": return CmdImport(parsed);
                    default:
                        Console.Error.WriteLine("Unknown command: " + parsed.Command);
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.GetType().FullName + ": " + ex.Message);
                return 10;
            }
        }

        // ------------------------------------------------------------ resolver

        private static void RegisterResolver()
        {
            var apiDir = Environment.GetEnvironmentVariable("EPLAN_API_DIR");
            if (String.IsNullOrWhiteSpace(apiDir) || !Directory.Exists(apiDir))
            {
                apiDir = DefaultApiDir;
            }
            if (!Directory.Exists(apiDir)) return; // compile-time HintPath is not used without resolver

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                try
                {
                    var name = new AssemblyName(eventArgs.Name).Name + ".dll";
                    var candidate = Path.Combine(apiDir, name);
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                catch { }
                return null;
            };
        }

        // ------------------------------------------------------------ args

        private sealed class Args
        {
            public string Command;
            public string Db;
            public bool AllowOutsideTemp;
            public bool CreateIfMissing;
            public List<string> Edzs = new List<string>();
            public List<string> ExpectPartNumbers = new List<string>();
            public string Scheme;
        }

        private static Args ParseArgs(string[] args)
        {
            if (args == null || args.Length == 0) return null;
            var result = new Args();
            result.Command = args[0];
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--db": result.Db = Next(args, ref i); break;
                    case "--edz": result.Edzs.Add(Next(args, ref i)); break;
                    case "--expect": result.ExpectPartNumbers.Add(Next(args, ref i)); break;
                    case "--scheme": result.Scheme = Next(args, ref i); break;
                    case "--allow-outside-temp": result.AllowOutsideTemp = true; break;
                    case "--create-if-missing": result.CreateIfMissing = true; break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal))
                            throw new ArgumentException("Unknown option: " + a);
                        result.Edzs.Add(a);
                        break;
                }
            }
            if (String.IsNullOrWhiteSpace(result.Db))
                throw new ArgumentException("--db <path> is required.");
            return result;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[i]);
            return args[++i];
        }

        private static void GuardDatabasePath(Args args)
        {
            var full = Path.GetFullPath(args.Db);
            if (full.IndexOf(MainDbGuardToken, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException(
                    "Refusing to touch the main parts database (guard token '" + MainDbGuardToken + "'): " + full);

            var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "edzsplit-oracle"));
            var insideTemp = full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
            if (!insideTemp && !args.AllowOutsideTemp)
                throw new InvalidOperationException(
                    "Scratch database must live under " + tempRoot + " (got " + full + "). " +
                    "Pass --allow-outside-temp only for a verified-safe directory.");

            args.Db = full;
        }

        // ------------------------------------------------------------ commands

        private static int CmdCreate(Args args)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(args.Db));
            if (File.Exists(args.Db) && new FileInfo(args.Db).Length > 0)
            {
                Console.WriteLine("already-exists: " + args.Db + " (" + new FileInfo(args.Db).Length + " bytes)");
                return 0;
            }
            var mgmt = new MDPartsManagement();
            mgmt.CreateDatabase(args.Db);
            Console.WriteLine("created: " + args.Db + " (" + new FileInfo(args.Db).Length + " bytes)");
            return 0;
        }

        private static int CmdOpenCheck(Args args)
        {
            using (var db = OpenDatabase(args))
            {
                Console.WriteLine("open-ok: " + args.Db);
                Console.WriteLine("parts: " + CountParts(db));
                Console.WriteLine("version: " + SafeToString(() => db.Version));
                Console.WriteLine("read-only: " + SafeToString(() => db.IsReadOnly));
            }
            return 0;
        }

        private static int CmdList(Args args)
        {
            using (var db = OpenDatabase(args))
            {
                var parts = db.Parts.Cast<MDPart>().ToList();
                Console.WriteLine("parts: " + parts.Count);
                foreach (var part in parts)
                {
                    Console.WriteLine("part: " + SafeToString(() => part.PartNr) + " / variant=" + SafeToString(() => part.Variant));
                }
            }
            return 0;
        }

        private static int CmdImport(Args args)
        {
            if (args.Edzs.Count == 0) throw new ArgumentException("At least one EDZ path is required.");

            var parentDir = Path.GetDirectoryName(args.Db);
            var before = SnapshotDirectory(parentDir);

            var mgmt = new MDPartsManagement();
            using (var db = OpenDatabase(args, mgmt))
            {
                var beforeCount = CountParts(db);

                var files = new ArrayList();
                foreach (var edz in args.Edzs)
                {
                    var fullEdz = Path.GetFullPath(edz);
                    if (!File.Exists(fullEdz)) throw new FileNotFoundException("EDZ not found", fullEdz);
                    files.Add(fullEdz);
                }

                var scheme = args.Scheme;
                if (String.IsNullOrWhiteSpace(scheme))
                {
                    scheme = ImportWithFallback(mgmt, files);
                }
                else
                {
                    mgmt.ImportParts(files, "IXPartsImportExportEdz",
                        MDPartsManagement.ImportMode.UpdateAndAppend,
                        false,
                        MDPartsManagement.ImportIdentifier.PartNumber,
                        scheme);
                }
                Console.WriteLine("import-scheme: " + scheme);

                var after = SnapshotDirectory(parentDir);
                Console.WriteLine("parts-before: " + beforeCount);
                Console.WriteLine("parts-after: " + CountParts(db));
                PrintSnapshotDiff(before, after);

                int failures = 0;
                foreach (var expected in args.ExpectPartNumbers)
                {
                    bool exists = false;
                    try { exists = db.ExistsPart(expected); } catch (Exception ex) { Console.WriteLine("expect-error: " + expected + " -> " + ex.Message); }
                    Console.WriteLine("expect: " + expected + " exists=" + exists);
                    if (!exists) failures++;
                }

                // Best-effort property dump (macro / picture / external document references)
                foreach (var expected in args.ExpectPartNumbers)
                {
                    try
                    {
                        var part = db.GetPart(expected);
                        if (part == null) continue;
                        DumpProperties(part);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("props-error: " + expected + " -> " + ex.Message);
                    }
                }

                return failures == 0 ? 0 : 5;
            }
        }

        // ------------------------------------------------------------ helpers

        private static MDPartsDatabase OpenDatabase(Args args)
        {
            return OpenDatabase(args, null);
        }

        private static MDPartsDatabase OpenDatabase(Args args, MDPartsManagement mgmt)
        {
            if (!File.Exists(args.Db))
            {
                if (!args.CreateIfMissing)
                    throw new FileNotFoundException("Database not found (use create first, or --create-if-missing): ", args.Db);
                Directory.CreateDirectory(Path.GetDirectoryName(args.Db));
                mgmt = mgmt ?? new MDPartsManagement();
                mgmt.CreateDatabase(args.Db);
                Console.WriteLine("created-on-demand: " + args.Db);
            }
            mgmt = mgmt ?? new MDPartsManagement();
            try
            {
                var db = new MDPartsDatabase(args.Db);
                return db;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ctor failed, falling back to OpenDatabase: " + ex.Message);
                mgmt.OpenDatabase(args.Db);
                return mgmt.SelectedPartsDatabase;
            }
        }

        private static string ImportWithFallback(MDPartsManagement mgmt, ArrayList files)
        {
            var candidates = new List<string>();
            try
            {
                var settings = new Eplan.EplApi.Base.Settings();
                foreach (var path in new[] { "USER.PartsManagementGui.FieldMapping.LastUsed", "STATION.PartsManagementGui.FieldMapping.LastUsed" })
                {
                    try
                    {
                        if (settings.ExistSetting(path))
                        {
                            var value = settings.GetStringSetting(path, 0);
                            if (!String.IsNullOrWhiteSpace(value) && !candidates.Contains(value.Trim()))
                                candidates.Add(value.Trim());
                        }
                    }
                    catch { }
                }
            }
            catch { }

            const string fallback = "Standardvorgabe EPLAN";
            if (!candidates.Contains(fallback)) candidates.Add(fallback);

            Exception last = null;
            foreach (var scheme in candidates.ToArray())
            {
                try
                {
                    mgmt.ImportParts(files, "IXPartsImportExportEdz",
                        MDPartsManagement.ImportMode.UpdateAndAppend,
                        false,
                        MDPartsManagement.ImportIdentifier.PartNumber,
                        scheme);
                    return scheme;
                }
                catch (Exception ex)
                {
                    var msg = ex.Message ?? string.Empty;
                    var looksLikeScheme = msg.IndexOf("S030060", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          msg.IndexOf("Invalid scheme", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!looksLikeScheme) throw;
                    last = ex;
                    Console.Error.WriteLine("scheme rejected: " + scheme);
                }
            }
            throw last ?? new InvalidOperationException("No field-mapping scheme accepted.");
        }

        private static int CountParts(MDPartsDatabase db)
        {
            return db.Parts.Cast<MDPart>().Count();
        }

        private static string SafeToString(Func<object> getter)
        {
            try { return Convert.ToString(getter()) ?? "(null)"; } catch (Exception ex) { return "<error: " + ex.Message + ">"; }
        }

        private static void DumpProperties(MDPart part)
        {
            var targets = new[]
            {
                "ARTICLE_PARTNR", "ARTICLE_MACRO", "ARTICLE_MACRONAME", "ARTICLE_GROUPSYMBOLMACRO",
                "ARTICLE_PICTUREFILE", "ARTICLE_EXTERNAL_DOCUMENT_1", "ARTICLE_EXTERNAL_DOCUMENT_2",
                "ARTICLE_EXTERNAL_DOCUMENT_3", "ARTICLE_EXTERNAL_DOCUMENT_4", "ARTICLE_EXTERNAL_DOCUMENT_5",
                "ARTICLE_MANUFACTURER", "ARTICLE_VISIBLENAME", "ARTICLE_ORDERNR", "ARTICLE_TYPE",
                "ARTICLE_VARIANT"
            };
            var props = part.Properties;
            if (props == null) return;
            foreach (var name in targets)
            {
                try
                {
                    var prop = props.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null) continue;
                    var value = prop.GetValue(props);
                    Console.WriteLine("prop: " + name + " = " + (value == null ? "(null)" : value.ToString()));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("prop-error: " + name + " -> " + ex.Message);
                }
            }
        }

        // ---------------------------------------------------- directory diff

        private static Dictionary<string, long> SnapshotDirectory(string dir)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return map;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { map[file] = new FileInfo(file).Length; } catch { }
            }
            return map;
        }

        private static void PrintSnapshotDiff(Dictionary<string, long> before, Dictionary<string, long> after)
        {
            foreach (var kvp in after)
            {
                if (!before.TryGetValue(kvp.Key, out var size))
                    Console.WriteLine("file-added: " + kvp.Key + " (" + kvp.Value + " bytes)");
                else if (size != kvp.Value)
                    Console.WriteLine("file-changed: " + kvp.Key + " (" + size + " -> " + kvp.Value + " bytes)");
            }
            foreach (var kvp in before)
            {
                if (!after.ContainsKey(kvp.Key))
                    Console.WriteLine("file-removed: " + kvp.Key);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"EplanEdzValidator — EPLAN import oracle (test-only)

Usage:
  EplanEdzValidator create     --db <scratch.adb>
  EplanEdzValidator open-check --db <scratch.adb>
  EplanEdzValidator list       --db <scratch.adb>
  EplanEdzValidator import     --db <scratch.adb> [--create-if-missing]
                               --edz <file.edz> [--edz <file2.edz>...]
                               [--expect <partnr>]... [--scheme <name>]

Safety: database must live under %TEMP%\edzsplit-oracle and must not contain
token 'ESS_part001'. Run with EPLAN_API_DIR set to override the API Bin folder.");
        }
    }
}
