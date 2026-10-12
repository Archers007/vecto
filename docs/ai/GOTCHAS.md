# Gotchas — Vecto

## Environment (WSL working on /mnt/d)
- **Always build with `dotnet.exe`** (Windows .NET SDK via WSL interop), never the Linux
  `dotnet` — the WPF project (`net8.0-windows`) won't build on the Linux SDK. The Windows
  dotnet can't parse `/mnt/d/...` arguments, so run it from inside the repo with relative
  paths (`cd` first).
- **CodeGraph does not auto-sync here** — run `codegraph sync` after edits (live
  file-watching is unreliable on `/mnt` drives).
- **Push via `cmd.exe /c "git push origin dev"`** — git credentials live on the Windows side.
- `git config core.filemode false` is set — don't "fix" phantom mode diffs.

## Licensing / dependencies
- **SixLabors.ImageSharp is pinned to exactly 4.1.3** in Vecto.Cli.csproj — no floating
  wildcard (a floating `3.1.*` is how five advisories landed with no commit). ImageSharp 4
  validates a licence at build time: a free, assembly-scoped **community key** for the
  assembly `vecto` is committed at `Vecto.Cli/sixlabors.lic` (Six Labors allows committing
  an assembly-scoped community key to a public repository). **Release builds fail without
  a valid key; Debug builds only warn.**
- The key **expires 2028-01-10** — renew in the final 90 days (from ~2027-10-12) at
  https://licensing.sixlabors.com with the same email and the exact assembly name `vecto`.
  Renaming the CLI's `AssemblyName` requires a new key. Exactly one `sixlabors.lic` may
  live in the repo ("Multiple Six Labors license files found" is a build error).
- Redistributed CLI binaries must carry ImageSharp's licence text —
  `THIRD-PARTY-NOTICES.md` at the repo root (Six Labors Split License, Apache-2.0 terms
  for open-source use). The weekly CI run (`.github/workflows/ci.yml`, NuGet audit ON)
  catches future advisories.
- Vector Magic's sample images (`C:\Program Files\Vector Magic\Samples`) are licensed
  sample content: fine for local benchmarking in `_bench/` (gitignored), **never commit them**.

## Engine
- `TreatWarningsAsErrors` is on solution-wide — new warnings break the build by design.
- SVG/CLI numeric output must go through InvariantCulture; the de-DE test will catch
  violations, but remember it for any new writer/exporter.
- Corner threshold is 68° for a geometric reason (false corners at staircase slope
  transitions measure 63.4° at k=3) — don't lower it without re-deriving the bound; see
  the comment in `Tracer.Resolve` and ALGORITHM_INDEX.md.
- `MinRegionArea` is in absolute pixels, tuned for logo-sized inputs (≤ ~2000px). Very
  large photos may need it scaled with resolution (tuning backlog).

## App
- `Vecto.exe --smoke` is a headless self-test (traces a generated image, writes
  `%TEMP%\vecto-smoke.svg`, exit 0/11) — WinExe has no console, so check the exit code.
- Crashes log to `%TEMP%\vecto-crash.log`.
