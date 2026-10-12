# Changelog

All notable changes to Mandarin DAC Bridge. Versions count up on `dev`; the version on `main` when
the owner publishes a release is the release (`dev/release.md`).

## [1.1.2] — 2026-10-12

### Fixed

- **A Squeezebox server's stored volume was put on the DAC the moment the player connected.**
  Switching Squeezebox on, restarting the bridge or a dropped connection could move a DAC (a
  headphone amp included) to the server's level, louder as easily as quieter. LMS sends its volume
  as a player connects and again with every stream it starts; the bridge now lets it move the DAC
  only once the server has asked that player to play, and what it sends on (re)connecting is left
  alone. Class: a safety rule ("no level is applied at start or on connect") that no test pinned;
  the old test sent the volume straight after connecting and expected the DAC to move. The test
  now proves the opposite on a first connect and on a reconnect, and fails if either guard is
  removed. Tried against a stand-in server that follows LMS's code, not a real LMS or Roon.

### Changed

- **Two oversized files shrink:** Squeezebox discovery and the strm parser moved out of
  `SlimPlayer.cs` (now under the length limit), and the volume test out of `EndToEndTests.cs`.

## [1.1.1] — 2026-10-12

### Added

- **A test image for every branch, to pull instead of building** (`test-image.yml`). Each push to a
  branch other than `main` runs every check, then publishes
  `ghcr.io/meltface-80/mandarin-dac-bridge:<version>-test` for x86-64 and arm64; a push to `dev`
  also moves the `dev` tag, which the owner's test box pulls. Each processor's image is built on a
  runner of that processor, as the release builds the program, since .NET's native compiler is
  not supported under emulation. It keeps the container name, the data volume and the port, so
  switching from a locally built image keeps every setting. Nothing changes for users on `main`,
  who still build the image themselves.

### Changed

- **The checks run once per push, inside the test image's run**, instead of separately as well;
  a pull request still runs them under the names the rulesets on `dev` and `main` require.

## [1.1.0] — 2026-10-12

### Added

- **Roon now playing, from Roon's extension API** (off by default; #5). Roon's Squeezebox support
  sends no track details, and Roon Bridge plays to a DAC without the bridge in between, so the pages
  and the now-playing screen showed little or nothing for Roon. With "Roon now playing" on and
  "Mandarin DAC Bridge" enabled in Roon's Settings → Extensions, each DAC shows its Roon zone's
  title, artist, album, cover and position. Not yet tried against a real Roon Core.
- **Quality gates that a machine enforces on every commit** (`dev/quality.md`). Nothing the bridge
  does changes; this is how the code is kept from getting worse.
  - **Tests that block.** CI (`checks.yml`) runs every gate and the whole suite on macOS, Windows
    and Linux on every push and pull request, and the release workflow now needs it green.
  - **Analyzers as errors.** .NET's recommended analyzers plus rules for bug classes this project
    has had: number parsing that depends on the machine's culture, locale-dependent comparison,
    return codes dropped without saying so, unused code. Nothing grandfathered.
  - **A file-length ratchet** (`tools/quality/length.py`). No file over 500 lines; the 7 already
    over are listed and may only shrink. It also runs at edit time for Claude Code.
  - **`tools/check.sh`**, the one command for all of it, and a pre-commit hook (`.githooks/`) for
    the fast half.
  - **The rules:** `CLAUDE.md` is a short rule sheet; procedure lives in `dev/` (quality, release,
    lessons, history); a pull request template asks for the evidence.

### Fixed

- **The Roon client took a message with no request number as request 0** (the first request it
  sent). Class: a parse result used without checking it succeeded; the analyzers now flag a dropped
  `TryParse` result (CA1806).
