# Release procedure

Read on demand from `CLAUDE.md`: before every push of a build, after every merge, and before
touching `README.md`, the site (`docs/`), the installers (`tools/mac`, `tools/windows`,
`tools/linux`) or a workflow. Every rule here is the owner's decision; changing one is the owner's
call, never a side effect.

## Branches (the owner's model, 2026-10-12)

```
main   ←── only the owner merges, and only from dev. The site and the installers follow main;
  │         a GitHub release the owner publishes builds every platform from it.
dev    ←── integration. Every finished piece lands here, by a pull request the owner merges.
  │         The owner tests dev builds (the command below).
  ├── feature/roon-now-playing     one concern per branch, cut from dev
  ├── quality/gates-1
  ├── fix/<bug>
  └── …
```

- **Every piece of work is its own branch, cut from the current `dev`**, named `<area>/<what>`
  (`feature/…`, `fix/…`, `quality/…`). One concern per branch. Never commit to `dev` or `main`
  directly.
- **A build branch carries its own version bump and CHANGELOG entry.** Versions keep counting up on
  `dev`; the version on `main` when the owner publishes a release is the release.
- Push the branch. CI (`checks.yml`) runs every gate and the whole suite on macOS, Windows and Linux.
  Once that run is green on the branch's latest commit, give the owner the test command for the
  branch (below).
- **Then open a pull request from the branch into `dev`** — never into `main` — following the
  template: what it changes, what was broken on purpose to prove the tests, and what is not covered.
  The owner reviews and merges it. **Claude never merges a pull request**, into `dev` or anywhere
  else.
- **A merged branch is finished.** It is never reused; follow-up work is a new branch from the new
  `dev`.
- Before starting a branch, and before every follow-up push to one, check whether its PR has already
  been merged (`git ls-remote`, the PR's state). Pushing to a merged branch loses the work.
- If `dev` has moved on while a branch was open, merge `dev` into the branch (never rebase or
  force-push a pushed branch) and run `tools/check.sh` again.
- **`dev` → `main` is the owner's alone.** Claude never opens that pull request unless asked.
- Recommended to the owner (set in GitHub, not by Claude): protect `main` and `dev` — pull requests
  only, and the `Checks` jobs required to pass: `Gates (file length, analyzers)`,
  `test (ubuntu-latest, linux-x64)`, `test (macos-latest, osx-arm64)`, `test (windows-latest, win-x64)`.

## Every build — required steps (in order)

**Docs-only exception:** a change that touches only `docs/` (the site), `dev/` or `*.md` is not a
build: no version bump, no CHANGELOG entry, no test command. `tools/check.sh gates` still runs, and
the change still gets a normal review.

1. Make the code change, with its tests.
2. Bump `<Version>` in `src/MandarinDacBridge/MandarinDacBridge.csproj`.
3. Add a `CHANGELOG.md` entry (format below).
4. `tools/check.sh` — the gates and the whole suite (`dev/quality.md`).
5. Commit in one commit: code + version + CHANGELOG.
6. Push the branch (cut from `dev`).
7. Wait for that commit's `Checks` run to finish green on all three systems — check it through the
   GitHub API (workflow runs for the branch, matching the pushed SHA). A failed or cancelled run
   means there is nothing to test yet: fix it; don't hand out the command.
8. Give the owner the test command for the branch (below).
9. Open the pull request into `dev`. The owner merges it.

## Testing a branch or `dev` (the command to give)

In Docker, built straight from GitHub (the settings volume is kept; replace `dev` with the branch
name to test a branch before its merge):

```bash
docker build -t mandarin-dac-bridge https://github.com/meltface-80/Mandarin-DAC-Bridge.git#dev
docker stop mandarin-dac-bridge 2>/dev/null; docker rm mandarin-dac-bridge 2>/dev/null
docker run -d --name mandarin-dac-bridge --network host --restart unless-stopped \
  --device /dev/snd --privileged -v /dev/snd:/dev/snd --cap-add SYS_NICE \
  -e TZ=Europe/London -v dac-bridge-data:/data mandarin-dac-bridge
```

Add the `-e` settings the owner uses (for example `-e SOLOIST_VERBOSE=1`). On a Mac or Windows, a
branch is tested from a clone: `git checkout <branch>`, then
`dotnet run --project src/MandarinDacBridge -c Release`.

## Releases — the owner's

- **Never create a tag or a GitHub release, and never change Latest/pre-release.** The owner
  publishes a release from `main` (GitHub → Releases → Draft a new release → tag `vX.Y.Z`, the
  version in the `.csproj`); `release.yml` then runs every check and builds every platform. A red
  check means no build.
- After the owner publishes, confirm the release's assets appeared (six builds: macOS arm64/x64,
  Linux x64/arm64, Windows x64/arm64). A workflow failing quietly is how a release ends up empty.
- Add the version's row to `dev/history.md`.

## README and the site

- The README's install commands and the site (`docs/`) describe `main`. A user-visible change
  updates them in its own branch, so they arrive with the code when the owner merges into `main`.
- The site's install-command builder writes `#main`; it stays that way.

## CHANGELOG.md format

```
## [X.Y.Z] — YYYY-MM-DD

### Added / Fixed / Changed

- **What changed, in a sentence the owner would use.** Why, and for a fix, the class of error and
  the gate added so it can't come back.
```

## Workflows

- **Never write a literal `${{ … }}` expression token in a workflow file**, not even in a shell
  comment: Actions evaluates them anywhere in the file, and an invalid one fails the run at startup
  with no jobs.
- A workflow that installs or builds runs the checks first (`uses: ./.github/workflows/checks.yml`).
