# Research report: Splitting multi-device EPLAN EDZ files on Linux (no EPLAN runtime)

**Project:** edzsplit · **Version:** 0.1.1 · **Date:** 2026-11-09 · **License:** MIT · **Authors:** edzsplit contributors

> **PROOF STATUS (read first):** The tool has been verified end-to-end at the **structural** level (7z integrity, manifest parsing, resolvable item references, path-traversal guard) on real manufacturer data: EKF (4 540 parts), Theben (86 nested EDZ), Rittal/FESTO (`parts.edz`). **Importing an output EDZ into a live EPLAN Parts Database was NOT executed in this session** — the development machine cannot create or open a scratch parts database (details in §10). Because the brief's success criterion requires a real import, this report **does not claim the problem is solved**; it proves the read/plan/split/write/verify pipeline works, and isolates the single missing step: running the import oracle (§10.2).

---

## 1. Executive summary

An EPLAN `.edz` file carries "packages" (parts, manufacturers, constructions, connection points, accessory lists…) and "items" (partxml, macros, pictures, documents, contours). Two shapes exist:

* **Case A** — an archive that already contains single-part EDZ files (e.g. `Theben.7z`).
* **Case B** — one EDZ holding thousands of devices (e.g. `EKF.edz`, 265 MB, 4 540 parts, 17 658 item references).

edzsplit reads EDZ files (7z or ZIP), builds the part → resource dependency graph, emits one valid EDZ per part, and self-verifies the outputs. The whole pipeline runs on Linux (Ubuntu 24.04), **without EPLAN, Windows or a license**.

**Proven:** 100 % of the 17 658 EKF references resolve (UTF‑8-aware listing); a full 4 540-part split completes in **4 min 06 s** with **0 failures**; 76/76 spot verifications pass; `--resume` works (4 540 skipped); Case A extracted 86/86 EDZ with all verifications OK; **60/60** non-manifest members of the 15 re-emitted `parts.edz` outputs are byte-identical to the source.

**Not proven:** the actual EPLAN import (blocked environment — §10). Output is written as uncompressed 7z "Copy" blocks (no compression, ~2× larger) that 7‑Zip itself validates.

## 2. Problem and scope

Original brief: a standalone Linux program that reads EDZ (ZIP/7Z), identifies all devices/product codes, extracts per-part metadata, resolves related resources, creates one valid EDZ per part preserving information and resources, validates the outputs, and verifies they import into EPLAN. Must handle multi‑GB archives, streaming, resume after interruption.

Out of scope: downloading from the EPLAN Data Portal; repairing the source database; writing back into EPLAN.

## 3. EDZ anatomy (field analysis)

Samples: 86 Theben EDZ, `parts.edz` (15 parts + 1 construction), `rt-05-accessorylists.edz` (206 packages), `smoke-232715.edz`, `import-smoke.edz`, `EKF.edz` (4 540 parts).

### 3.1. Container

* Container is **standard 7z** (EKF: `LZMA:12`, **non-solid**, 6 057 blocks; Theben.7z: `LZMA2:24m`, solid). ZIP is also readable (legacy).
* Official entry names use **forward slashes** (`items/macro/…`) — verified by hexdump of an official EDZ (`Theben\THB.0060002.edz`). Windows `7z` *displays* them as `\`; they are not backslashes.
* `manifest.xml` is stored **last**; payload (`items/…`) comes first (same order as official exports).
* `manifest.xml` references items by **backslash** paths in `locator` attributes (e.g. `EKF\30102Р.png`).

### 3.2. manifest.xml

```xml
<manifest version="2.0">
 <packages>
  <package type="part" key="EKF.10201" name="EKF.10201">
   <items>
    <item type="partxml" name="part" locator="EKF.10201.part.xml"/>
    <item type="partxml" name="manufacturer" locator="EKF.manufacturer.xml"/>
    <item type="picture" name="picturefile" locator="EKF\10201.png"/>
   </items>
  </package>
  …
```

* Package types observed: `part`, `manufacturer`, `construction`, `connectionpoints`, `accessorylist`, `accessoryplacement`.
* Item types: `partxml`, `macro`, `picture`, `document`, `contour` (contour never seen in samples but handled generically).
* Item `name` carries EPLAN semantics: `part`, `manufacturer`, `supplier`, `graphicmacro` (=`P_ARTICLE_MACRO`, 3D macro), `groupsymbolmacro` (=`P_ARTICLE_GROUPSYMBOLMACRO`, schematic), `picturefile`, `connectionpoints`, `construction`, `accessorylist`, `accessoryplacement`, `externalDocument1..20`.
* Every `part` package carries its own `manufacturer`/`supplier` items pointing at the **same** `*.manufacturer.xml` — official EDZ files duplicate shared members per package; the writer must deduplicate per output (see §6).

### 3.3. References between parts and resources

Part-XML attributes:

| Attribute | Meaning |
|---|---|
| `P_ARTICLE_PARTNR` | Part code (package key) |
| `P_ARTICLE_MACRO` | `$(MD_MACROS)\…\xxx.ema` — 3D macro (item `graphicmacro`) |
| `P_ARTICLE_GROUPSYMBOLMACRO` | Schematic macro (item `groupsymbolmacro`) |
| `P_ARTICLE_PICTUREFILE` | `$(MD_IMG)\…\xxx.jpg` (item `picturefile`) |
| `P_ARTICLE_EXTERNAL_DOCUMENT_n` | `$(MD_DOCUMENTS)\…` (item `document`) or a bare URL (not a file) |
| `P_ARTICLE_REF_CONSTRUCTION_NAME` | `construction` package key |
| `P_ARTICLE_REF_TERMINAL_NAME` | Base name for the `connectionpoints` item (`<name>.connectionpoints.xml`) |
| `P_ARTICLE_IS_ACCESSORY`, `P_ARTICLE_ACCESSORYID` | Accessory flags (not package keys) |

`accessorylist` (`*.accessorylist.xml`) lists `<accessorylistposition partnr="…" variant="…" placement="…"/>` pointing **backwards** from the list to parts — this is the critical **reverse edge** for closure: a part that *is* an accessory must retain the accessory lists that reference it. `construction` holds `drillingPosition` entries and references no parts. `connectionpoints` (`*.connectionpoints.xml`) belongs to the part package. `externalDocument` may be a real file (`$(MD_DOCUMENTS)\Siemens\…\3SB34000B_de.pdf`, present in the archive) or a URL.

### 3.4. Sample data health

| Sample | Size | Packages | Item refs | Notes |
|---|---|---|---|---|
| EKF.edz | 265,109,028 B | 4 540 parts | 17 658 | 100 % of refs resolve directly with UTF‑8-aware listing |
| Theben.7z | 20,499,119 B | 86 nested EDZ | — | Case A |
| parts.edz | 9,834,942 B | 15 parts + 1 construction | 76 | construction **shares the key `FES.195716`** with the part |
| rt-05-accessorylists.edz | 110,755,739 B | 172 parts, 23 accessory lists, 11 constructions | 1 238 | 95+95+10 external documents; 35 connection points |
| smoke-232715.edz | 6,043,479 B | 27 parts | 96 | two resource-rich Siemens parts |
| import-smoke.edz | 1,823 B | 1 part | 3 | minimal oracle fixture |

**Analysis correction:** an initial pass with the OEM codepage reported 421 "mojibake" references needing `_` repair. With `-scc UTF-8` listings, **all 17 658 references resolve directly**; every "mojibake" finding was an OEM display artifact. The `non-ASCII → _` repair path is retained as a fallback but is unused for EKF.

## 4. Case A

`Theben.7z` contains `Theben\THB.*.edz` (86 files). `edzsplit case-a` extracts them verbatim and runs the structural verifier on each: **86/86 OK** (see evidence). This is a safe pass-through; file contents are never parsed.

## 5. Case B: architecture and algorithm

### 5.1. Pipeline

```
detect → list (7z -slt) → index (archive members, sanitize fallback)
      → manifest parse → cache extraction (single pass, to disk)
      → graph (payload refs + reverse accessory-list edges)
      → plan per part (closure) → clone manifest (locator rewrite only on repair)
      → write (managed 7z writer) → verify → JSONL state + resume
```

* **Single-pass listing** via `7z l -slt -sccUTF-8` (accurate UTF-8 names).
* **Single-pass extraction** into `<out>/.work-<runid>/cache`, then payloads are read from disk (avoids 4 540 separate 7z processes); the cache is deleted after the run (keep with `--keep-cache`).
* **Manifest clone** keeps only the closure's packages/items; locators are rewritten **only** when an item resolves through the sanitize fallback; every rewrite is logged (`locator.repaired` / `payload.rewritten`).

### 5.2. Dependency graph

Nodes: packages (`pkg:<type>:<key>`) and items (`item:<path>`). Edges:

1. package → item (ownership from the manifest);
2. `part.xml` item → payload references (macro/groupsymbolmacro/picture/document → resolved via `$(MD_MACROS|IMG|DOCUMENTS)` to archive members; construction → the `construction` package by key, or sanitize; connectionpoints → the `connectionpoints` item by name; accessory lists) — recursive;
3. accessorylist package → `accessorylist`/`accessoryplacement` items (placement by name) — recursive;
4. reverse context: part ← accessory lists with `<accessorylistposition partnr="KEY">` (sanitize retry).

**Closure modes**
* `none` — the part and its manifest items only.
* `related` — seed part + direct resources + accessory lists referencing it + their items (no expansion to other parts).
* `full` (default per the brief) — recursive closure to the fixpoint: every pulled-in part drags in its accessory lists, constructions and connection points. **Watch the output size:** on heavily cross-referenced accessory networks this can cascade; `--max-closure-ratio` warns when an output exceeds r× source size; `--dry-run` plans without writing. EKF has no accessory lists, so `full` and `related` coincide there (4 540 outputs, ~757 MB).

**Finding the right package under a shared key (found & fixed):** `parts.edz` has `part FES.195716` and `construction FES.195716` with the **same key**. The first build keyed packages globally by key, shadowing the construction package — the `FES.195716.construction.xml` item was silently dropped. Fix: type-scoped lookup (`FindPackage("construction", …)`, `FindPart`, `_packagesByKey: key → List<Package>`). After the fix all 15 outputs verify OK and the construction landed in `FES.195716.edz`.

**Multiple item owners (found & fixed):** `EKF.manufacturer.xml` is owned by all 4 540 part packages. The first build kept a single owner per member path — expanding it dragged package 2 into part 1's closure (the synthetic selftest showed SYN.001's output containing SYN.002's package; the writer then hit a duplicate entry). Fix: `path → List<owners>`, and owner-follow is applied **only** to structural items (`connectionpoints`, `accessorylist`, `accessoryplacement`, `construction`, `contour`); asset items (picture/macro/document/manufacturer) are leaves, preventing catalog explosion.

### 5.3. The writer (managed, no compression — Copy coder)

* EPLAN EDZ files are 7z; `SharpCompress` reads 7z but **cannot write it**; a from-scratch LZMA encoder is unsafe and unnecessary.
* The writer is a small managed component (ported from the author's Tool-Oumijia `SevenZipCopyWriter`): one folder, one packed stream, the `Copy` coder, UTF-16LE **forward-slash** entry names (byte-compatible with official files), StartHeader CRC recomputed. Round-trip through 7-Zip 23.01 `t/l/x`: OK; output re-reads perfectly; `edzsplit verify` confirms.
* `manifest.xml` is written **last** (same order as an official export).
* **Trade-off:** no compression → larger outputs (4 540 outputs = 757 MB vs 265 MB compressed source). Acceptable for v0.1; the planned v0.2 path is an LZMA backend (spawned `7z a` with forced `/` names + header CRC patch, or a native LZMA SDK binding).

### 5.4. Controlled repair

If an item reference does not resolve directly, the tool tries a sanitize pass `non-ASCII → _`; the rewrite is applied **only** when the sanitized name exists, and every rewrite is logged (`locator.repaired` / `payload.rewritten`, the latter only in `--fix-refs auto`). The tool never invents filenames.

### 5.5. Resume and quarantine

`.edzsplit-state.jsonl` (append + `Flush(true)`) records `{partnr,status,file,sha256}`; `--resume` skips `done` entries. A failed part is reported and skipped; exit code 3 (batch continues) unless `--fail-fast` is given. Safe for multi-thousand-part runs.

## 6. EDZ writer and verification

Three layers of verification independent of the EPLAN API:

1. **Structural verify** (`edzsplit verify`): `7z t`; manifest parse; every item reference resolves (direct/repaired); path-traversal guard; version check.
2. **Container round-trip**: a synthetic EDZ produced by our own writer is read back by `7z t/l/x` (`edzsplit selftest`); official `parts.edz` → split → per-member SHA256 comparison: **60/60 byte-identical**.
3. **Import oracle** (`tools/EplanEdzValidator`): opens/creates a **scratch** parts database via `MDPartsManagement`, imports via `IXPartsImportExportEdz` with the `Standardvorgabe EPLAN` scheme, asserts `ExistsPart` and the DB delta. **Could not run on this machine** (§10).

## 7. Test results

| ID | Test | Result |
|---|---|---|
| T0-lite | `parts.edz` → 15 parts | 15/15 EDZ verify OK; 60/60 non-manifest member bytes identical to source; construction correctly included in `FES.195716.edz` |
| T1 | `smoke-232715.edz`, two Siemens parts (+1 Rittal) | 3/3 verify OK (9, 9, 6 items) |
| T2 | EKF `--only EKF.30102Р,EKF.ILR-10DR-230A,EKF.10201` | 3/3 verify OK (Cyrillic key) |
| T3 | `parts.edz` construction closure | construction landed in the right output |
| T4 | Theben Case A | 86/86 extracted, all verify OK |
| T6 | EKF full (4 540 parts) | 4 540 written, 0 failed, 757 MB, 4 m 06 s; 76/76 spot checks OK; resume: 4 540 skipped |
| T7 | Negative tests | broken/invalid archives error out loudly, exit code 4 |
| T0 | Re-emit identity & **EPLAN import** | **NOT DONE (blocked — §10)** |

Replay commands live in `docs/EVIDENCE.md`.

## 8. The seven research questions

1. **Is there a GitHub project with a complete standalone EDZ writer?** — None found. Public projects (EplanEdzManager etc.) read EDZ and call EPLAN to export; QElectroTech parses selected EDZ structures. This niche is exactly why edzsplit exists.
2. **Can the EDZ format be fully reconstructed from official exports?** — Yes, sufficiently to **read, clone and write**: the 7z container, the manifest schema (`version="2.0"`, packages/items, the name↔attribute mapping), entry ordering, `/` entry names, manifest-last, MD variables. Byte-identical round-trip is not possible (nor needed) — the success measure is *importability*, which this machine could not test (§10).
3. **How is the part → resource dependency graph built?** — §5.2/§5.3: part-XML attributes + reverse accessory-list edges + fixpoint BFS + closure modes + leaf-versus-structural owner tagging.
4. **Python, C# or C++ on Linux?** — **C# on .NET 8**: Linux-native, no EPLAN runtime, no third-party packages; managed writer; `7z` CLI for test/extract. Python is entirely viable for the reader side; C++ only needed later for native LZMA.
5. **Two-device PoC with verified import?** — The PoC (2, and also 27, parts) split **ran** and outputs verify structurally; the **import into EPLAN has not been executed** because the parts database on this machine is sealed / no scratch database can be created. This is the single gap versus the brief's §7.
6. **Which functions truly require the EPLAN API?** — Exactly three: (a) the import oracle (proves importability), (b) authoritative database export (golden master for comparisons), (c) repair and re-validate packages that EPLAN rejects. Split/inspect/verify need nothing.
7. **Best fallback if Linux-only is not feasible?** — **"Linux pipeline + Windows oracle"**: CI runs `edzsplit` + structural verify anywhere; a Windows (possibly offline) EPLAN runner executes the oracle (`tools/EplanEdzValidator`) plus before/after SHA256 comparisons; a release is tagged only when the oracle is green. Where EPLAN is unavailable, the golden-file fallback (member-level byte comparison, as in T0-lite) is the minimum viable evidence — documented as such, never overstated.

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| EPLAN says "OK" but silently drops resources | Assert `ExistsPart` + DB delta + field reads; mirror EPLAN's own `errors.txt` pattern |
| Manifest schema drift across EPLAN versions | Clone original attributes verbatim; never invent a schema |
| Mojibake / Cyrillic filenames | UTF-8 listing; sanitize fallback; per-repair logging; never guess |
| Writer divergence (solid/coder) | Copy coder, non-solid, `7z t` round-trip, hexdump comparison against official files |
| Multi-GB + interruption | Disk cache + streaming + SHA256 state + resume |
| One bad part killing a batch | Quarantine + exit 3 + optional `--fail-fast` |
| `full` closure cascades a catalog | `--max-closure-ratio`, `--dry-run`, `--only`, `related` |
| Touching the production parts DB | Validator hard-guards the `ESS_part001` token and confines the oracle to `%TEMP%\edzsplit-oracle` |

## 10. Limitations and open items

### 10.1. The EPLAN environment on this machine

* The host install (`C:\Program Files\EPLAN\Platform\2022.0.3`) is missing `Cfg\install.xml` → `EPLAN.exe` reports *"Necessary XML-file … doesn't exist!"* and exits. The running instance (SessionId 2) works, but during the session no parts-database-creation UI could be discovered (owner-drawn ribbon; no Database button; File menu / Parts management reveal no DB maintenance; the user does not know the creation path either; DLLs and settings are ACL-denied).
* Parts database: `ESS_part001.adb` = **1 byte, ACL deny read**; `ESS_part001.alk` = a 42-byte UTF‑16 redirect to `ESS_part001.adb`. No valid sample or template exists to copy; no legitimate scratch database could be produced.
* `MDPartsManagement.CreateDatabase` outside EPLAN: returns silently, leaves a 0-byte file, `OpenDatabase` throws an empty `ApplicationException` — headless creation is impossible.
* Consequence: **the import oracle could not be executed on this machine in this session.**

### 10.2. What is needed to close the criterion

1. A machine/VM with a fully working EPLAN Electric P8 2022.0.3; create a **scratch** database under `%TEMP%` (UI: Master data → Parts → Management → new database; or admin repairs `Platform\2022.0.3\Cfg\install.xml` on a test copy).
2. Run the oracle (`tools/EplanEdzValidator`): `create` → `import` the produced EDZ → assert `parts-before/after`, `ExistsPart`, macro/picture/document fields.
3. If import fails, fall back to the golden-file evidence path and state it plainly in the report.
4. Add an xunit suite + CI matrix (ubuntu-24.04; optional self-hosted Windows EPLAN runner).

### 10.3. Other technical limitations

* No compression (Copy coder) — larger outputs; no LZMA yet.
* `related` vs `full` needs benchmarking on catalogs with dense accessory graphs.
* `--jobs` (parallel) not implemented yet (sequential is safe by design).
* Verified only against EPLAN 2022 semantics; other EPLAN versions untested.
* Not executed on real Linux in this session (cross-compiled only; no Docker/WSL here).
* ZIP container is read but the writer emits 7z only.

## 11. Roadmap

* v0.2: LZMA writer (spawned `7z a` with forced `/` names + header CRC patch, or native LZMA SDK); parallel `--jobs`; xunit CI; Linux UAT.
* v0.3: golden-file harness (`edzsplit verify --compat <official.edz>`); self-hosted EPLAN oracle CI.
* v1.0: import UAT green on EPLAN 2022 (and 2026 if available) — closing the brief's criterion.

## 12. Appendix: replay commands

See `docs/EVIDENCE.md` — every command and output used in this report (internal paths scrubbed).

## 13. References

* EPLAN Electric P8 2022.0.3 (local install) — format source; `parts.edz` (official EDZ export from the user's machine); `rt-*.edz` (official exports produced by the Tool Oumijia add-in).
* 7-Zip 23.01 — `7z l/t/x`, `-slt`, `-sccUTF-8`.
* Code consulted during survey: Tool-Oumijia-EPLAN (`EdzArchive.cs`, `EdzPackageReader.cs`, `SevenZipCopyWriter.cs`), EplanEdzManager (EDZ reader + official EPLAN export path).

<sub>Produced from the evidence in `docs/EVIDENCE.md`. Absolute paths in examples were replaced with `<workspace>`.</sub>
