# Building on Ubuntu Server 24.04

`edzsplit` is plain .NET 8. It needs the .NET 8 runtime (or SDK to build) and `7z` (p7zip-full). Nothing else — no EPLAN, no Windows, no fonts, no registry.

## Prerequisites

```bash
sudo apt update
sudo apt install -y dotnet-sdk-8.0 p7zip-full
```

## Build from source

```bash
git clone <repo-url> edzsplit && cd edzsplit
dotnet build src/edzsplit/edzsplit.csproj -c Release
dotnet run --project src/edzsplit -- selftest   # writer → 7z read-back → split → verify
```

## Publish a self-contained binary (no runtime needed on the target box)

```bash
dotnet publish src/edzsplit/edzsplit.csproj -c Release -r linux-x64 --self-contained -o ./publish
sudo install -m 755 ./publish/edzsplit /usr/local/bin/edzsplit
edzsplit selftest
```

## Docker

```bash
docker build -t edzsplit .
docker run --rm -v "$PWD/data:/data" edzsplit inspect /data/EKF.edz
docker run --rm -v "$PWD/data:/data" edzsplit split  /data/EKF.edz --out /data/out --log /data/ekf.jsonl
docker run --rm -v "$PWD/data:/data" edzsplit verify /data/out/EKF.ILR-10DR-230A.edz
```

## Notes on running against multi-GB inputs

* The pipeline extracts the source once into `<out>/.work-<runid>/cache` and deletes that cache afterwards (kept with `--keep-cache` for debugging). Make sure the output filesystem has room for roughly one uncompressed copy of the archive.
* Memory usage is O(1) in archive size (streaming copy with 128 KB buffers); the benchmark in `docs/EVIDENCE.md` used a 265 MB archive comfortably on a normal desktop.
* `--resume` makes long runs interruption-safe: `.edzsplit-state.jsonl` in `--out` records progress; re-run the same command with `--resume` to continue.

## cross-compilation smoke check

The repository was authored on Windows and cross-published for `linux-x64`; the Linux code path uses only `System.IO`, `System.Xml`, `System.Text`, and the external `7z` binary, so runtime behavior should match Windows. If you run it on a real Ubuntu host, please file issues with your `p7zip` version (`7z`) and .NET 8 version (`dotnet --info`).
