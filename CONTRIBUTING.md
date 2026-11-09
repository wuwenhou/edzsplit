# Contributing

Thanks for considering a contribution.

## Ground rules

1. **No licensed EPLAN parts databases** (`ESS_part001.*`) or proprietary manufacturer EDZ files in the repo. Real-world EDZ samples for development can be referenced by local path in docs only.
2. **Evidence, not vibes.** If a change claims the output EDZ is valid, attach the output of `edzsplit verify` and, when possible, a log excerpt. Import-level claims require the import oracle (see `docs/EVIDENCE.md`).
3. Keep `edzsplit` dependency-free (.NET 8 base class library + system `7z`). The test-only oracle in `tools/EplanEdzValidator/` may reference the installed EPLAN API.
4. Do not weaken safety guards (path-traversal check, main-database guard in the validator, quarantine semantics) without a documented reason.
5. English for code and commit messages; documentation is bilingual (VI/EN mirrors).

## Development setup

```bash
# Linux
sudo apt install -y dotnet-sdk-8.0 p7zip-full
dotnet build src/edzsplit/edzsplit.csproj
dotnet run --project src/edzsplit -- selftest

# Windows
# requires .NET 8 SDK and 7-Zip
dotnet build src/edzsplit/edzsplit.csproj
dotnet run --project src/edzsplit -- selftest
```

## Tests

* `edzsplit selftest` — end-to-end synthetic two-part round trip (writer → 7z read-back → split → verify). Must pass before every commit.
* Structural verification on real samples from the development machine is recorded in `docs/EVIDENCE.md` — extend it when adding features that change output.
* Import UAT uses `tools/EplanEdzValidator/` and a scratch database. Never against a production parts database.

## Style

* No external NuGet packages in the runtime tool.
* JSONL events to stdout; machine-readable output via `--json`.
* Exit codes: 0 ok, 2 usage, 3 partial/warnings, 4 failed — do not invent new ones without documenting them.
