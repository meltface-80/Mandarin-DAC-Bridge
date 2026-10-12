# Changelog

All notable changes to Mandarin DAC Bridge. Versions count up on `dev`; the version on `main` when
the owner publishes a release is the release (`dev/release.md`).

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
