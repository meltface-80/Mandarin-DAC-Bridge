# Code quality plan

Mandarin DAC Bridge is written mostly by an AI coding assistant. That works while a project is
small, and stops working when the code grows faster than its structure: files get too big for
anyone to reason about, tests stop proving anything, and each fix starts causing new bugs. This
plan is how we stop that. It follows Rouen's `dev/quality.md`, hqpweb's `docs/quality-plan.md` and
ohshitgorillas' file-length ratchet, adapted to a C# (.NET 10, Native AOT) program.

**The idea:** rules in a prompt get skipped or misread; tools don't. Every rule a machine can check
lives in a gate that runs on every commit and blocks a release when it fails. `CLAUDE.md` keeps
only what no tool can check.

## Principles

1. **Gates run automatically.** Locally (pre-commit, about ten seconds) and in CI (`checks.yml`),
   where a red gate stops the test image and the release. A rule that lives only in a document is ignored in a long
   session.
2. **Today's debt is recorded, not fixed all at once.** A gate starts from a baseline of what
   already fails. New code must pass; the baseline may only shrink.
3. **Gates earn their place.** Each one names the bug class it catches. One that catches nothing
   over a few releases is reconsidered.
4. **Logic lives in small classes with tests**, not in `Manager.cs` or the page.
5. **Tests prove behaviour.** For any important test, break the code on purpose and check the test
   fails. A test that cannot fail is worse than none: it reads as protection.
6. **Small steps.** One concern per change. A refactor keeps behaviour identical and proves it.
7. **Don't freeze the product.** Gates constrain how the code is written, not what it does.

## The gates

| Gate | Command | Catches | Starts as | Tightens to |
|------|---------|---------|-----------|-------------|
| **Tests that block** | `dotnet test` (in `tools/check.sh`) | Regressions | The whole suite on macOS, Windows and Linux in CI; the release needs it green | — |
| **Analyzers** | the build (`Directory.Build.props`, `.editorconfig`) | Number parsing that depends on the machine's culture (a "," decimal broke it once), locale-dependent string comparison, return codes dropped without saying so, unused code, .NET's recommended set | Every warning an error, nothing grandfathered: today's findings were fixed | More rules as bugs show the need |
| **File length** | `python3 tools/quality/length.py` | Files too big to reason about | No file over 500 lines; the 7 already over are listed in `tools/quality/length-baseline.json` and may only shrink | Baseline empty; limit 400 |
| **Edit-time length** | `.claude/settings.json` → `tools/quality/edit_hook.py` | The same, at the first edit instead of at commit | A note past 450 lines; an edit that would cross 500 (or a baselined file's length) is refused; a shrinking edit never is | — |
| **The gates' own tests** | `python3 -m unittest discover -s tools/quality` | A gate that can't fail | Each verdict pinned, including that baselines only tighten | — |
| **Page scripts** (next) | — | Undeclared names, use before declaration, silent `catch`, wrong property or argument in the page's JavaScript | The scripts moved out of the HTML, ESLint and `// @ts-check` with today's findings recorded | Nothing recorded |

`tools/check.sh` runs everything; `tools/check.sh gates` runs the fast half (length, the gates' own
tests, the build with the analyzers), which is what the pre-commit hook runs. Once per clone:
`git config core.hooksPath .githooks` (the Claude Code session hook does it for you).

### Where the tools live

The analyzers ship with the .NET SDK: nothing to install. The length gate and its hooks are Python
3 with no packages. `tools/` and `.githooks/` are never part of the program or the Docker image
(the image builds only `src/`).

### Moving a baseline

- A baselined file shrank: `python3 tools/quality/length.py --ratchet` records the smaller number.
  The gate fails until you do, so the allowance only ever tightens. A file that drops under the
  limit leaves the baseline for good.
- An analyzer finding is fixed, never recorded. A suppression goes only where the code stands, with
  `[SuppressMessage(..., Justification = "...")]` saying truthfully why (two exist: SHA-1 in
  `Devices.IdOf` and `Description.UdnFor` makes identifiers, not security, and changing it would
  rename every DAC).

Never raise a baseline to make a change pass. If a change genuinely needs it, that is a question
for the owner, asked before the change.

## Splitting the big files

Recorded on 2026-10-12: `tests/MandarinDacBridge.Tests/EndToEndTests.cs` (1,134 lines),
`src/MandarinDacBridge/Manager.cs` (900), `tests/MandarinDacBridge.Tests/UnitTests.cs` (644),
`src/MandarinDacBridge/Spotify/SoloistPlayer.cs` (640), `src/MandarinDacBridge/Page/index.html`
(547), `src/MandarinDacBridge/Renderer.cs` (542), `src/MandarinDacBridge/Slim/SlimPlayer.cs` (542).

One piece per session, moved into its own file with its tests, and the baseline ratcheted in the
same commit. Order — where every change lands first, then the logic most often in bugs:

1. **`EndToEndTests.cs`** → one file per area (`PlaybackTests`, `SqueezeboxTests`, `SpotifyTests`,
   `CalderaTests`, `QobuzTests`, `RoonTests`, `VolumeTests`) over a shared `BridgeFixture`. Until
   the fixture exists, an area moves out as a part of the same class (`partial`), sharing its
   helpers: `VolumeTests.cs`, `CalderaTests.cs` and `SqueezeboxTests.cs` so far.
2. **`UnitTests.cs`** → the same, by area.
3. **`Manager.cs`** → each connection's wiring (Squeezebox, Spotify, Caldera, Qobuz, Roon, the Mac)
   and the page's view. (The settings went to `Settings.cs` in 1.1.4, and finding Roon on this machine
   to `Roon/RoonRunning.cs` in 1.1.6.)
4. **`SoloistPlayer.cs`** → its sound path (private PulseAudio and the pipe), the WebSocket, the
   metadata (needed before the Spotify now-playing fix).
5. **`Page/index.html`** → its script and styles into their own files (with the page-scripts gate).
6. **`Renderer.cs`**. (`SlimPlayer.cs` is under the limit since 1.1.2: server discovery and the
   strm parser moved to `Slim/SlimDiscovery.cs` and `Slim/Strm.cs`.)

Each move keeps behaviour identical, and the existing suite is the proof: it must pass before and
after with no test changed except where it lives.

## Security (after the page-scripts gate)

- **Login.** Today anyone who can reach port 55500 can change settings, switch connections on, and
  start downloads. An optional password (off by default, so existing installs keep working), with a
  session cookie, guarding every write route.
- **Verified downloads.** The bridge fetches Soloist, Roon Bridge, Caldera and QobuzProxy, and the
  installers fetch the bridge. Downloads only from their publishers' fixed HTTPS hosts; the release
  workflow publishes SHA-256 sums next to each build, and the installers refuse a file whose sum
  doesn't match; the bridge logs the SHA-256 of each program it fetches.

## Rules for writing tests

- A bug fix starts with a failing test that reproduces it.
- Assert behaviour, not implementation: what the owner, an app or the DAC sees.
- Never weaken a test to make it pass. If behaviour changed on purpose, say so in the commit.
- No tests that cannot fail. Break the code on purpose for every important one.
- No fixed sleeps where a condition can be waited for.
- The stand-ins (a pretend Roon Core, Soloist, LMS, Plex server, DAC) follow what their publishers
  document, and say so; they are not evidence of how the real thing behaves.

## How we'll know it's working

- Fixes stop causing new bugs, and each bug that gets through leads to a gate that would have
  caught it.
- The length baseline's total goes down every few versions.
- `CLAUDE.md` gets shorter as its rules move into tools.
