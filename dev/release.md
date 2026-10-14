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
  │         Pushes to dev move the `:dev` image, which the owner's test box pulls.
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
- Push the branch. CI (`test-image.yml`) runs every gate and the whole suite on macOS, Windows and
  Linux (`checks.yml`), then builds `ghcr.io/meltface-80/mandarin-dac-bridge:<version>-test`. Once
  that run is green on the branch's latest commit, give the owner the docker command for it
  (below).
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
build: no version bump, no CHANGELOG entry, no test command, and no test image (`test-image.yml`
ignores a push that touches only `docs/**` and `*.md`). `tools/check.sh gates` still runs, and the
pull request's `Checks` run still has to pass. The change still gets a normal review.

**Tests-and-gates exception** (the owner's decision, 2026-10-13): a change that touches only the
tests and the gates (`tests/`, `tools/check.sh`, `tools/quality/`, `.github/workflows/checks.yml`)
is not a build either: no version bump and no CHANGELOG entry, since nothing that ships changes. Its
commit message says what it proves. The whole of `tools/check.sh` is green before the push, as for
any change. Anything else that ships or decides what ships (`src/`, the `Dockerfile`, the
installers, `release.yml`, `test-image.yml`) is a build.

1. Make the code change, with its tests.
2. Bump `<Version>` in `src/MandarinDacBridge/MandarinDacBridge.csproj`.
3. Add a `CHANGELOG.md` entry (format below).
4. `tools/check.sh` — the gates and the whole suite (`dev/quality.md`).
5. Commit in one commit: code + version + CHANGELOG. One exception (the owner's decision,
   2026-10-12): a pure move out of an oversized file, with behaviour unchanged and the baseline
   ratcheted, may come first as a commit of its own on the same branch, so the fix that follows is
   reviewed apart from the move. The branch's last commit carries the version and the CHANGELOG.
6. Push the branch (cut from `dev`).
7. Wait for that commit's **Test image** run (`test-image.yml`) to finish green — check it through
   the GitHub API (workflow runs for the branch, matching the pushed SHA). Its `checks` jobs (every
   gate, and the suite on all three systems) run first, and the image is built only if they pass.
   A failed or cancelled run means there is nothing to test yet: fix it; don't hand out the
   command.
8. Give the owner the docker command for `:<version>-test` (below).
9. Open the pull request into `dev`. The owner merges it.

## Images

The test builds are published to **`ghcr.io/meltface-80/mandarin-dac-bridge`**, for `linux/amd64`
and `linux/arm64`, by `test-image.yml`:

| When | Tags |
|------|------|
| Every push to a branch other than `main` (not docs-only), after `checks.yml` passes | `<version>-test` (pinned to the `.csproj`'s version: use this one for a branch), `test` (the last test build, any branch), and `dev` when the branch is `dev` (what the owner's test box pulls) |

- Each processor's image is built on a runner of that processor (`ubuntu-24.04-arm` for arm64),
  never under emulation: the .NET SDK and its native compiler are not supported under QEMU.
- The image keeps the container name, the data volume (`dac-bridge-data`, mounted at `/data`) and
  the port of the locally built one, so switching between them keeps every setting.
- The image is named in the workflow, not taken from the repository's name: a renamed repository
  must not send builds to a new name that no command pulls.
- No `latest` image, and no release image: users on `main` build the image themselves (README).
  Publishing release images would be the owner's decision.
- If a pull answers `denied`, the package is private: GitHub → the profile's Packages →
  `mandarin-dac-bridge` → Package settings → visibility, before anything else.

## Testing a branch or `dev` (the command to give)

Once the branch's Test image run is green, give this with the version filled in (the `.csproj`'s
`<Version>` on that commit). It pulls FIRST, and everything after the pull sits inside `&& { … }`,
so a failed pull (an image not built yet) leaves the running bridge untouched. Lines pasted one
after another do not do that: a shell runs each whatever the last one did. The settings volume is
kept.

```bash
docker pull ghcr.io/meltface-80/mandarin-dac-bridge:NEW-test && {
  docker stop mandarin-dac-bridge
  docker rm mandarin-dac-bridge
  docker run -d --name mandarin-dac-bridge --network host --restart unless-stopped \
    --device-cgroup-rule 'c 116:* rmw' -v /dev/snd:/dev/snd --cap-add SYS_NICE \
    -e TZ=Europe/London -v dac-bridge-data:/data \
    ghcr.io/meltface-80/mandarin-dac-bridge:NEW-test
}
```

**Use the volume the box already has.** A wrong volume name doesn't fail: the bridge starts with
empty settings. `docker run` (the README's command) made `dac-bridge-data`; Compose puts its
project name in front (`mandarin-dac-bridge_dac-bridge-data` for a clone in `Mandarin-DAC-Bridge`).
`docker volume ls` shows which one holds the settings.

After the owner merges into `dev`, the same with `:dev` in both places follows `dev`. Add the `-e`
settings the owner uses (for example `-e SOLOIST_VERBOSE=1`). On a Mac or Windows, a branch is
tested from a clone: `git checkout <branch>`, then
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
- A value from the event or the matrix reaches a command through `env:`, never written into the
  command itself: a tag or a title is text anyone who can create one chooses, and in a command it
  runs. `WorkflowTests` checks every workflow's `run:` lines.
- Permissions are the least each job needs: `contents: read` for the workflow, and write only on the
  job that writes (the release job attaches the builds).
- A release builds the `.csproj`'s version (`tools/release/version.sh`); a tag that names another is
  refused, and a build missing stops the release before anything is attached.
