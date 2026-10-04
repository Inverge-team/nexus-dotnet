# Releasing

Publishing is automated. **Merging `dev` into `main` publishes whatever version
`Directory.Build.props` declares.** Nothing is published from `dev`, and nothing is
published by hand.

## One-time setup

1. **Create the nuget.org API key** — nuget.org → your name → **API Keys** →
   **Create**:
   - Scope: **Push → "Push new packages and package versions"**. The narrower
     "Push only new package versions" cannot create a package that does not exist
     yet, so the first release would be rejected.
   - Packages: use the **Glob Pattern** box with `Inverge.*`. The checkbox list only
     offers packages you already own, and it also means this key keeps working for
     future `Inverge.*` packages.
2. **Add it to the repo** — Settings → Secrets and variables → Actions → New
   repository secret, named exactly **`NUGET_API_KEY`**. The workflow fails on the
   first step with a clear message if it is missing, rather than dying on a 401
   halfway through.
3. **Create `main`** from `dev` (see the note at the bottom).

Optional but worth it: once the first version is live, request the **`Inverge.` ID
prefix reservation** (Manage Packages → ID Prefix Reservation). It stops anyone else
taking `Inverge.Something` and gives your packages the verified-owner mark.

## Cutting a release

1. On `dev`, set the version in `Directory.Build.props`:
   - A release candidate: `<VersionPrefix>1.0.0</VersionPrefix>` +
     `<VersionSuffix>rc.2</VersionSuffix>`.
   - A final release: the same `VersionPrefix` with **`VersionSuffix` removed or
     emptied**. That is the whole promotion from rc to final.
2. Add the version's section to `CHANGELOG.md`.
3. Merge `dev` → `main`.

`.github/workflows/release.yml` then builds, runs the full test matrix on .NET 8, 9
and 10, packs, and publishes — `Inverge.Nexus` first on its own, because the other
three depend on it and a dependent published before its dependency is indexed is
briefly uninstallable. It waits for indexing, pushes the other three plus the symbol
packages, then tags `v<version>` and opens a GitHub Release with the packages
attached.

Tests run **before** the push. A red build publishes nothing.

## Re-running is safe

A version on nuget.org can never be replaced or re-uploaded — only unlisted. The
workflow is built around that:

- Before pushing anything it asks nuget.org whether the version already exists. If it
  does, the publish steps are skipped and the run still succeeds, so an unrelated
  commit to `main` does not fail and cannot attempt a doomed re-publish.
- Every push uses `--skip-duplicate`, so a run interrupted halfway can be re-run
  without erroring on a conflict.
- The tag and Release are only created if they do not already exist.

To release again you must **bump the version**. There is no way to overwrite one.

## Publishing by hand

You should not need to, but `workflow_dispatch` is enabled, so you can run the
Release workflow manually against `main` from the Actions tab.

## A note on `main`

The umbrella `SaaS/CLAUDE.md` carries a hard rule — *never push `main`* — because on
the **backend** repo `main` auto-deploys production. This SDK repo is different:
`main` is the release branch and pushing to it is the intended way to publish. Even
so, an agent working here will not create or push `main` for you; create it yourself
and do the merges, so a publish is always a deliberate human act:

```bash
cd SDKs/nexus-dotnet
git checkout -b main dev
git push -u origin main        # this triggers the first release
git checkout dev
```

Afterwards, releases are ordinary merges of `dev` into `main`.
