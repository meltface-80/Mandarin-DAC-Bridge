# A lighter install, the same on every system

Status: **decided by the owner on 2026-10-19** (the decisions are at the end). Prompted on
2026-10-19 by a report from a user on macOS 27 with a FiiO K5 Pro: the Mac installer put about a
hundred Homebrew packages on the machine, and the page then said "the DAC doesn't take 44.1 kHz"
for a DAC whose own panel listed 44.1 kHz. The second part is a bug, not an install question. It is
investigated here because it came in the same report, and it ships as its own version (1.1.30)
before the bundled ffmpeg (1.1.31).

## Part 1: why the Mac install is so big

### What happens today

`tools/mac/install.sh`, step 2, runs `brew install ffmpeg-full`. Before that it installs Homebrew if
the Mac has none, and Homebrew installs Apple's command-line tools. No release has been published
yet, so every Mac also builds the bridge from source, with a private .NET SDK.

In 2025 Homebrew split its ffmpeg in two. The formulas as they stand today (ffmpeg 9.0.2, read from
`Homebrew/homebrew-core` on 2026-10-19):

| Formula | Direct dependencies | Has libsoxr (SoX) |
|---|---|---|
| `ffmpeg` | 12 (`dav1d`, `lame`, `libvmaf`, `libvpx`, `openssl@4`, `opus`, `sdl2-compat`, `svt-av1`, `x264`, `x265`, `xz`, …) | **no** |
| `ffmpeg-full` | 46, among them `tesseract` (OCR), `whisper.cpp` and `ggml` (speech recognition), `rubberband`, `libplacebo`, `libbluray`, `aom`, `rav1e`, `zeromq`, `libssh`, `srt` | yes |
| `libsoxr` | none (cmake, to build only) | it is SoX's resampler |

The dependencies of those dependencies make up the list in the report: about a hundred formulae and
several hundred MB, for one library the bridge uses, `libsoxr`, which depends on nothing.

### What the bridge needs from ffmpeg

- **Decoding** what the renderer says it plays (`Upnp/Control.cs`): FLAC, WAV, AIFF, ALAC and AAC
  (MP4), MP3, Ogg (Vorbis, Opus, FLAC), DSD files (DSF, DFF) when the DAC takes no DoP, and raw
  PCM, read over `http`/`https` only (`StreamUri.FfmpegProtocols`), written as 32-bit PCM in a WAV
  to its output pipe (`FfmpegSource`).
- **Filters:** `pan` (mono to stereo, more channels down to two) and, only when the DAC can't take a
  track's rate (`CLAUDE.md`, *Bit-perfect*), `aresample=<rate>:resampler=soxr:precision=28`
  (`Sources.Open`). Without SoX such a track is refused (`Audio/Soxr.cs`).

No video, subtitles, OCR, speech recognition, other filters, or network protocols beyond HTTP(S).
Almost everything `ffmpeg-full` installs is never used.

### The same question on the other systems

| System | Where ffmpeg with SoX comes from today |
|---|---|
| macOS | Homebrew → `ffmpeg-full` (and Homebrew and Apple's command-line tools first, if missing) |
| Windows | winget → `Gyan.FFmpeg` (Gyan's full build); a user who already has the essentials build is told to remove it |
| Linux / Docker | Debian's `ffmpeg` in the image (has libsoxr) |

### The options weighed

- **A.** Keep Homebrew, use plain `ffmpeg` plus `libsoxr`, and have the bridge call SoX itself. Still
  Homebrew and Apple's tools, resampling moved into the audio path, and nothing for Windows.
- **B.** The installers download a ready-made static ffmpeg from a third-party builder. This adds a
  publisher to trust and keep watching.
- **C.** The release builds its own small ffmpeg, the same on every system. **Chosen by the owner.**

Not an option: Homebrew's plain `ffmpeg` alone. It has no SoX, and the bit-perfect rule allows SoX
or nothing.

## The bridge's own ffmpeg (C), as built

### What it is

- **ffmpeg 9.0.2** from `https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz` and **libsoxr 0.1.3**
  from `https://downloads.sourceforge.net/project/soxr/soxr-0.1.3-Source.tar.xz`, each refused
  unless its SHA-256 is the one pinned in `tools/ffmpeg/sources.lock`. The pins are the ones
  Homebrew's formulas carry for the same files; libsoxr's was also checked here by downloading it.
- Built by **`tools/ffmpeg/build.sh <rid> <out>`**, one script for all six builds and for a Mac that
  builds from source. It needs a C compiler, `make`, `curl` and a POSIX shell, and nothing else: no
  cmake, no nasm, no pkg-config, no Homebrew.
  - **libsoxr** is compiled directly, without its cmake: its scalar engines only (`cr32`, `cr64`,
    `vr32`; `soxr-config.h` written by the script). `precision=28` uses `cr64`, SoX's
    double-precision engine, the same quality as now. Without the SIMD engines it is slower, but a
    single stereo track takes a small fraction of one core.
  - **ffmpeg** with `--disable-everything --disable-autodetect`, then only: the decoders, demuxers
    and parsers for the formats above; the `pcm_s32le` and `flac` encoders; the `wav`, `flac`, raw
    PCM and `null` muxers; the `pan`, `aresample`, `aformat`, `anull`, `atrim` and `sine` filters and
    the `lavfi` input; the `http`, `https`, `tcp`, `tls`, `crypto`, `httpproxy`, `data`, `file` and
    `pipe` protocols; `--enable-libsoxr`. LGPL (no `--enable-gpl`); static, with no shared FFmpeg
    libraries.
  - `sine`, `lavfi`, the `flac` encoder and `file` are there for the test suite, which makes its test
    tones with `ffmpeg -f lavfi -i sine=…`, so the suite runs against the very ffmpeg users get. The
    bridge itself never names them: a controller's stream is held to `StreamUri`'s protocols by
    `-protocol_whitelist`, and no `movie`/`amovie` filter, which could read files, is built.
  - **TLS** from the system, as Debian's ffmpeg has it from OpenSSL: Secure Transport on macOS,
    Schannel on Windows, the system's OpenSSL 3 on Linux (the one shared library the Linux build
    needs besides the C library). Certificate checking is ffmpeg's default, unchanged from today.
  - The script runs the result once: `-buildconf` must list `--enable-libsoxr`, and a second of
    `sine` must resample with SoX (not run for a build made for another processor).
- Each release archive carries it beside the program:

  ```
  mandarin-dac-bridge(.exe)
  ffmpeg/bin/ffmpeg(.exe)
  ffmpeg/NOTICE.md        what it is, its versions, source URLs and SHA-256, the configure line, how to rebuild it
  ffmpeg/COPYING.LGPLv2.1 ffmpeg's licence
  ffmpeg/LICENCE.soxr     libsoxr's
  ```

  LGPL's terms for handing out a static build are met by naming the exact sources, the script and
  the configure line, all public: anyone can rebuild it, or swap in their own ffmpeg (`FFMPEG`).
  The bridge only runs ffmpeg as a separate program; nothing of ffmpeg is linked into the bridge.

### Decision 2: shipping it is allowed

`CLAUDE.md` says third-party programs (Soloist, Roon Bridge, Caldera, QobuzProxy) are downloaded
from their publishers and never committed or put in the image. ffmpeg here is different: it is
built from ffmpeg.org's own source by the release workflow (or by the Mac installer), is never
committed, and is not put in the Docker image. It does ship in the release archives, and the owner
decided that is what is needed (2026-10-19). `CLAUDE.md` says so under *Owner's decisions*.

### Where it is used

- **The bridge** looks for `ffmpeg/bin/ffmpeg` beside itself first, on every system, then where it
  looks today (Homebrew's `ffmpeg-full`, the PATH, winget's and the package managers' folders).
  `FFMPEG` still overrides. Of those, the first with SoX is taken, as today (`Soxr.Pick`).
- **The Mac installer:** from a release, unpacks the program and `ffmpeg/` together, checked against
  `SHA256SUMS` as one archive, and names the bundled ffmpeg in the launch file (`FFMPEG`). No
  Homebrew and no Apple tools. With no release, it builds the bridge (as today) and then this ffmpeg
  with `tools/ffmpeg/build.sh`, using the Apple tools that building already needs, and no Homebrew.
- **The Windows installer:** from a release, the same, and winget is not used. With no release, or a
  release from before 1.1.31 without `ffmpeg\`, it falls back to winget's `Gyan.FFmpeg` as today:
  building ffmpeg needs MSYS2, which a user's PC doesn't have.
- **CI** (`checks.yml`) builds it on macOS, Windows (MSYS2) and Linux, cached by the hash of
  `tools/ffmpeg/`, puts it first on the PATH, and runs the whole suite with it. Homebrew's
  `ffmpeg-full` and Chocolatey's are no longer installed there.
- **The release** (`release.yml`) builds it for all six: natively on each runner, the Intel Mac on
  the Apple silicon runner for x86_64, Windows ARM64 on the Windows ARM runner. Each build's
  archive holds it, and `SHA256SUMS` covers the archive.
- **Docker** keeps Debian's ffmpeg, which has SoX and gets Debian's security updates.

### Decision 3: an earlier `ffmpeg-full` is left alone

The installers never remove an `ffmpeg-full` (or Homebrew, or `Gyan.FFmpeg`) they installed before:
something else may use it, and removing packages is the user's call. The Mac installer says once
that the bridge no longer needs it, and how to remove it (`brew uninstall ffmpeg-full`). The
uninstallers say the same.

## Part 2: "the DAC doesn't take 44.1 kHz"

### What the report shows

- The DAC's panel: sample rates 44.1 to 768 kHz, **bit depths "not reported", formats "–"**,
  running at 44.1 kHz.
- The card: "the DAC doesn't take 44.1 kHz".

### What the code does with that

- The panel's rates come from `Devices.FromMac`: from the stream's physical formats
  (`kAudioStreamPropertyAvailablePhysicalFormats`, `pft?`) when there are any, **else from the
  device's nominal rates** (`nsr#`). Bit depths and formats come only from the physical formats. So
  "not reported" and "–" mean the physical-format list came back **empty**, and the rates shown are
  the fallback.
- `CoreAudioSink.Configure` looks only at that same list, with no fallback. An empty list leaves
  `best = -1`, and it throws "the DAC doesn't take 44.1 kHz", and would for any rate.
- So the message is wrong: it says the DAC refuses 44.1 kHz, when what happened is that Core Audio
  listed no formats for the stream.
- `CoreAudio.GetArray` returns an empty array on every failure (the property missing, the size
  call failing, the read failing) and says nothing, so the log can't tell which one it was.

### Not a recent regression

`CoreAudioSink.cs`, `Native/CoreAudio.cs` and the Mac half of `Devices.cs` have not changed in
behaviour since 1.1.0. Whatever is new is on that machine: macOS 27, or how this DAC's driver
presents its stream.

### Root cause: not known yet

Hypotheses, none checked against a real Mac, so none is a root cause yet (`dev/lessons.md`):

1. On macOS 27 (or with this DAC), `pft?` on the stream fails or is empty, and the formats could
   only be read some other way (the current `pft `, the virtual formats `sfma`).
2. The DAC has more than one output stream, and the first (`FirstOutputStream`) isn't the one with
   the formats.
3. A size or layout mismatch makes `GetArray` give up (`size < sizeof(T)`).

### Decision 4: refuse, probe, and say why (1.1.30)

- **Refused, never guessed.** When no linear-PCM physical format covers the track's rate, the DAC
  is not played. There is no fallback to the nominal rate or to the current format.
- **Probed when it happens.** Before refusing, the sink asks Core Audio again and records what it
  said: for each of the device's output streams, `pft?`, `pft `, `sfma` and `sfmt` (whether the
  property exists, the result code and size of the size call and of the read, and what was read),
  and the device's `nsr#` and `nsrt`, with the macOS version.
- **The error says what is true.** Which one depends on what was read:
  - no stream at all: "Core Audio shows no output stream for this DAC";
  - a stream, but no formats listed: "Core Audio lists no formats for this DAC (macOS …), so it
    can't be set to 44.1 kHz";
  - formats listed, none covering the rate: "the DAC doesn't take 44.1 kHz (it lists: 48 kHz, …)",
    the message from before, now true.
  Each ends with where the details are: "what Core Audio said is in the log; `mandarin-dac-bridge
  --diagnose` prints it all".
- **The log** gets the whole probe, once per DAC and rate until the answer changes (so a controller
  retrying doesn't fill it).
- **`--diagnose`** prints the same probe for every output device.
- **Tested** on any system: the choice of a format, the wording of each refusal and the probe's
  report are pure code (`Audio/MacFormats.cs`) given what Core Audio said. Reading Core Audio itself
  (`Native/CoreAudioProbe.cs`) runs only on a Mac: CI's macOS runner runs `--diagnose`, which
  probes its devices.
- The fix of the cause follows once a user's probe shows it.

## The owner's decisions (2026-10-19)

1. **Option C**: the release builds its own small ffmpeg with SoX, for every system.
2. **Shipping it in the release archives is allowed**, under the conditions above.
3. **An earlier `ffmpeg-full` (or `Gyan.FFmpeg`) is never removed** by the installers.
4. **A DAC whose formats can't be read is refused, probed, and the error and the log say why**;
   no fallback.

## Not tried

Nothing here was run on a Mac, on macOS 27, or with a K5 Pro. The Homebrew dependency counts were
read from the formulas, not from an install. The ffmpeg build was tried here on Linux x64 only, with
Ubuntu's ffmpeg 6.1.1 source (ffmpeg.org can't be reached from this environment); 9.0.2 and the other
five targets are built first by CI.
