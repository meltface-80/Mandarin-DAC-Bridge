# Mandarin DAC Bridge — repository rules

The rule sheet for anyone changing this repository, people and AI assistants alike. It holds only what no tool checks; the tools are the gates in [docs/quality-plan.md](docs/quality-plan.md), which is binding. What the bridge does and how it's built: [README.md](README.md).

## Branches: work goes to `dev`; `main` is the owner's

- Every bug fix and every feature gets its own branch from `dev`: `fix/<topic>`, `feature/<topic>`, `quality/<topic>`. One concern per branch. Branch from the latest `dev`; never stack on an unmerged branch.
- Never commit to `dev` or `main` directly.
- Push the branch. CI runs the gates and the tests on macOS, Windows and Linux. When CI is green on the branch's latest commit, open a pull request into `dev` and merge it. Then give the owner the test command for `dev` (below). A red or unfinished run means there is nothing to test yet: fix it first.
- `main` is the owner's. Never push to it, never open or merge a pull request into it, never tag, never create or edit a release. The owner tests `dev` and merges `dev` into `main` when happy.
- A pull request follows [the template](.github/pull_request_template.md): what it does, how you know it works (what you broke on purpose to show the tests catch it), and what isn't covered. A green CI run isn't proof.

## Gates (what the tools enforce; details in docs/quality-plan.md)

- **Analyzers.** Every build runs .NET's recommended analyzers and the rules in `.editorconfig`, and every warning is an error. Fix the finding. A suppression is allowed only where the code stands, with `[SuppressMessage(..., Justification = "...")]`; never project-wide, never to get a change through.
- **File length.** `python3 tools/quality/length.py` fails above 600 lines. Files already over are in `tools/quality/length-baseline.json` and may only shrink; after splitting one, run it with `--ratchet`. Never raise or add an entry: split the file. The edit hook (`.claude/settings.json`) refuses an edit that would cross the limit; an edit that shrinks a file is never refused.
- **Tests.** `dotnet test -c Release` is green before a push. A bug fix starts with a test that fails without it. Never weaken, skip or delete a test to get green; if behaviour changed on purpose, say so in the commit. New tests go in a file for their area (`tests/MandarinDacBridge.Tests/<Area>Tests.cs`), not into `EndToEndTests.cs` or `UnitTests.cs`.
- **Pre-commit.** `git config core.hooksPath tools/hooks` once per clone. Never `--no-verify`.

## What no tool checks

- **Bit-perfect.** The samples reach the DAC unchanged: no software volume, no dither, no resampling unless the DAC can't take the track's rate (then SoX, to the nearest rate it takes in the same family). Volume is the DAC's own, or none.
- **Exclusive, one app at a time.** The bridge holds each DAC; it lets go only when the owner says (Share when idle) or when Caldera or Roon Bridge is switched on. Every way in goes through the arbiter: whatever plays keeps the others out.
- **Volume safety.** The bridge never raises a DAC's volume by itself: no level is applied at start or on connect. A check against a real DAC only ever lowers it.
- **Off by default.** Every connection besides UPnP starts switched off.
- **Third-party programs are downloaded, never shipped.** Spotify Soloist, Roon Bridge, Caldera Headless and QobuzProxy are fetched from their publishers when the owner asks, never committed or put in the image.
- **Secrets stay put.** The Soloist API key, the Plex token and Roon's token live in owner-only files. They never go to the page, the logs or git.
- **macOS, Windows and Linux.** A feature works on all three, or says plainly where it doesn't. CI runs all three; Docker is Linux.
- **Say what wasn't verified.** Measured on real hardware, tried against a stand-in, or not tried: each change says which (for example "not yet tried against a real Roon Core").
- **Docs travel with the code.** A user-visible change updates README.md and `docs/index.html` in the same branch. The site is published from `main`, so it changes when the owner merges.

## Testing a `dev` build (give the owner this after each merge into `dev`)

```bash
docker build -t mandarin-dac-bridge https://github.com/meltface-80/Mandarin-DAC-Bridge.git#dev
docker stop mandarin-dac-bridge 2>/dev/null; docker rm mandarin-dac-bridge 2>/dev/null
docker run -d --name mandarin-dac-bridge --network host --restart unless-stopped \
  --device /dev/snd --privileged -v /dev/snd:/dev/snd --cap-add SYS_NICE \
  -e TZ=Europe/London -v dac-bridge-data:/data mandarin-dac-bridge
```

The settings volume is kept. Add the `-e` settings the owner uses (for example `-e SOLOIST_VERBOSE=1`).

## Commits

A plain-English summary line, then what changed and why. End with the attribution lines the session gives. Commit with explicit paths when other work is in the tree.
