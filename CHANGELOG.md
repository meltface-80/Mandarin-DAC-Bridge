# Changelog

All notable changes to Mandarin DAC Bridge. Versions count up on `dev`; the version on `main` when
the owner publishes a release is the release (`dev/release.md`).

## [1.1.9] — 2026-10-13

### Fixed

- **A track at a rate the DAC can't take was resampled without SoX when ffmpeg had none.** The rule is
  SoX or nothing (`CLAUDE.md`, bit-perfect), but with an ffmpeg built without SoX (Homebrew's plain
  `ffmpeg`, which the Mac installer installed, and Chocolatey's `ffmpeg`) the bridge quietly used ffmpeg's own
  resampler instead. Now:
  - **Every install has SoX.** The Mac installer adds Homebrew's `ffmpeg-full` (ffmpeg with SoX; a
    few hundred MB, once), beside the plain ffmpeg Mandarin uses. The Windows installer adds winget's
    `Gyan.FFmpeg` (the full build) when no ffmpeg with SoX is there. The Docker image's has it.
    Chocolatey users: `choco install ffmpeg-full`.
  - **The bridge uses the ffmpeg with SoX** where several are installed (`ffmpeg-full`, kept off the
    PATH by Homebrew, is found by itself, and the Mac installer names it in the launch file), and says at
    start which one, and whether it has SoX. Only when it starts: `--list` and the other one-off
    commands ask no ffmpeg.
  - **Without SoX, such a track is refused** and says why, on the page and in the log, with what to
    install. Tracks the DAC takes at their own rate play as before with any ffmpeg.
  Class of error: a fallback that broke a rule quietly where it should have refused. Gates added:
  `SoxrTests` (the refusal, picking the ffmpeg with SoX, an ffmpeg that doesn't answer), and CI now
  installs ffmpeg with SoX on every system and fails if it hasn't got it, so the resampling tests
  test SoX. Run the Mac installer again to get `ffmpeg-full`. Not tried on a real Mac or Windows
  machine: the installers' new steps run only there.

## [1.1.8] — 2026-10-13

### Changed

- **`dev` and `main` hold the same code again.** Lyrion following the DAC (1.1.6, #19) and the docs
  corrections (#22) were merged straight into `main`, and the Compose fix (1.1.7, #21) into `dev`.
  This is all three together. Nothing in the program changes beyond them: the version is numbered
  past both so its test image is never mistaken for either.

## [1.1.7] — 2026-10-13

### Fixed

- **Update in Dockge (or Portainer) failed with "pull access denied for mandarin-dac-bridge".** The
  Compose files (the repository's and the one the site writes) build the bridge and name the image
  `mandarin-dac-bridge`, which exists nowhere to pull. Dockge's Update runs `docker compose pull`
  first, which asked Docker Hub for it and failed; and a plain `docker compose up -d` kept the image
  it already had instead of building the new code. Both files now say `pull_policy: build`: pull
  skips the bridge and every `up` builds the latest code. A Compose file made before this needs the
  line added under `dac-bridge:` (README, *Install with Docker*). Class of error: a Compose file that
  builds its image but leaves the pull policy at its default; the new `ComposeTests` fail on any
  service in either file that builds without `pull_policy: build`. Tried with Docker 29 and Compose
  5.6: `pull` fails before and is skipped after, and `up` rebuilds only with the line. Not tried in
  Dockge itself.

## [1.1.6] — 2026-10-13

### Fixed

- **Lyrion's stored volume still reached the DAC, when the music started.** 1.1.2 stopped the bridge
  putting Lyrion's volume on the DAC as the player connected, but Lyrion sends it again with every
  stream it starts, so a DAC at 30 with Lyrion's slider at 100 still went to 100 at the first track:
  1.1.2 moved the jump, it didn't remove it. Now the two share one volume (the owner's choice). On
  connecting, the bridge sets Lyrion's volume to the DAC's level (Lyrion's JSON-RPC) and tells it
  again whenever the DAC moves elsewhere; Lyrion's slider sets the DAC to the number it shows, read
  exactly from the gain Lyrion sends (`Slim/ServerVolume.cs`). Lyrion answers each level it is told
  with that level; those echoes are waited for and never taken as a move, so neither a late copy of
  what Lyrion had nor a late echo of an earlier level can move the DAC. A change made while another
  app has the DAC is refused and Lyrion's slider is put back, so it isn't applied later. Roon's
  Squeezebox support has no JSON-RPC: there, only its changes after connecting move the DAC, by as
  much as they move its slider, never to where its slider happens to be. Class: a safety fix proven
  against what the code was told rather than against the server's real behaviour. The stand-in now
  sends what LMS's code sends (its stored volume, its echo, its re-send at each stream); the tests
  fail if echoes aren't waited for, if the first echo isn't, if a change without JSON-RPC is taken
  as a level, if a refused change isn't put back, or if Lyrion isn't told when the DAC moves.
- **After a reconnect mid-track, Lyrion's slider did nothing until the next track** (1.1.2): it is
  followed again from Lyrion's echo of the level it is told on reconnecting.
- **A DAC set to Fixed when Lyrion connected** stays in step once switched to its own volume: the
  bridge decides how to take Lyrion's volume at the first volume Lyrion sends after the switch.
- **The "left alone" message filled the log** once per volume message (1.1.2); server discovery's
  silent catches now say why they are silent.

### Added

- **`LMS_JSON_PORT`**: Lyrion's web port when it isn't 9000 and the server is set by address (a port
  outside 1–65535 is ignored).

### Changed

- **One JSON-RPC request for Lyrion** (what plays and its volume), and the old gain-to-level curve,
  no longer used, is gone. Two oversized files shrink: finding Roon on this machine moved out of
  `Manager.cs` (`Roon/RoonRunning.cs`) and the Squeezebox test out of `EndToEndTests.cs`.

## [1.1.5] — 2026-10-13

### Changed

- **`dev` and `main` hold the same code again.** The same-origin check (1.1.3) was merged straight
  into `main` (#11), and the settings fixes (1.1.4) into both (#12, #15); merging them into `main`
  kept `main`'s version, 1.1.3, and dropped 1.1.4's entry below. Nothing in the program changes:
  this version is that code, numbered past both so its test image is never mistaken for either.

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

## [1.1.3] — 2026-10-12

### Fixed

- **Any website's page could change the bridge.** The bridge took a change from whoever sent it,
  so a page of another site, open in a browser anywhere on the network, could switch connections
  on, start downloads or turn a DAC up. Now a change that a browser says comes from another site
  (Sec-Fetch-Site, or Origin on older browsers) is refused with 403; the bridge's own page and
  programs that aren't browsers (UPnP controllers, a Squeezebox server, `curl`) go ahead. Class: a
  web API trusting every request that reaches it (cross-site request forgery). Gate: an end-to-end
  test sends another site's requests to the API, a download and a UPnP action and requires them
  refused with nothing changed, and fails without the check; unit tests cover each header case
  (three deliberate breaks of the rule each failed them). The password in `dev/quality.md` is
  still to come.

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
