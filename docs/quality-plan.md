# Code quality plan

Mandarin DAC Bridge is written mostly by an AI coding assistant, for an owner who tests it on real DACs. That works while a project is small. It stops working when the code grows faster than its structure: files get too big to reason about, tests stop proving anything, and each fix starts causing new bugs. This plan is how we keep that from happening: gates a machine enforces on every commit, so the code can't quietly get worse. It's a working document; it changes as we learn, and each change is noted here with the reason.

Adapted from hqpweb's quality plan (github.com/statelycurmudgeon/hqpweb, `docs/quality-plan.md`) for a C# / .NET program.

## Principles

1. **Gates run automatically.** A rule that only lives in a document gets skipped in a long session. Checks run before each commit (`tools/hooks/pre-commit`), at edit time (`.claude/settings.json`) and in CI on every push and pull request.
2. **Gates earn their place.** Add a gate for a bug that got through, or a bug class that's clearly likely here. One that catches nothing over a few releases is reconsidered.
3. **Decision logic lives in small classes with tests.** The page renders; the bridge decides, in code that's tested on its own.
4. **Tests prove behaviour.** A test must fail when the behaviour it describes breaks. Check it: break the code on purpose and watch the test fail.
5. **Measured beats modelled.** A stand-in (a pretend Roon Core, Soloist, LMS, DAC) is a convenience; real hardware and real services are the authority. Each change says which it was checked against.
6. **Small steps.** One concern per branch. Refactors keep behaviour identical and prove it.
7. **Don't freeze the product.** Gates constrain how the code is written, not what the bridge does or how the page looks.
8. **Show the evidence.** Each pull request says what it changes, what was broken on purpose to show the tests catch it, and what isn't covered ([the template](../.github/pull_request_template.md)).

## Gates

| Gate | Starts as | Tightens to |
| --- | --- | --- |
| **Tests** (`dotnet test`) | Blocking in CI on macOS, Windows and Linux, every push and pull request | — |
| **Analyzers** (.NET, `Directory.Build.props` + `.editorconfig`) | .NET's recommended set plus rules for bug classes we've had (culture-dependent parsing, locale-dependent comparison, dropped return codes, unused code), every warning an error, nothing grandfathered | More rules as bugs show the need |
| **File length** (`tools/quality/length.py`) | Fails above 600 lines; reports above 400; an edit is refused at 600 and noted at 500. Files already over are listed and may only shrink | Fails above 400 |
| **Page scripts** (planned) | The page's JavaScript moved into files, checked with ESLint and TypeScript's checker (`// @ts-check` with JSDoc), today's findings recorded so only new ones fail | No recorded findings |
| **Function size and complexity** (planned) | Reported | Fails above 60 lines or a complexity of 15 |
| **Mutation testing**, by hand | Now and then on the core logic (arbiter, DSD, volume curve, sources, SOOD/MOO framing): break it on purpose, check a test fails. Not a gate | — |

**The numbers are starting values**, not measurements: 600 and 400 lines per file. Adjust them from experience, and write down why.

**Files over the limit when the gate came in** (2026-10-12): `tests/MandarinDacBridge.Tests/EndToEndTests.cs` (1,134), `src/MandarinDacBridge/Manager.cs` (900), `docs/index.html` (795), `tests/MandarinDacBridge.Tests/UnitTests.cs` (644), `src/MandarinDacBridge/Spotify/SoloistPlayer.cs` (640). One is split per session, before features that touch it.

## Rules for writing tests

- **A bug fix starts with a failing test** that reproduces it.
- **Assert behaviour, not implementation:** what the owner, an app or the DAC sees, not which method was called.
- **Never weaken a test to make it pass.** If the behaviour changed on purpose, say so in the commit.
- **The stand-in isn't evidence.** A pretend service follows what its publisher documents or what was measured, and says which.
- **No tests that can't fail**, and no fixed sleeps where a condition can be waited for.
- **New tests go in a file for their area**, not into the two big test files.

## Next steps

1. Split the five oversized files, one per session: `EndToEndTests.cs` into per-area test files first (it's where every feature adds tests), then `Manager.cs` (connections, the page's view, settings), `SoloistPlayer.cs`, `UnitTests.cs`, `docs/index.html`.
2. The page scripts gate (above).
3. **A login, and verified downloads.** Anyone who can reach the bridge's page can change its settings and start downloads. Add an optional page password, and verify what's downloaded: the installers check release checksums, and the bridge records the SHA-256 of each program it fetches (Soloist, Roon Bridge, Caldera, QobuzProxy), from fixed HTTPS hosts only.
4. Function size and complexity, reported.

## How we'll know it's working

- Bug fixes stop causing new bugs, and each bug that gets through leads to a gate that would have caught it.
- No file grows past the limit without being split first.
- Changing what the bridge does means updating the tests of that behaviour, never the gates.
