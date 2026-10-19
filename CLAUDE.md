# Mandarin DAC Bridge — repository rules

This file is the repository's rule sheet. Procedure lives in the documents it names, read on
demand; nothing here restates them. A rule here overrides any default behaviour unless the owner
says otherwise in that session.

| Read | When |
|---|---|
| `dev/quality.md` | Before adding a file or class, touching `tools/` or a workflow's gates, or anything that would move a baseline |
| `dev/release.md` | Before every push of a build, after every merge, before touching `README.md`, `docs/` (the site), the installers or a workflow |
| `dev/lessons.md` | Before diagnosing an "it broke in version N" report, touching audio timing, process supervision, the Mac driver or CI on Windows/macOS, and before a non-trivial review |
| `dev/design/*.md` | Before working on the feature a design covers |
| `dev/history.md` | When a past version is referenced or a regression is being dated |
| `tests/README.md` | Before writing or moving tests |

## Spec authority

A feature with a design in `dev/design/` is specified there. Code matches it, or the design changes
first: a departure lands as an edit to the design in the same commit as the code, named in the
commit message, never as code that quietly disagrees. An open question the design does not settle
goes to the owner before the code that depends on it is written; it is not resolved by picking.

## Gates

`tools/check.sh` is the bar and is green before every push: file length, the gates' own tests, the
build with the analyzers (every warning an error), and the whole test suite. The pre-commit hook
runs the fast half (`tools/check.sh gates`); CI (`checks.yml`) runs all of it on macOS, Windows and
Linux, and blocks the test image and the release. `--no-verify` and workflow edits that skip a gate stay off the table.

Baselines only shrink. Never raise a length entry, add a `#pragma warning disable`, a project-wide
suppression or a lowered rule severity to make a change pass. A `[SuppressMessage]` goes only where
the code stands, with a true `Justification`; if one seems necessary, ask first.

## Code

- New logic goes in a new file for its area (`src/MandarinDacBridge/<Area>/`), under 500 lines, with
  its tests in `tests/MandarinDacBridge.Tests/<Area>Tests.cs`. Never appended to `Manager.cs`,
  `EndToEndTests.cs` or `UnitTests.cs`.
- A session that touches a baselined file moves one piece out of it (order in `dev/quality.md`) and
  ratchets the baseline in the same commit.
- No incomplete implementations: no `// ...`, no `// rest stays the same`.
- A `catch` that stays silent carries a comment saying truthfully why silence is safe.
- A rename or a move to settings is finished in one commit: zero stale references.
- **Bit-perfect.** The samples reach the DAC unchanged: no software volume, no dither, no resampling
  unless the DAC can't take the track's rate (then SoX, to the nearest rate it takes in the same
  family, or its highest rate when it takes none of that family). Volume is the DAC's own, or none.
- **Exclusive, one app at a time.** The bridge holds each DAC and lets go only when the owner says
  (Share when idle) or when Caldera or Roon Bridge is on. Every way in goes through the arbiter.
- **Volume safety.** The bridge never raises a DAC's volume by itself: no level is applied at start
  or on connect. A check against a real DAC only ever lowers it.
- **Secrets stay put.** The Soloist API key, the Plex token and Roon's token live in owner-only
  files. They never go to the page, the logs or git.
- **macOS, Windows and Linux.** A feature works on all three, or says plainly where it doesn't.
- Identities never change: how DAC ids and UPnP UDNs are derived (`Devices.IdOf`,
  `Description.UdnFor`; every DAC would be renamed and lose its settings), the Roon `extension_id`,
  the `settings.json` field names, the program, container and volume names
  (`mandarin-dac-bridge`, `dac-bridge-data`) and port 55500.

## Tests

A bug fix starts with a failing test that reproduces it. A test that matters is broken on purpose
once, and must fail, before it is trusted. No test may need a real DAC, Roon Core, Spotify, Plex,
Qobuz or other network service; the stand-ins are not evidence, and what only real hardware shows
(a DAC's volume steps, Core Audio and WASAPI on real devices, a real Roon Core) is not testable
here: each change says what it was and wasn't tried against.

## Bugs

Root cause, not symptom; it must explain every reported failure. A hypothesis that survives none of
the checks in `dev/lessons.md` is not a root cause, and shipping a fix for it attaches a false
explanation to a real change. The CHANGELOG names the class of error, and the gate that would have
caught it is added.

## Owner's decisions — never changed unasked

- Never merge or close a pull request, open one into `main`, create a tag or a GitHub release, or
  change Latest/pre-release. The owner merges and publishes.
- **When in doubt, ask the owner** — and explain plainly what is being asked and why.
- Every connection besides UPnP is off by default.
- Third-party programs (Spotify Soloist, Roon Bridge, Caldera Headless, QobuzProxy) are downloaded
  from their publishers when the owner asks, never committed or put in the image. ffmpeg is the
  exception (2026-10-19, `dev/design/install.md`): a small LGPL build with SoX, made from ffmpeg's
  and libsoxr's pinned source by `tools/ffmpeg/build.sh`, ships in the release archives; never
  committed, never in the image.
- The site (`docs/`) and the README's install instructions describe `main`; they move with the
  owner's merge (`dev/release.md`).

## Builds and commits

Branches (`dev/release.md`): every piece of work is its own branch cut from `dev`, one concern
each. When its CI is green it goes to `dev` by a pull request **the owner merges**. Only the owner
merges `dev` into `main`. Claude never merges a pull request, never commits to `dev` or `main`,
never pushes to a branch whose PR is merged, and never rewrites pushed history.

A build bumps `<Version>` in `src/MandarinDacBridge/MandarinDacBridge.csproj` and adds a CHANGELOG
entry, in one commit with the code. A change touching only `docs/`, `dev/` or `*.md` is not a build:
no bump. Nor is one touching only the tests and the gates (`tests/`, `tools/check.sh`,
`tools/quality/`, `.github/workflows/checks.yml`): nothing that ships changes. After a push, the docker command for the branch's test image is given only once that
commit's Test image run (`test-image.yml`, which runs the checks first) is green.

## Markdown

`dev/` and `README.md` are full English. Decisions go in the commit message, and in a design or
`dev/*.md` when they change a rule. `CHANGELOG.md` and `dev/history.md` are where a version's story
is told.

## Delegation

Work through four roles for every non-trivial change: **architect** (map side effects on every
platform and every way in), **reviewer** (edge cases, empty states, errors), **developer** (complete
code), **QA** (`tools/check.sh`, reported as `[PASS]` / `[FAIL] reason`). Changes touching the audio
path (sinks, renderer, DSD), the arbiter, process supervision (Soloist, Caldera, Roon Bridge,
QobuzProxy), secrets, volume or the Mac driver get the multi-agent review in `dev/lessons.md`. Rote,
may be delegated: porting a named test case, moving a class into its own file with behaviour
unchanged. Judgment, never delegated: anything in `dev/design/`, gate semantics, the audio path,
and anything that writes to a DAC's volume or a user's settings.
