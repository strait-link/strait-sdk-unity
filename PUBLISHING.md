# Publishing (Unity Package Manager + OpenUPM)

The Unity package lives in `src/` (`src/package.json`). There is no upload step:

- **Git URL** — users add `https://github.com/<owner>/<repo>.git?path=src#vX.Y.Z` in
  Package Manager. Works as soon as the repo is public and the tag exists.
- **OpenUPM** (searchable registry, `openupm add <name>`) — after a one-time
  submission, OpenUPM's bot builds every new semver tag by itself.

The package name (`com.<slug>.sdk`), display name, URLs and copyright holder come from
`brand.json`; `scripts/brand.mjs` writes them into `src/package.json`, the README
install block, `LICENSE` and `src/LICENSE.md`.

## One-time owner setup

1. **Pick the brand.** From the workspace root (the folder holding every SDK repo): `shared-spec/scripts/rename-brand.sh … --final --apply`.
   Rename/move the GitHub repo first if it will change.
2. **Make the GitHub repo public.**
3. **OpenUPM:** https://openupm.com/packages/add/ → paste the repo URL → it finds
   `src/package.json` → submit (opens a PR on the openupm repo; no account
   needed beyond GitHub). Once merged, tags are built automatically.

## Every release

1. Bump `version` in `src/package.json` **and** `<Version>` in
   `src/Strait.Signature.csproj`, add a `## X.Y.Z` entry to CHANGELOG.md, run
   `node scripts/brand.mjs --write` (updates the README's `#vX.Y.Z`) and
   `node scripts/unity-meta.mjs --write` (any new file in `src/` needs a `.meta`, or
   Unity silently ignores it). Commit.
2. `dotnet test test/Strait.Signature.Tests.csproj`.
3. `git tag vX.Y.Z && git push origin main vX.Y.Z`.
4. *Actions → Release* checks the versions agree, runs the tests, the `.meta` check,
   and refuses a placeholder brand (a public tag can't be taken back).

Not yet verified inside the Unity editor (no Unity licence on the build machine):
before the first public tag, add the git URL to an empty Unity 2021.3 LTS project,
import the Quick start sample and check the console is clean.
