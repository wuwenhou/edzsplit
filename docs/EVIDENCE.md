# EVIDENCE — edzsplit 0.1.0-poc

All commands were run on the development host (Windows x64) unless marked otherwise. Paths are scrubbed (`<workspace>`, `<home>`). Outputs are trimmed to essential lines; `…` marks elided lines. Times are local.

## 0. Environment

```
OS               Windows 11 x64 (host)
.NET SDK         8.0.425
7-Zip            23.01 (x64), 7z does NOT require elevation
EPLAN            Electric P8 2022.0.3 (see "Import oracle" — could not create scratch DB)
Sample SHA256    EKF.edz      20830197d2bf074c229d2c38f0c1a6d80f753fa3452eeabb6e0bf1ce1c9a5f53
                 Theben.7z    215865ba0dd200e49f0d60d2d0fc13bb16f893146a0936dce0bdba0ca377a54e
                 parts.edz    d0aa9bf3ae2f5b45b1764e77044b4aeb5289abbdbd33705a990abe0ca3b44bb7
                 smoke-232715 6315f2da7d04b88aa8f4b93d84e2bc3e5923ec8aa45c17e98d5a9aac4fbe31ad
                 import-smoke 8f4653ddc1c1acc4554b340a2c6e488484df1979013d6ef831582b60f3b98257
                 rt-05        cf1a62049c782e598d63bc557bf703c74acc147faff6916675cb8ed841a2dbec
```

## 1. `edzsplit selftest` (writer → 7z read-back → split → verify)

```
selftest: synthetic EDZ written -> <tmp>\synthetic-two-parts.edz
selftest: source verify Ok=True errors=0
selftest: split planned=2 written=2 failed=0
selftest: SYN.001.edz verify Ok=True errors=0 warnings=0
selftest: SYN.002.edz verify Ok=True errors=0 warnings=0
selftest: --only SYN.001 -> written=1
SELFTEST PASS
```

## 2. Format analysis (EKF.edz)

```
$ 7z l -slt -sccUTF-8 EKF.edz
Path = items\macro\EKF\BA 47-63 1Р.ema     ← real Cyrillic Р (U+0420)

$ edzsplit inspect EKF.edz
Container : 7z  (265109028 bytes)
Entries   : 6057   (uncompressed 328807394 bytes)
Manifest  : v2.0
Packages  : 4540  (part=4540)
Items     : 17658
Refs      : direct=17658 repaired=0 missing=0 (unique missing=0)
```

> **Analysis correction of an earlier claim:** an initial comparison against the OEM-codepage listing (`7z l -ba`) produced 421 "missing/mojibake" references. With `-sccUTF-8`, the archive stores Cyrillic entry names that match the manifest exactly — 0 missing. The `non-ASCII → _` repair path remains implemented as a fallback but is unused by EKF.

## 3. `edzsplit inspect` on all samples

```
import-smoke.edz   : Packages 1 (part=1), Items 3,   Refs direct=3
smoke-232715.edz   : Packages 27 (part=27), Items 96, Refs direct=96
rt-05 (accessorylists): Packages 206 (part=172, accessorylist=23, construction=11), Items 1238, Refs direct=1238
parts.edz          : Packages 16 (part=15, construction=1), Items 76, Refs direct=76
Theben.7z          : Entries 87, Nested EDZ 86, Manifest MISSING (expected: Case A, no manifest)
```

## 4. Splits and verification

### 4.1 smoke-232715.edz — two-device PoC (+1 Rittal)

```
$ edzsplit split smoke-232715.edz --out <ws>/out/poc/smoke \
      --only SIE.3RT2015-1BB41-1AA0,SIE.3RH2911-1FA22-0MA0,3RT2015-1BB41+1FA22 --log <ws>/out/poc/smoke.jsonl
=== Split summary ===
Planned : 3   Written : 3   Resume : 0 skipped   Failed : 0

$ edzsplit verify <each>
SIE.3RT2015-1BB41-1AA0.edz  OK:True  Pkgs:1 Items:9  Missing:0
SIE.3RH2911-1FA22-0MA0.edz  OK:True  Pkgs:1 Items:9  Missing:0
3RT2015-1BB41+1FA22.edz     OK:True  Pkgs:1 Items:6  Missing:0
```

### 4.2 parts.edz — identity re-emit

```
$ edzsplit split parts.edz --out <ws>/out/poc/parts
Planned : 15   Written : 15   Failed : 0

# member-level SHA256 comparison (source vs each output, manifest.xml excluded):
# 60/60 non-manifest member files byte-identical.
$ edzsplit verify <15 outputs>   → all OK (Missing:0)
```

### 4.3 Bug found via parts.edz, and fix

```
# First build dropped the construction package from output of part FES.195716
# (part and construction share the key "FES.195716").
# Fix: type-scoped package lookup. Rebuilt:
Planned : 15   Written : 15   Failed : 0
FES.195716.edz now contains TWO packages: part FES.195716 + construction FES.195716.
15/15 outputs verify OK (one shows Pkgs:2, Items:6).
```

### 4.4 EKF.edz — selective split (Cyrillic part)

```
$ edzsplit split EKF.edz --out <ws>/out/poc/ekf --only 'EKF.30102Р,EKF.ILR-10DR-230A,EKF.10201'
Planned : 3   Written : 3   Failed : 0

$ edzsplit verify <3 outputs>
EKF.30102Р.edz     OK:True  Items:4
EKF.ILR-10DR-230A.edz OK:True Items:5
EKF.10201.edz      OK:True  Items:4
```

### 4.5 EKF.edz — FULL split (4 540 parts)

```
$ edzsplit split EKF.edz --out <ws>/out/poc/ekf-full --log <ws>/out/poc/ekf-full.jsonl
=== Split summary ===
Planned : 4540   Written : 4540   Resume : 0 skipped   Failed : 0   totalBytes=756,764,107
Wall clock: 00:04:06

# Spot verification (76/76 OK):
#   40 random files + all 36 items matching 30102 / ILR-10 / evo4000 / pvc-22
# All Missing:0.

# Resume behaviour (state file present from the run above):
$ edzsplit split EKF.edz --out <ws>/out/poc/ekf-full --resume
Planned : 4540   Written : 0   Resume : 4540 skipped   Failed : 0
```

### 4.6 Theben.7z — Case A

```
$ edzsplit case-a Theben.7z --out <ws>/out/poc/theben
Extracted 86 EDZ files to <ws>/out/poc/theben: ok=86, bad=0
```

## 5. Import oracle (`tools/EplanEdzValidator`)

**Build:** OK (net48, x64; references `C:\Program Files\EPLAN\Platform\2022.0.3\Bin\Eplan.EplApi.*.dll`).

**Why the oracle could not run (session log):**

```
$ EplanEdzValidator create --db <tmp>\edzsplit-oracle\scratch-test.adb
ERROR: System.IO.FileNotFoundException: Could not find file '<tmp>\…\scratch-test.adb'.

$ type nul > scratch-test.adb
$ EplanEdzValidator create --db scratch-test.adb
created: scratch-test.adb (0 bytes)          ← file created but remains 0 bytes

$ EplanEdzValidator open-check --db scratch-test.adb
ctor failed, falling back to OpenDatabase:
ERROR: System.ApplicationException:        ← empty message
```

Findings on the host installation (read-only inspection):

```
C:\Program Files\EPLAN\Platform\2022.0.3\Cfg\install.xml   — MISSING (only install_hotfix.xml present)
EPLAN.exe launch → dialog "Necessary XML-file '...Cfg\install.xml' doesn't exist!" → exits
C:\Users\Public\EPLAN\Data\Parts\Company name\ESS_part001.adb  — 1 byte, READ DENIED
C:\Users\Public\EPLAN\Data\Parts\Company name\ESS_part001.alk  — 42 bytes UTF-16 redirect: "ESS_part001.adb\n1.0" (read OK)
All settings backups and DLLs — READ DENIED
```

**UI search for a parts-database creation control** (desktop automation, EPLAN @Session 2): found none — ribbon is owner-drawn; no Database button in Master data/Parts group; File menu exposes no database maintenance; user confirmed being unable to find it either. The EPLAN instance opened at 11:53 vanished between interactions (host window is flaky in this session).

> **Consequence:** import-level proof for the produced EDZ files is **not available** on this machine. Structural verification is the only evidence that can be attached today.

## 6. Known-good replay checklist

```bash
edzsplit selftest                                            # writer round-trip
edzsplit inspect EKF.edz
edzsplit split EKF.edz --out out --only EKF.10201
edzsplit verify out/EKF.10201.edz
edzsplit case-a Theben.7z --out out/theben
# EPLAN oracle (only on a machine with a working EPLAN):
EplanEdzValidator create     --db <scratch-dir>\scratch.adb --allow-outside-temp
EplanEdzValidator open-check --db <scratch-dir>\scratch.adb --allow-outside-temp
EplanEdzValidator import --db <scratch-dir>\scratch.adb --edz <path-to-output.edz> \
    --expect <partnr> --allow-outside-temp
```

## 7. What this evidence does and does NOT prove

| Claim | Proved? |
|---|---|
| Output EDZ is a valid 7z | YES (7z t/l/x on our files) |
| Output EDZ manifest parses, refs resolvable, no traversal | YES (edzsplit verify) |
| Output EDZ is byte-compatible with the official container format (entry naming/ordering) | YES (synthetic round-trip + hexdump match of an official file) |
| EDZ produced by this tool differs only by design (manifest clone) from source | YES (60/60 non-manifest members byte-identical) |
| EDZ imports into EPLAN Parts Database | **NOT DONE — environment blocked (§5)** |
| Multi-part catalogs with heavy accessory-graph webs split correctly | partially (no accessory-rich sample available locally) |
