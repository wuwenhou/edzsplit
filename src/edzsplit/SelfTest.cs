using System.Text;

namespace EdzSplit;

/// <summary>
/// End-to-end smoke test without EPLAN: fabricate a two-part EDZ in memory,
/// split it with the real pipeline, verify both outputs.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "edzsplit-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outDir);
        var source = Path.Combine(root, "synthetic-two-parts.edz");

        try
        {
            // ---- 1) Fabricate a synthetic two-part EDZ -------------------------------
            var partA = """
                <?xml version="1.0" encoding="utf-8" ?>
                <partsmanagement count="1" type="EPLAN.PartsManagement" version="2022.0">
                 <part P_ARTICLE_PARTNR="SYN.001" P_ARTICLE_MANUFACTURER="SYN" P_ARTICLE_MACRO="$(MD_MACROS)\Syn\A.ema" P_ARTICLE_PICTUREFILE="$(MD_IMG)\Syn\a.png" P_ARTICLE_EXTERNAL_DOCUMENT_1="https://example.com/datasheet">
                  <variant P_ARTICLE_VARIANT="1" />
                 </part>
                </partsmanagement>
                """;
            var partB = """
                <?xml version="1.0" encoding="utf-8" ?>
                <partsmanagement count="1" type="EPLAN.PartsManagement" version="2022.0">
                 <part P_ARTICLE_PARTNR="SYN.002" P_ARTICLE_MANUFACTURER="SYN">
                  <variant P_ARTICLE_VARIANT="1" />
                 </part>
                </partsmanagement>
                """;
            var manufacturer = """
                <?xml version="1.0" encoding="utf-8" ?>
                <partsmanagement count="1" type="EPLAN.PartsManagement" version="2022.0">
                 <manufacturer P_ARTICLE_MANUFACTURER="SYN" />
                </partsmanagement>
                """;
            var manifest = """
                <?xml version="1.0" encoding="utf-8" ?>
                <manifest version="2.0">
                 <packages>
                  <package type="part" key="SYN.001" name="SYN.001">
                   <items>
                    <item type="partxml" name="part" locator="SYN.001.part.xml"/>
                    <item type="partxml" name="manufacturer" locator="SYN.manufacturer.xml"/>
                    <item type="macro" name="graphicmacro" locator="Syn\A.ema"/>
                    <item type="picture" name="picturefile" locator="Syn\a.png"/>
                   </items>
                  </package>
                  <package type="part" key="SYN.002" name="SYN.002">
                   <items>
                    <item type="partxml" name="part" locator="SYN.002.part.xml"/>
                    <item type="partxml" name="manufacturer" locator="SYN.manufacturer.xml"/>
                   </items>
                  </package>
                 </packages>
                </manifest>
                """;

            var files = new List<(string Name, byte[] Bytes)>
            {
                ("items\\partxml\\SYN.001.part.xml", Encoding.UTF8.GetBytes(partA)),
                ("items\\partxml\\SYN.002.part.xml", Encoding.UTF8.GetBytes(partB)),
                ("items\\partxml\\SYN.manufacturer.xml", Encoding.UTF8.GetBytes(manufacturer)),
                ("items\\macro\\Syn\\A.ema", Encoding.ASCII.GetBytes("FAKE-MACRO-A")),
                ("items\\picture\\Syn\\a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                ("manifest.xml", Encoding.UTF8.GetBytes(manifest))
            };

            SevenZipCopyWriter.Write(source, files.Select(f => new SevenZipCopyEntry
            {
                Key = f.Name,
                Size = f.Bytes.LongLength,
                Open = () => new MemoryStream(f.Bytes, writable: false)
            }).ToList());
            Console.WriteLine("selftest: synthetic EDZ written -> " + source);

            // ---- 2) Verify the synthetic source round-trips through 7z ---------------
            var verifySource = Verify.Run(source);
            Console.WriteLine("selftest: source verify Ok=" + verifySource.Ok + " errors=" + verifySource.Errors.Count);
            if (!verifySource.Ok) return Fail("Source EDZ failed structural verification", root, keep: true);

            // ---- 3) Split it ----------------------------------------------------------
            var report = Splitter.Run(new SplitOptions
            {
                Archive = source,
                OutDir = outDir,
                Closure = ClosureMode.Full,
                FixRefs = "auto",
                VerifyStructural = true
            });
            Console.WriteLine($"selftest: split planned={report.PartsPlanned} written={report.PartsWritten} failed={report.PartsFailed}");
            if (report.PartsFailed > 0) return Fail("Split reported failures: " + string.Join("; ", report.Failed), root);
            if (report.PartsWritten != 2) return Fail($"Expected 2 outputs, got {report.PartsWritten}", root);

            // ---- 4) Verify both outputs -------------------------------------------------
            var edzA = Path.Combine(outDir, "SYN.001.edz");
            var edzB = Path.Combine(outDir, "SYN.002.edz");
            foreach (var path in new[] { edzA, edzB })
            {
                var result = Verify.Run(path);
                Console.WriteLine($"selftest: {Path.GetFileName(path)} verify Ok={result.Ok} errors={result.Errors.Count} warnings={result.Warnings.Count}");
                if (!result.Ok) return Fail("Output failed verification: " + path + " :: " + string.Join("; ", result.Errors), root);
            }

            // ---- 5) Split only one part --------------------------------------------------
            var outOne = Path.Combine(root, "out-one");
            var one = Splitter.Run(new SplitOptions
            {
                Archive = source,
                OutDir = outOne,
                Closure = ClosureMode.Full,
                OnlyPartNumbers = new List<string> { "SYN.001" }
            });
            Console.WriteLine($"selftest: --only SYN.001 -> written={one.PartsWritten}");
            if (one.PartsWritten != 1) return Fail("--only filter did not produce exactly one output", root);

            Console.WriteLine("SELFTEST PASS");
            try { Directory.Delete(root, recursive: true); } catch { }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("selftest exception: " + ex);
            return Fail(ex.Message, root, keep: true);
        }
    }

    private static int Fail(string message, string root, bool keep = false)
    {
        Console.WriteLine("SELFTEST FAIL: " + message);
        if (!keep) { try { Directory.Delete(root, recursive: true); } catch { } }
        else Console.WriteLine("selftest workspace kept at: " + root);
        return 4;
    }
}
