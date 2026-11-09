# edzsplit

**Split a multi-device EPLAN EDZ into one standalone EDZ per part — Linux-native, no EPLAN runtime, no Windows.**

[Tiếng Việt](README.vi.md) · [Research report (VI)](docs/REPORT.vi.md) · [Research report (EN)](docs/REPORT.en.md) · [Evidence](docs/EVIDENCE.md)

> **Status / proof honesty notice:** This tool is fully functional as a *structural* splitter and has been verified end-to-end (archive → per-part EDZ → structural re-verification) on real manufacturer data (EKF, Theben, Rittal/FESTO samples, 4540-part full split in ~4 min). **Import of an output EDZ into a live EPLAN Parts Database has NOT yet been executed on the machine it was developed on** (the local EPLAN install could not create or open a scratch parts database — see [report §10](docs/REPORT.en.md#10-limitations-and-open-items)). Claims about EPLAN import compatibility therefore remain *unproven* until you run the oracle described in [docs/EVIDENCE.md](docs/EVIDENCE.md). Read the report before adopting.

## What it does

EPLAN `.edz` files are how parts (articles) are exchanged with the EPLAN Parts Database. They come in two shapes:

* **Case A** — an archive (e.g. `Theben.7z`) that already contains many single-part `.edz` files. Handled by `edzsplit case-a`.
* **Case B** — *one* `.edz` that contains thousands of parts and resources (macros, images, documents), e.g. `EKF.edz` (265 MB, 4 540 parts, 17 658 item references). This is the hard problem `edzsplit` solves: dependency analysis plus a writer that produces one valid EDZ per part.

## Features

* Reads official EPLAN EDZ files (7z container, `manifest.xml` + `items/…`, UTF-16 entry names) — ZIP container also readable.
* Identifies all packages (`part`, `manufacturer`, `construction`, `connectionpoints`, `accessorylist` …) and item kinds (`partxml`, `macro`, `picture`, `document`, `contour` …).
* Builds a part → resource dependency graph from part-XML attributes (`P_ARTICLE_MACRO`, `P_ARTICLE_GROUPSYMBOLMACRO`, `P_ARTICLE_PICTUREFILE`, `P_ARTICLE_EXTERNAL_DOCUMENT_n`, `P_ARTICLE_REF_CONSTRUCTION_NAME`, `P_ARTICLE_REF_TERMINAL_NAME`, accessory lists/placements …).
* `--closure full` (default): part + direct resources + transitively-referenced constructions/connection points + accessory lists referencing the part (and their members). `related` and `none` available.
* **Streaming**: per-archive single-pass extraction to an on-disk cache; item metadata never fully loaded in RAM; output written entry-by-entry. Multi-GB sources are supported (see benchmark in evidence doc).
* **Resume**: `.edzsplit-state.jsonl` records every completed/failed part; `--resume` skips finished work after an interruption.
* **Repair**: non-ASCII ↔ sanitized-name resolution (e.g. Cyrillic vs `_`-substituted archive entries), applied only when the sanitized name actually exists — never by guessing. Payloads rewritten only in `--fix-refs auto` mode and every rewrite is logged.
* **Structural verification** of outputs: 7z integrity test, manifest parse, all item references resolvable, path-traversal guard.
* **Quarantine**: broken packages are reported and skipped (exit code 3); `--fail-fast` available; never aborts a 4 540-part batch because one package is broken.
* **Deterministic, dependency-free**: .NET 8, calls the system `7z` (p7zip on Linux) for archive testing/extraction; the EDZ writer itself is a managed no-compression 7z writer (entry names byte-compatible with official files).

## Install

### Ubuntu Server 24.04

```bash
sudo apt update
sudo apt install -y p7zip-full
# Option 1: run from source
sudo apt install -y dotnet-sdk-8.0
dotnet publish src/edzsplit/edzsplit.csproj -c Release -r linux-x64 --self-contained -o /usr/local/bin/edzsplit-bin
sudo ln -sf /usr/local/bin/edzsplit-bin/edzsplit /usr/local/bin/edzsplit

# Option 2: Docker
docker build -t edzsplit .
docker run --rm -v /data:/data edzsplit inspect /data/EKF.edz
```

### Windows

Needs [.NET 8 runtime](https://dotnet.microsoft.com/) and [7-Zip](https://www.7-zip.org/) (`7z.exe`, or point `EDZSPLIT_7Z` at it). Or build from source with the .NET 8 SDK.

## Usage

```bash
# Inspect (container, manifest, packages, item counts, reference repair stats)
edzsplit inspect EKF.edz
edzsplit inspect EKF.edz --json > ekf-inspect.json

# Split everything, full closure (default)
edzsplit split EKF.edz --out out/ekf --log ekf.jsonl

# Split specific parts
edzsplit split EKF.edz --out out/ekf --only EKF.ILR-10DR-230A,EKF.10201

# Resume after interruption
edzsplit split EKF.edz --out out/ekf --resume

# Dry run (plan only)
edzsplit split EKF.edz --out out/ekf --dry-run

# Case A: archive of ready-made EDZ files
edzsplit case-a Theben.7z --out out/theben

# Verify any EDZ (structural)
edzsplit verify out/ekf/EKF.ILR-10DR-230A.edz

# Smoke test (no EPLAN, no external file)
edzsplit selftest
```

Exit codes: `0` ok · `2` usage · `3` finished with warnings/quarantine · `4` failed.
Every command emits JSONL events on stdout.

## Verify before you trust

Structural verification ≠ proof of EPLAN import. Before relying on the outputs, run the import oracle against a **scratch EPLAN Parts Database**:

1. `tools/EplanEdzValidator/` builds a test-only .NET Framework console that creates/opens or imports into a **scratch** parts database via the official EPLAN API and asserts the part exists afterwards.
2. See [docs/EVIDENCE.md](docs/EVIDENCE.md#import-oracle) for steps and for what has and has not been verified so far.

**Do not point the oracle at a production parts database.**

## Repository layout

```
src/edzsplit/            The splitter (.NET 8, no third-party packages)
tools/EplanEdzValidator/ Test-only EPLAN API oracle (.NET Framework 4.8, Windows + installed EPLAN)
docs/                    Reports (VI/EN), evidence log, Linux build notes
Dockerfile               Ubuntu 24.04 image (p7zip + .NET 8 runtime)
```

## Known limitations

See [docs/REPORT.en.md §10](docs/REPORT.en.md#10-limitations-and-open-items). Highlights: output EDZ uses uncompressed (Copy) 7z blocks (~2× larger); sequential split; verified only against EPLAN 2022 file semantics; import UAT pending an EPLAN environment; Linux run not executed here (cross-compiled only).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Please do not include licensed EPLAN parts databases or manufacturer EDZ files in issues/PRs.

## License

MIT — see [LICENSE](LICENSE).
