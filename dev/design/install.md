# A lighter install, the same on every system

Status: **draft for the owner's review.** No code depends on it until the open questions at the end
are answered. Prompted on 2026-10-19 by a report from a user on macOS 27 with a FiiO K5 Pro: the Mac
installer put about a hundred Homebrew packages on the machine, and the page then said "the DAC
doesn't take 44.1 kHz" for a DAC whose own panel listed 44.1 kHz. The second part is a bug, not an
install question. It is investigated here because it was in the same report, and its fix is its own
branch (below).

## Part 1: why the Mac install is so big

### What happens today

`tools/mac/install.sh`, step 2, runs `brew install ffmpeg-full`. Before that it installs Homebrew if
the Mac has none, and Homebrew installs Apple's command-line tools.

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

- **Decoding**: FLAC, ALAC, WAV, AIFF, MP3, AAC and raw PCM, read over `http`/`https` only
  (`StreamUri.FfmpegProtocols`), turned into 32-bit PCM in a WAV (`FfmpegSource`).
- **SoX**, only when the DAC can't take a track's rate (`CLAUDE.md`, *Bit-perfect*):
  `aresample=<rate>:resampler=soxr:precision=28` (`Sources.Open`). A DAC like the K5 Pro
  (44.1 to 768 kHz) never needs it. Without SoX, such a track is refused (`Audio/Soxr.cs`).

No video, subtitles, OCR, speech recognition, filters other than `aresample`, or network protocols
beyond HTTP(S). Almost everything `ffmpeg-full` installs is never used.

### The same question on the other systems

| System | Where ffmpeg with SoX comes from today | Weight |
|---|---|---|
| macOS | Homebrew → `ffmpeg-full` (and Homebrew and Apple's command-line tools first, if missing) | heavy: the report |
| Windows | winget → `Gyan.FFmpeg` (Gyan's full build); a user who already has the essentials build is told to remove it | one large download, but it needs winget, and the bridge has to find it among others |
| Linux / Docker | Debian's `ffmpeg` in the image (has libsoxr) | fine, it is in the image |

Each system gets ffmpeg in its own way, so each has its own failures: Homebrew's split, winget's
missing or broken, an essentials build found first.

### Options

**A. Keep Homebrew, but plain `ffmpeg` plus `libsoxr`, and the bridge calls SoX itself.** ffmpeg
decodes at the track's own rate; when resampling is needed the bridge passes the PCM through libsoxr
(P/Invoke) at the same quality as today (`precision=28`, SoX's very high quality). About a sixth of
today's packages. But it still needs Homebrew and Apple's tools, it moves resampling into the audio
path (owner's judgment, the multi-agent review), and Windows would need a `soxr.dll` from somewhere.
The weight is smaller, the problem is the same.

**B. No Homebrew: the installers download a ready-made static ffmpeg with SoX from a publisher of
ffmpeg builds,** pinned by SHA-256 like QobuzProxy (`Qobuz/QobuzProxy.lock`), into the bridge's
own folder (`~/Mandarin-DAC-Bridge/ffmpeg/bin/ffmpeg`; on Windows the bridge already looks in
`<its folder>\ffmpeg\bin`, `Config.Common`). One file, no Homebrew, no Apple tools, removed with the
folder. But it puts trust in a third party's build, which builds have libsoxr (checked with
`-buildconf`, as today) has to be confirmed per publisher and processor before choosing one, and
their URLs and hashes move when they publish.

**C. The release builds its own small ffmpeg, the same on every system (recommended).** `release.yml`
(and `test-image.yml` for test builds) builds ffmpeg from ffmpeg.org's source with only what the
bridge uses, statically, for each of the six builds, and puts it in the bridge's tarball/zip:

- `--disable-everything`, then: the decoders above, the `wav`, `flac`, `mov`/`mp4`, `aiff`, `mp3`,
  `aac` and raw-PCM demuxers, the `wav` muxer, the `aresample` filter with `--enable-libsoxr`
  (libsoxr built statically beside it), and the `http`/`https`/`tcp`/`tls` protocols (TLS from the
  system: Secure Transport on macOS, Schannel on Windows; Docker keeps Debian's ffmpeg).
- A few MB instead of a few hundred. LGPL only (libsoxr is LGPL; nothing GPL is enabled).
- Covered by the release's `SHA256SUMS` like the bridge itself (1.1.26): no new trust and no new
  publisher, since it is built from ffmpeg's own source by the owner's workflow.
- The installers stop installing ffmpeg at all: the Mac installer needs neither Homebrew nor Apple's
  tools (unless there is no release build and it has to build the bridge from source, as today),
  and the Windows installer no longer needs winget.
- The bridge looks for `ffmpeg` beside itself first (it already does on Windows), then where it looks
  today, so an install made with the old installer keeps working.
- The ffmpeg that can be refused for SoX, and the remedies telling users to install `ffmpeg-full`
  (`Soxr.Remedy`), go away for installed copies. They stay for a copy run from source.
- Cost: building ffmpeg in CI for six targets (minutes per release, cacheable), and the LGPL's duty
  that goes with handing it out: say which ffmpeg and libsoxr versions are inside, link to their
  source and the build options, and give the licence (a `THIRD-PARTY.md` in the archive).

**Not an option:** using Homebrew's plain `ffmpeg` alone. It has no SoX, so on a DAC that lacks a
track's rate that track would be refused. The bit-perfect rule allows SoX or nothing.

### Recommendation

C. It answers both parts of the request: the Mac install is the bridge and nothing else, and every
system gets the same ffmpeg in the same way, checked the same way. A has the same weight problem in a
smaller size. B is quicker, but it adds a third party the owner has to trust and keep watching.

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
  `best = -1` and it throws "the DAC doesn't take 44.1 kHz", and would for any rate.
- So the message is wrong: it says the DAC refuses 44.1 kHz, when what happened is that Core Audio
  listed no formats for the stream. Two parts of the bridge also disagree about the same DAC: the
  panel says it takes 44.1 kHz and the sink says it doesn't.
- `CoreAudio.GetArray` returns an empty array on every failure (the property missing, the size
  call failing, the read failing) and says nothing, so the log can't tell which one it was.

### Not a recent regression

`CoreAudioSink.cs`, `Native/CoreAudio.cs` and the Mac half of `Devices.cs` have not changed in
behaviour since 1.1.0. Whatever is new is on that machine: macOS 27, or how this DAC's driver
presents its stream.

### Root cause: not known yet

Hypotheses, none checked against a real Mac, so none is a root cause yet (`dev/lessons.md`):

1. On macOS 27 (or with this DAC), `pft?` on the stream fails or is empty, and the formats can
   only be read some other way (the current `pft `, the virtual formats `sfma`).
2. The DAC has more than one output stream and the first (`FirstOutputStream`) isn't the one with
   the formats.
3. A size or layout mismatch makes `GetArray` give up (`size < sizeof(T)`).

The existing `--diagnose` tells 1 and 3 apart from a working case only partly: it prints formats for
the first stream, and nothing at all when the list is empty.

### What is needed, in order

1. **From the user**, as it is today:
   `~/Mandarin-DAC-Bridge/mandarin-dac-bridge --diagnose`, and the lines of
   `~/Mandarin-DAC-Bridge/data/bridge.log` around the message.
2. **A diagnostic build** (its own branch, `fix/mac-formats-diagnose`): `--diagnose` prints every
   output stream of each device, the result code of each Core Audio call (`HasProperty`, the size,
   the read) for `pft?`, and the current physical (`pft `) and virtual (`sfmt`) formats and `sfma`.
   It changes no behaviour.
3. **The fix**, once the dump shows the cause (`fix/mac-formats`): a failing test first, on the
   pure part (`FromMac`'s and `Configure`'s choice of a format, moved out so it can be tested on any
   system). Whatever the cause, the message changes to say what is true ("Core Audio lists no formats
   for this DAC") instead of blaming the rate.

Whether the sink may fall back to the nominal rate and the current physical format when no list
comes back is open question 4: it is the audio path, and it means playing to a format the bridge
didn't choose.

## Open questions for the owner

1. **Which option for ffmpeg: A, B or C?** (Recommended: C.) It decides whether the release ships
   an ffmpeg, which is the owner's decision.
2. If C: is ffmpeg in the release archive acceptable under the rule that third-party programs are
   "downloaded from their publishers … never committed or put in the image"? Here ffmpeg is built
   from ffmpeg.org's source by the release workflow, not downloaded, committed or put in the Docker
   image, but it does ship.
3. If C: do the installers remove an `ffmpeg-full` they installed before? Recommended: no. It may be
   in use by something else, and removing Homebrew packages is the user's call. The installer would
   say it is no longer needed and how to remove it.
4. When Core Audio lists no physical formats, may the sink play at a nominal rate the device lists,
   in the stream's current physical format, or must it refuse (as now, with a truthful message)?
   Recommended: refuse until the dump shows why the list is empty.
5. May the user's `--diagnose` output be asked for in the issue tracker? It holds device names and
   UIDs, but no secrets.

## Not tried

Nothing here was run on a Mac, on macOS 27, or with a K5 Pro. The Homebrew dependency counts were
read from the formulas, not from an install. Building ffmpeg as described in C hasn't been tried in
CI.
