using System.Security.Cryptography;
using System.Text;

namespace EdzSplit;

internal sealed class SplitOptions
{
    public string Archive = string.Empty;
    public string OutDir = string.Empty;
    public List<string> OnlyPartNumbers = new();
    public string? OnlyFromFile;
    public ClosureMode Closure = ClosureMode.Full;
    public string FixRefs = "auto"; // auto | never
    public bool VerifyStructural = true;
    public bool Resume = false;
    public bool KeepCache = false;
    public bool KeepGoing = true;
    public double MaxClosureRatio = 1.0; // 1.0 = no limit
    public bool DryRun = false;
    public string? WorkDir;
    public string? LogPath;
}

internal sealed class SplitReport
{
    public int PartsPlanned;
    public int PartsWritten;
    public int PartsSkippedResume;
    public int PartsFailed;
    public long TotalBytesWritten;
    public List<string> Failed = new();
    public List<string> Warnings = new();

    public int ExitCode => PartsFailed == 0 ? (Warnings.Count > 0 ? 3 : 0) : (PartsWritten > 0 ? 3 : 4);
}

internal static class Splitter
{
    private const string StateFileName = ".edzsplit-state.jsonl";

    public static SplitReport Run(SplitOptions options)
    {
        var sevenZip = SevenZipCli.Find();
        var report = new SplitReport();
        var events = new EventLog(options.LogPath);

        var archive = Path.GetFullPath(options.Archive);
        if (!File.Exists(archive))
            throw new FileNotFoundException("Input archive not found.", archive);
        if (!SevenZipCli.IsSevenZipFile(archive) && !SevenZipCli.IsZipFile(archive))
            throw new InvalidDataException("Input is neither 7z nor ZIP.");

        var outDir = Path.GetFullPath(options.OutDir);
        Directory.CreateDirectory(outDir);

        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var workDir = options.WorkDir ?? Path.Combine(outDir, ".work-" + runId);
        Directory.CreateDirectory(workDir);
        var cacheDir = Path.Combine(workDir, "cache");
        Directory.CreateDirectory(cacheDir);

        events.Write("split.begin", new { archive, outDir, closure = options.Closure.ToString(), fixRefs = options.FixRefs, dryRun = options.DryRun });

        try
        {
            // 1) List archive + locate manifest.xml -------------------------------
            var members = SevenZipCli.List(sevenZip, archive);
            var index = ArchiveIndex.FromMembers(members);
            events.Write("archive.listed", new { entries = members.Count, totalBytes = members.Sum(m => m.Size) });

            string manifestText;
            try
            {
                var manifestExtract = SevenZipCli.ExtractMembers(sevenZip, archive, new[] { "manifest.xml" }, cacheDir, workDir);
                var extracted = Path.Combine(cacheDir, "manifest.xml");
                if (manifestExtract.ExitCode != 0 || !File.Exists(extracted))
                {
                    // Fall back: manifest might live under a folder prefix; look for the entry by name.
                    manifestText = ReadManifestViaSearch(archive, cacheDir, workDir, index, events);
                }
                else
                {
                    manifestText = File.ReadAllText(extracted, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                events.Write("manifest.error", new { error = ex.Message });
                throw;
            }

            var model = ManifestModel.Parse(manifestText);
            events.Write("manifest.parsed", new
            {
                packages = model.Packages.Count,
                items = model.AllItems.Count(),
                packageTypes = model.Packages.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count()),
                itemNameKinds = model.AllItems.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.Count())
            });

            // 2) Select target parts ----------------------------------------------
            var partPackages = model.Packages
                .Where(p => string.Equals(p.Type, "part", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var only = options.OnlyPartNumbers.ToList();
            if (!string.IsNullOrWhiteSpace(options.OnlyFromFile) && File.Exists(options.OnlyFromFile))
                only.AddRange(File.ReadLines(options.OnlyFromFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)));

            List<Package> selected;
            if (only.Count > 0)
            {
                var wanted = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
                selected = partPackages.Where(p => wanted.Contains(p.Key)).ToList();
                var missing = wanted.Except(selected.Select(p => p.Key), StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                {
                    foreach (var miss in missing)
                        events.Write("part.not-found", new { partnr = miss });
                    report.Warnings.Add("Parts not found in archive: " + string.Join(", ", missing));
                }
            }
            else
            {
                selected = partPackages;
            }
            report.PartsPlanned = selected.Count;

            // 3) Extract whole archive once into the cache -------------------------
            if (!options.DryRun)
            {
                var extract = SevenZipCli.ExtractAll(sevenZip, archive, cacheDir);
                if (extract.ExitCode != 0)
                    throw new InvalidDataException($"7z extraction failed (exit {extract.ExitCode}): {Tail(extract.Output)}");
                events.Write("cache.extracted", new { cacheDir });
            }

            string? ReadPayload(string memberPath)
            {
                var normalized = ArchiveIndex.DiskPath(cacheDir, memberPath);
                try
                {
                    if (!File.Exists(normalized)) return null;
                    return File.ReadAllText(normalized, Encoding.UTF8);
                }
                catch { return null; }
            }

            // 4) Build the graph ----------------------------------------------------
            var graph = DependencyGraph.Build(model, index, ReadPayload);

            // 5) Resume state --------------------------------------------------------
            var statePath = Path.Combine(outDir, StateFileName);
            var done = options.Resume ? LoadDone(statePath) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            events.Write("resume.state", new { resume = options.Resume, alreadyDone = done.Count });

            // 6) Per-part split ------------------------------------------------------
            foreach (var part in selected)
            {
                var partKey = part.Key;
                try
                {
                    if (done.Contains(partKey))
                    {
                        report.PartsSkippedResume++;
                        continue;
                    }

                    var closure = graph.ComputeClosure(part, options.Closure, ReadPayload);
                    var items = closure.Items.DistinctBy(i => ArchiveIndex.Normalize(i.MemberPath), StringComparer.OrdinalIgnoreCase).ToList();
                    var packages = closure.Packages.DistinctBy(NodeId.Package, StringComparer.OrdinalIgnoreCase).ToList();

                    // Closure size guard rail.
                    var estimatedBytes = items.Sum(i => graph.Resolve(i)?.Size ?? 0);
                    var sourceBytes = new FileInfo(archive).Length;
                    if (options.MaxClosureRatio < 1.0 && sourceBytes > 0 && estimatedBytes > (long)(sourceBytes * options.MaxClosureRatio))
                        report.Warnings.Add($"Closure for {partKey} is {estimatedBytes:N0} bytes (> {options.MaxClosureRatio:P0} of source)");

                    if (options.DryRun)
                    {
                        report.PartsWritten++;
                        events.Write("part.plan", new
                        {
                            part = partKey,
                            packages = packages.Select(p => p.Type + ":" + p.Key).ToArray(),
                            items = items.Count,
                            estimatedBytes
                        });
                        continue;
                    }

                    var edzPath = Path.Combine(outDir, SanitizeFileName(partKey) + ".edz");
                    var tmpPath = edzPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

                    // Build payload rewrites (auto mode): fix non-ASCII refs to the archive's sanitized names.
                    var payloadCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    if (string.Equals(options.FixRefs, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var item in items)
                        {
                            if (!string.Equals(item.Type, "partxml", StringComparison.OrdinalIgnoreCase)) continue;
                            var resolved = graph.Resolve(item);
                            if (resolved == null) continue;
                            var full = ArchiveIndex.DiskPath(cacheDir, resolved.Path);
                            if (!File.Exists(full)) continue;
                            var payload = File.ReadAllText(full, Encoding.UTF8);
                            var rewritten = RewritePayloadRefs(payload, index, out var rewrites);
                            if (rewrites > 0)
                            {
                                payloadCache[ArchiveIndex.Normalize(resolved.Path)] = Encoding.UTF8.GetBytes(rewritten);
                                events.Write("payload.rewritten", new { part = partKey, item = item.MemberPath, rewrites });
                            }
                        }
                    }

                    // Clone the manifest: keep only this closure, repair locators when resolved-by-sanitize.
                    var manifestClone = new ManifestModel { Version = model.Version };
                    foreach (var package in packages)
                    {
                        var clone = package.Clone();
                        foreach (var item in clone.Items)
                        {
                            var source = package.Items.First(i => ReferenceEquals(i, item) || (i.Type == item.Type && i.Name == item.Name && i.Locator == item.Locator));
                            var resolved = graph.Resolve(source);
                            if (resolved != null)
                            {
                                // Compare normalized paths — 7z lists forward slashes on Linux, backslashes on Windows.
                                var normalizedResolved = ArchiveIndex.Normalize(resolved.Path);
                                if (!string.Equals(normalizedResolved, item.MemberPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Rewrite locator to the actual archive entry name (keep sub-directories).
                                    var prefix = $"items\\{item.Type}\\";
                                    item.Locator = normalizedResolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                        ? normalizedResolved.Substring(prefix.Length)
                                        : normalizedResolved;
                                    events.Write("locator.repaired", new { part = partKey, from = source.Locator, to = item.Locator });
                                }
                            }
                        }
                        manifestClone.Packages.Add(clone);
                    }
                    var missingItems = packages.SelectMany(p => p.Items).Where(i => graph.IsMissing(i)).ToList();
                    foreach (var missingItem in missingItems)
                    {
                        report.Warnings.Add($"Missing item for {partKey}: {missingItem.Type}/{missingItem.Locator}");
                        events.Write("item.missing", new { part = partKey, type = missingItem.Type, name = missingItem.Name, locator = missingItem.Locator });
                    }

                    // Entries: items first, manifest.xml LAST (EPLAN export order).
                    // Entry names come from the (possibly repaired) cloned manifest so the
                    // output is always internally consistent.
                    var entries = new List<SevenZipCopyEntry>();
                    foreach (var clonePackage in manifestClone.Packages)
                    {
                        foreach (var cloneItem in clonePackage.Items)
                        {
                            var memberPath = ArchiveIndex.Normalize(cloneItem.MemberPath);
                            if (payloadCache.TryGetValue(memberPath, out var rewritten))
                            {
                                entries.Add(new SevenZipCopyEntry
                                {
                                    Key = memberPath,
                                    Size = rewritten.LongLength,
                                    Open = () => new MemoryStream(rewritten, writable: false)
                                });
                                continue;
                            }
                            if (!index.TryResolve(memberPath, out _, out _)) continue; // missing item
                            var cachePath = ArchiveIndex.DiskPath(cacheDir, memberPath);
                            if (!File.Exists(cachePath)) continue;
                            entries.Add(new SevenZipCopyEntry
                            {
                                Key = memberPath,
                                Size = new FileInfo(cachePath).Length,
                                Open = () => File.OpenRead(cachePath)
                            });
                        }
                    }

                    var manifestBytes = Encoding.UTF8.GetBytes(manifestClone.ToXml());
                    entries.Add(new SevenZipCopyEntry
                    {
                        Key = "manifest.xml",
                        Size = manifestBytes.LongLength,
                        Open = () => new MemoryStream(manifestBytes, writable: false)
                    });

                    SevenZipCopyWriter.Write(tmpPath, entries);

                    string sha256 = ComputeSha256(tmpPath);
                    File.Move(tmpPath, edzPath, overwrite: true);

                    AppendState(statePath, new { partnr = partKey, status = "done", file = edzPath, sha256, items = entries.Count });
                    report.PartsWritten++;
                    report.TotalBytesWritten += new FileInfo(edzPath).Length;
                    events.Write("part.written", new { part = partKey, file = edzPath, sha256, entries = entries.Count });
                }
                catch (Exception ex)
                {
                    report.PartsFailed++;
                    report.Failed.Add(partKey + ": " + ex.Message);
                    events.Write("part.failed", new { part = partKey, error = ex.ToString() });
                    AppendState(Path.Combine(outDir, StateFileName), new { partnr = partKey, status = "failed", error = ex.Message });
                    if (!options.KeepGoing) throw;
                }
            }

            events.Write("split.summary", new
            {
                planned = report.PartsPlanned,
                written = report.PartsWritten,
                resumed = report.PartsSkippedResume,
                failed = report.PartsFailed,
                totalBytes = report.TotalBytesWritten,
                warnings = report.Warnings.Count
            });
        }
        finally
        {
            if (!options.KeepCache)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        foreach (var warning in report.Warnings)
            events.Write("warning", new { message = warning });

        return report;
    }

    private static string ReadManifestViaSearch(string archive, string cacheDir, string workDir, ArchiveIndex index, EventLog events)
    {
        // Try a flat extract of any entry whose name is manifest.xml (any folder).
        var candidates = index.Members
            .Where(m => string.Equals(Path.GetFileName(ItemRef.NormalizeSlashes(m.Path)), "manifest.xml", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Path)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("manifest.xml not found in archive (neither at root nor under a folder).");
        var extract = SevenZipCli.ExtractMembers(SevenZipCli.Find(), archive, candidates, cacheDir, workDir);
        foreach (var candidate in candidates)
        {
            var path = ArchiveIndex.DiskPath(cacheDir, candidate);
            if (File.Exists(path))
            {
                events.Write("manifest.found", new { entry = candidate });
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }
        throw new InvalidDataException("manifest.xml extraction failed.");
    }

    private static HashSet<string> LoadDone(string statePath)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(statePath)) return done;
        foreach (var line in File.ReadLines(statePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("partnr", out var part) &&
                    doc.RootElement.TryGetProperty("status", out var status) &&
                    string.Equals(status.GetString(), "done", StringComparison.OrdinalIgnoreCase))
                    done.Add(part.GetString()!);
            }
            catch { }
        }
        return done;
    }

    private static void AppendState(string statePath, object record)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        using var stream = new FileStream(statePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(true);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return builder.ToString();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Tail(string text, int max = 2000)
        => text.Length <= max ? text : text.Substring(text.Length - max);

    /// <summary>
    /// Rewrites reference values inside an XML payload when the referenced member
    /// only exists under its sanitized (non-ASCII -> '_') archive name. Values that
    /// resolve exactly are left untouched. Returns the (possibly unchanged) payload
    /// and the number of rewrites applied.
    /// </summary>
    private static string RewritePayloadRefs(string payload, ArchiveIndex index, out int rewrites)
    {
        rewrites = 0;
        var result = payload;
        var refs = PayloadRefs.ExtractFromXml(payload, string.Empty);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payloadRef in refs)
        {
            if (payloadRef.Kind is not ("macro" or "groupsymbolmacro" or "picture" or "externalDocument"))
                continue;
            if (!seen.Add(payloadRef.RawValue)) continue;

            var expanded = PayloadRefs.ExpandMdVariables(payloadRef.RawValue);
            if (expanded == null) continue; // external URL
            if (!index.TryResolve(expanded, out _, out var repaired)) continue;
            if (!repaired) continue; // resolves exactly or does not exist at all

            var newValue = SanitizeTokenTail(payloadRef.RawValue);
            if (newValue == payloadRef.RawValue) continue;

            result = result.Replace(payloadRef.RawValue, newValue, StringComparison.Ordinal);
            rewrites++;
        }
        return result;
    }

    /// <summary>Keeps the leading "$(MD_...)\…\" token and sanitizes every non-ASCII character in the tail.</summary>
    private static string SanitizeTokenTail(string rawValue)
    {
        var tokens = new[] { "$(MD_MACROS)", "$(MD_IMG)", "$(MD_IMAGES)", "$(MD_DOCUMENTS)", "$(MD_DOC)", "$(MD_MISC)" };
        var normalized = ItemRef.NormalizeSlashes(rawValue);
        foreach (var token in tokens)
        {
            var idx = normalized.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var tail = normalized.Substring(idx + token.Length).TrimStart('\\');
            var sanitizedTail = new System.Text.StringBuilder(tail.Length);
            foreach (var ch in tail) sanitizedTail.Append(ch < 128 ? ch : '_');
            return normalized.Substring(0, idx + token.Length) + "\\" + sanitizedTail;
        }
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized) builder.Append(ch < 128 ? ch : '_');
        return builder.ToString();
    }
}
