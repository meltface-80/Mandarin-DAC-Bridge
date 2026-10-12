# Changelog

All notable changes to Mandarin DAC Bridge. Versions count up on `dev`; the version on `main` when
the owner publishes a release is the release (`dev/release.md`).

## [1.1.4] — 2026-10-12

### Fixed

- **A `settings.json` that couldn't be read was silently replaced by the defaults.** The bridge
  started with every DAC's settings and the services' secrets gone, said nothing, and overwrote the
  file at the next change. Now a file that isn't settings (cut short, edited by hand) is moved aside
  (`settings.json.unreadable-…`) and the log says why; one that can't be opened at all (held by
  another program, its permissions) is left as it is and never written over; `{"dacs":null}` no
  longer stops the bridge starting. Roon's tokens (`roon-api.json`) get the same care, where a bad
  file used to drop the other Cores' tokens. Class: a silent catch that lost data. Gate: tests start
  from a cut-short file, a file held open and a null section, and require the file kept byte for byte
  or the bridge started (the first failed before; each guard was removed once and its test failed).
- **Secrets files were written, then made owner-only**, and a crash midway could leave them cut
  short. `settings.json` and `roon-api.json` are now created owner-only and written whole through a
  new file of their own (`OwnerOnlyFile`). The tests require mode 600 and fail if the writer creates
  files others can read; the moment between writing and restricting in the old code can't itself be
  shown by a test.
- **Caldera's copy of the Plex token was in folders others could read** (`DATA_DIR/caldera`, its
  HOME, and its player folders). They are now their owner's only (the Caldera test requires it, and
  failed before). What Caldera prints (its log lines and errors, which reach the log and the page)
  is cleaned of the owner's token and any `X-Plex-Token=` in a URL. The token is still on Caldera's
  command line for the moment `caldera-music --set` runs; the README says so.
- **Moving the volume slider rewrote `settings.json` each time**, though a level isn't kept. It
  writes only when something kept changes.
- **The README said Docker hides the Soloist key** from other accounts on the machine. It doesn't.

### Changed

- **Two oversized files shrink:** the settings classes moved out of `Manager.cs` into `Settings.cs`,
  and the Caldera test out of `EndToEndTests.cs` into `CalderaTests.cs`.

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

## [1.0.0] — 2026-10-10

The first version, before versions were moved with each change (`dev/history.md` says which pull
request carried what).

### Added

- **Exclusive, bit-perfect USB DACs for Audirvana and Mandarin.** The bridge holds each USB DAC,
  switches it to each track's own rate, plays the samples unchanged (DSD as DoP) and offers it on
  the network as a UPnP renderer, `"<DAC> (Bridge)"`, controlled by one app at a time. A page on
  port 55500 shows each DAC. Native programs for macOS, Windows (WASAPI exclusive mode) and Linux,
  and a Docker image; installers for the Mac and Windows, and the install site.
- **More ways to play, each off until switched on:** a Squeezebox player per DAC (Lyrion, Roon's
  Squeezebox support); Spotify Connect through Spotify Soloist (Linux); Qobuz Connect through
  QobuzProxy; Plex through Caldera Headless (Linux), with Plex sign-in and sign-out; the Mac's Music
  and Spotify apps through a virtual output; Roon Bridge, downloaded from Roon Labs, beside the
  bridge; share-when-idle per DAC; and a now-playing screen at `/now`.
- **The DAC's own volume**, where it has one, from every app and the page.
