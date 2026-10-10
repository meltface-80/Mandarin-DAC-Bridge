# Mandarin DAC Bridge

**📖 Install guide & command builder: [meltface-80.github.io/Mandarin-DAC-Bridge](https://meltface-80.github.io/Mandarin-DAC-Bridge/)**

**Lets Audirvana and Mandarin share your USB DACs, bit-perfect.** Written in C# (.NET 10) and
built ahead of time (Native AOT) into one small program. Nothing of .NET needs installing where
it runs.

On a Mac, Audirvana takes a USB DAC in exclusive (hog) mode to switch its sample rate for each
track. While it holds the DAC, nothing else can see it, so Mandarin finds no DAC. The bridge
fixes this by taking each DAC itself:

* It **holds every USB DAC exclusively**, playing or not, so no other app can grab it.
* It **switches the DAC to each track's own rate** and plays the samples unchanged
  (bit-perfect passthrough, with DSD as DoP).
* It **offers each DAC on the network as a UPnP renderer**, `"<DAC name> (Bridge)"`, which both
  Audirvana and Mandarin can play to.
* It **lets only one app control a DAC at a time.** Whichever app starts playing owns the DAC.
  If the other app tries to change anything while it plays, it is refused (UPnP error 705,
  *Transport is locked*), so the two never fight over it.
* It shows a **page on port 55500**: one tile per DAC with its name and model. Tap a tile to see
  what the DAC can do; the ✕ in the top-right corner takes you back.
* **Optionally** ([More ways to play](#more-ways-to-play)), each DAC is also a **Squeezebox player**
  (Lyrion Music Server, Roon), a **Spotify Connect speaker** (through Spotify Soloist, on Linux), a
  **Qobuz Connect speaker** (through QobuzProxy) and a **Plex player** (through Caldera Headless, on
  Linux). On a Mac, the **Music app and the Spotify app** play through it too. A DAC can be **shared
  with Roon Bridge** while idle, and a **now-playing screen** at `/now` shows what's playing on a
  monitor beside it. Everything optional is off until you switch it on.
* It runs on **macOS, Windows and Linux** (or Docker).

```
 Audirvana ───── UPnP ──────┐                          ┌──▶ exclusive, rate-switched ──▶ USB DAC 1
 Mandarin ────── UPnP ──────┤                          │
 Lyrion / Roon ─ Squeezebox ┤                          │
 Spotify app ─── Connect ───┼──▶  Mandarin DAC Bridge ─┤
 Qobuz app ───── Connect ───┤      (port 55500)        │
 Plexamp ─────── Plex ──────┤    /now: now playing     │
 Music / Spotify on the Mac ┘                          └──▶ exclusive, rate-switched ──▶ USB DAC 2
```

---

## Install on a Mac

macOS 14 or newer (macOS 27 included), Apple silicon or Intel. No Docker needed.

1. **Open Terminal**: press Command-Space, type **Terminal** and press Return.
2. **Paste this line** and press Return:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/install.sh)"
```

3. **Done.** The page opens in your browser at **http://localhost:55500**. On a phone, open the
   address Terminal shows at the end. The bridge starts by itself whenever you log in.

The installer uses the same Homebrew and ffmpeg as Mandarin, so it installs nothing new if
Mandarin is already on the Mac. It downloads the bridge from this repository's latest
**Release** into `~/Mandarin-DAC-Bridge`. If there is no Release yet, it builds the bridge on the
Mac instead, which takes a few minutes, once. If macOS asks to let **mandarin-dac-bridge** find
devices on your network, choose **Allow**. To update, paste the same line again.

**Then point the apps at the bridge:**

* **Audirvana:** Settings → Audio → choose **"<DAC> (Bridge)"** in the UPnP list, *not* the DAC
  itself. While Audirvana still holds the DAC, its tile says *Waiting · Audirvana has the DAC*.
  The bridge takes the DAC the moment Audirvana lets go of it.
* **Mandarin:** Settings → Audio Devices → choose **"<DAC> (Bridge)"**. If Mandarin's own *local*
  output for that DAC is switched on, switch it off: the bridge owns the DAC now.

To remove it:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/uninstall.sh)"
```

## Install on Windows

Windows 10 or 11, x64 or ARM64. Open **Terminal** (right-click Start) and paste:

```powershell
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/install.ps1 | iex"
```

It installs ffmpeg with winget (if it isn't there) and the bridge into
`%LOCALAPPDATA%\Mandarin-DAC-Bridge`, starts it now and at every sign-in (a hidden scheduled
task), and opens **http://localhost:55500**. If Windows asks to let **mandarin-dac-bridge** use the
network, allow it on private networks. To update, paste the same line again.

The bridge holds each USB DAC in **WASAPI exclusive mode**, so Windows' mixer never touches the
samples, and switches it to each track's own rate. In *Sound settings* the DACs show as in use:
that's the hold. To remove it:

```powershell
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/uninstall.ps1 | iex"
```

### Publishing a build (for the repository owner)

On GitHub, go to **Releases → Draft a new release**, create a tag such as `v1.0.0` and click
**Publish**. GitHub Actions (`.github/workflows/release.yml`) then tests the code, builds it for
Apple silicon and Intel Macs, Linux x64 and arm64, and Windows x64 and ARM64, and attaches
`mandarin-dac-bridge-<platform>.tar.gz` (`.zip` for Windows) to the release. The installers always
fetch the latest release.

---

## Install with Docker (DietPi, Intel i5, any Linux)

If Docker isn't installed: `dietpi-software install 162` (Docker) and `134` (Docker Compose) on
DietPi.

```bash
git clone https://github.com/meltface-80/Mandarin-DAC-Bridge.git
cd Mandarin-DAC-Bridge
docker compose up -d --build
```

Then open **http://<server-ip>:55500**. To update: `git pull && docker compose up -d --build`.
The image builds the bridge with the .NET 10 SDK, then keeps only the native program and ffmpeg.

You can use `docker run` instead of Compose. This builds straight from GitHub, so no clone is
needed (it needs `git` on the host):

```bash
# Build the bridge from GitHub (first time, and to update)
docker build -t mandarin-dac-bridge https://github.com/meltface-80/Mandarin-DAC-Bridge.git#main

# Replace any existing container (the settings volume is kept)
docker stop mandarin-dac-bridge 2>/dev/null
docker rm mandarin-dac-bridge 2>/dev/null

docker run -d \
  --name mandarin-dac-bridge \
  --network host \
  --restart unless-stopped \
  --device /dev/snd \
  --privileged \
  -v /dev/snd:/dev/snd \
  --cap-add SYS_NICE \
  -e TZ=Europe/London \
  -v dac-bridge-data:/data \
  mandarin-dac-bridge
```

* **`--network host` is required.** UPnP discovery uses multicast, which doesn't cross Docker's
  bridge network.
* **`--device /dev/snd`, `--privileged` and `-v /dev/snd:/dev/snd` together** let the container
  open the DACs: `--device` hands the sound devices over, `--privileged` lets the container open
  them, and the `/dev/snd` mount keeps the device list current, so a DAC plugged in later is
  found too.
* If Mandarin runs on the same machine with `--device /dev/snd`, switch off its local output for
  the DACs. Otherwise both containers try to open them.

---

## More ways to play

UPnP is always on. Everything here is optional, and goes through the same exclusive,
rate-switched path and the same rule: whatever is playing keeps the others out until it stops
(and for 10 seconds after).

### Squeezebox (squeezelite)

Switch on **Squeezebox** under *Connections* on the page (or set `SQUEEZELITE=1`). Each DAC
becomes a Squeezebox player, `"<DAC> (Bridge)"`, for **Lyrion Music Server** and for **Roon**
with *Settings → Setup → Squeezebox support* on. The server is found by itself (UDP broadcast on
3483); type its address on the page, or set `LMS_SERVER`, if it isn't.

The bridge speaks the Squeezebox protocol (SlimProto) itself instead of running the squeezelite
program, because squeezelite opens the DAC itself, and the bridge holds it. The server's stream
goes through ffmpeg like a UPnP track. FLAC, PCM (WAV, AIFF, raw), MP3 and Ogg are taken as they
come, and gapless: the bridge asks for the next track when it has read the current one. DSD is
converted to PCM by the server. On a DAC with a volume of its own, LMS's volume sets the DAC's (see
[Volume](#volume)); for a DAC feeding an amplifier, set the player's volume control in LMS to *fixed at
100%*. Title,
artist and cover come from LMS's JSON-RPC (Roon's Squeezebox support has none, so its tracks show
just the format).

### Spotify Connect, through Spotify Soloist (Linux)

[Spotify Soloist](https://developer.spotify.com/documentation/soloist) is Spotify's own headless
Spotify Connect client for Linux. With it switched on, each DAC appears in the Spotify app's device
list as `"<DAC> (Bridge)"`. It needs:

* **Your own Soloist API key.** Make it at
  [Spotify for Developers → Soloist](https://developer.spotify.com/dashboard/soloist) (needs Spotify
  Premium) and type it on the page. It is kept in `settings.json` (readable by its owner only) and
  never shown again. You can also set `SOLOIST_API_KEY`.
* **Soloist itself.** Spotify doesn't allow it to be redistributed, so the bridge doesn't include it:
  **Download Soloist** on the page fetches the official build for this machine's processor
  (x86-64, ARM64 or ARM32) from Spotify into `DATA_DIR/soloist`, the way RoPieee fetches Roon Bridge.
  Each build lasts 90 days; the page shows until when, and an expired build that the bridge
  downloaded is fetched again by itself.
* **Linux with glibc 2.38 or later** (Debian 13, Ubuntu 24.04, Raspberry Pi OS trixie), or the
  **Docker image**, which has it. Debian 12, DietPi on bookworm and Raspberry Pi OS bookworm are
  too old to run Soloist natively; Docker works there. Soloist doesn't exist for macOS.
* **PulseAudio** (`sudo apt install pulseaudio`; it's in the Docker image). The bridge runs it
  privately, with no sound card: it doesn't touch the system's sound.

How it fits: Soloist only plays to PipeWire or PulseAudio, and the bridge holds the DAC. So each DAC's
Soloist is started with a private PulseAudio server of its own that has no sound card, only a pipe
sink: Soloist's sound comes out of a named pipe as 32-bit samples at 44.1 kHz, and the bridge plays
them through the same exclusive, rate-switched path as everything else. The pipe waits for its
reader, so the DAC's clock sets the pace: nothing is resampled and nothing drifts, and 16- and
24-bit audio arrive unchanged. Soloist's WebSocket (on 127.0.0.1) tells the bridge what's playing
(title, artists, album, cover, position) and when Spotify plays, pauses or stops. If another app has
the DAC when Spotify starts, the bridge tells Soloist to pause, so the Spotify app shows it didn't
play. On a DAC with a volume of its own, the Spotify app's volume sets the DAC's, and a change made
elsewhere moves Spotify's slider too. On a DAC without one, Spotify's volume stays at 100%
(bit-perfect): a change in the Spotify app is put back.

Soloist takes the API key only on its command line, so other accounts on the same machine could read
it from the process list. On a machine you share with others, run the bridge in Docker. On a Mac,
the Spotify app itself plays through the bridge instead ([below](#this-mac-the-music-app-and-the-spotify-app)).

### Qobuz Connect, through QobuzProxy (Mac, Windows, Linux)

Switch on **Qobuz Connect** on the page (or set `QOBUZ=1`) and press **Install QobuzProxy**: it
fetches [QobuzProxy](https://github.com/leolobato/qobuz-proxy) (MIT) from GitHub into a private
Python environment in `DATA_DIR/qobuz` (Python 3.10 or later must be installed; the Docker image has
it). Then **Sign in to Qobuz** opens QobuzProxy's own page (port 8689). Each DAC then appears in the
Qobuz app's device list as `"<DAC> (Bridge)"`.

QobuzProxy speaks Qobuz Connect and hands each track to the DAC's UPnP device, so Qobuz plays
through the bridge like Audirvana does, under the same one-app-at-a-time rule. The bridge tells it
the DAC's best rate and depth, so it streams hi-res up to what the DAC takes. The bridge starts
QobuzProxy again when DACs come or go.

### Plex, through Caldera Headless (Linux)

Switch on **Plex · Caldera Headless** on the page (or set `CALDERA=1`), press **Download
Caldera** (it fetches [Caldera Headless](https://caldera.homes/music/amp/#linux) for this machine's
processor), then **Sign in to Plex**: the page shows a code to enter at
[plex.tv/link](https://plex.tv/link). Each DAC then appears as a player in Plexamp, Plex for iOS and
the Plex web app. The Plex token stays in `settings.json` (owner-only) and is never sent to the page.

Caldera plays to the DAC itself, bit-perfect, with its own player per DAC. The bridge shares the DAC
with it: while nothing plays through the bridge, it lets go of the DAC so Caldera can open it, and
it takes the DAC back when an app plays to the bridge and Caldera has let go (Caldera releases it
when idle). Whoever plays first has it. What Caldera plays shows on the DAC's page and on `/now`.

### This Mac: the Music app and the Spotify app

> **New.** On a test Mac the DAC Bridge output gives back what it gets, sample for sample, at 44.1,
> 96 and 192 kHz. A click can come only when the Mac misses an audio deadline (then macOS moves the
> output's clock on); the bridge reads it with a large buffer so that it doesn't. Playing from the
> Music and Spotify apps themselves hasn't been tried on a real Mac yet.

Neither app can play to a DAC the bridge holds, or switch the DAC's rate per track. So, as
[Arco](https://github.com/renebouwmeester/arco) does for Roon, a small virtual output, **DAC
Bridge**, becomes the Mac's output while this is on. The app plays to it, and the bridge plays the
same samples to the DAC you choose:

```
 Music / Spotify ──▶ "DAC Bridge" output ══ loopback ══▶ the bridge ──▶ exclusive, rate-switched ──▶ DAC
```

* **The track's own rate.** The Music app names each track's rate (AppleScript); the bridge sets the
  virtual output to it, pausing the app around the change. Spotify is 44.1 kHz.
* **Bit-perfect.** Samples come back as floats, which carry 24 bits exactly.
* **No drift.** The virtual output's clock follows the DAC's: the bridge nudges it (within ±0.1%)
  from how full its buffer runs, so the app is paced by the DAC.
* Title, artist, album, cover and position show on the DAC's page and on `/now`.

Install the output once (it builds the driver with Apple's command line tools, and asks for your
password; the Mac's sound stops for a few seconds):

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/driver.sh)"
# remove it again:  … driver.sh)" -- --remove
```

Then switch on **This Mac: Apple Music & Spotify** on the page (or set `MAC_APPS=1`) and pick the
DAC. The first time, macOS asks to allow **Microphone** access: that's how the bridge reads the DAC
Bridge output back (it never listens to a microphone). Switched off, the Mac's output goes back to
what it was. `mandarin-dac-bridge --loopback-test` checks that the output gives back exactly what
it's given, at 44.1, 96 and 192 kHz. The driver is adapted from Arco's (Apache-2.0; see
`tools/mac/driver`).

### Roon Bridge, beside the bridge

Roon Bridge can't be bundled with the bridge (Roon's licence), and Roon's own playback protocol
(RAAT) is closed, so it can't play *through* the bridge. It can run *beside* it and share the
DACs. Like RoPieee, this downloads Roon's own installer from Roon Labs and runs it (Linux):

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/linux/roon-bridge.sh)"
```

On a Mac, install [Roon Bridge for macOS](https://download.roonlabs.net/builds/RoonBridge.dmg).
In Docker, install Roon Bridge on the host, not in the container. Then on each DAC Roon should
use, switch on **Share when idle**. The bridge then lets go of that DAC whenever nothing plays through
it (no app playing, none in its 10-second grace), so Roon Bridge can open it. When Audirvana,
Mandarin, LMS or Spotify play to the bridge, it takes the DAC back, if Roon has let go of it.
While Roon is playing, they are refused with *the DAC is held by another program*. The tile says
*Shared · free for other players* or *Shared · RoonBridge is playing*.

Or skip Roon Bridge: with the Squeezebox player on, Roon's Squeezebox support plays to the
bridge directly (with Roon's limits for Squeezebox players, and not as Roon Ready).

### The now-playing screen

`http://<machine>:55500/now`, full-screen in any browser: cover, title, artist, album, time and
format of whatever is playing (from any app), and a slowly drifting clock when nothing is. It
follows whichever DAC is playing; move the mouse to pick one (or add `?dac=<id>`). On a Linux
machine with a monitor plugged in and no desktop (DietPi, Raspberry Pi OS Lite), this sets up
cage and Chromium to show it from boot:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/linux/display.sh)"
# a bridge on another machine:  … -- --url http://192.168.1.20:55500/now     remove: … -- --uninstall
```

---

## How it works

| | macOS | Windows | Linux / Docker |
|---|---|---|---|
| Finding DACs | Core Audio (USB devices only) | the MMDevice API (USB endpoints) | `/proc/asound` (USB audio cards) |
| Exclusive hold | Core Audio **hog mode**, held all the time | **WASAPI exclusive mode**, event-driven | the ALSA **`hw:`** device, held open |
| Rate switching | the DAC's physical format, set per track (integer, widest depth) | the exclusive stream's format, per track (32, 24 or 16-bit) | `hw` params, set per track (S32_LE, S24_LE, S24_3LE or S16_LE) |
| Bit-perfect | up to 24-bit (Core Audio's float path carries 24 bits exactly) | up to 32-bit | up to 32-bit |
| DSD | DoP (off by default; switch it on per DAC) | DoP (off by default; switch it on per DAC) | DoP, on automatically when the DAC reports native DSD |
| Optional | Music and Spotify apps, Qobuz | Qobuz | Spotify Connect, Qobuz, Plex |

The program calls Core Audio, WASAPI and ALSA directly from C#
(`Native/CoreAudio.cs`, `Native/Wasapi.cs`, `Native/Alsa.cs`). On a Mac, Core Audio's real-time thread reads the
samples from a buffer in native memory (`Audio/CoreAudioSink.cs`), so .NET's garbage collector
never touches that path.

* **Decoding.** ffmpeg turns whatever the app sends (FLAC, WAV, AIFF, ALAC, raw L16/L24, MP3,
  AAC…) into 32-bit integer PCM at the track's own rate, which carries 16- and 24-bit audio
  exactly. Only when the DAC can't take that rate is the track resampled (SoX, 28-bit
  precision), to the nearest rate in the same family (44.1 or 48 kHz).
* **Gapless.** If the app sends the next track ahead of time (`SetNextAVTransportURI`) and it has
  the same rate, it plays straight on with no gap. A next track at a different rate waits for the
  first to finish, then the DAC switches rate.
* **Who's in control.** The bridge tells the apps apart by their requests (User-Agent and
  address; Mandarin is recognised by its stream port, 3500). The app that's playing owns the
  DAC, and keeps it for 10 seconds after it stops or pauses, so it doesn't lose the DAC between
  tracks. Status reads are open to every app. The DAC's page shows who is in control and who was
  kept out. **Release** hands the DAC over at once.
* **Volume.** See [below](#volume). Mute works.

`mandarin-dac-bridge --list` prints the DACs found and what each takes, and every other output it saw with why it was passed over. `mandarin-dac-bridge --diagnose` prints everything the machine reports about its sound devices. The page's API is
`GET /api/dacs`, and `GET`/`POST /api/services` for the optional connections. Each DAC's UPnP description is at `/upnp/<id>/description.xml`.

### Volume

Many USB DACs, headphone DACs above all (the AudioQuest DragonFly, for one), have no volume knob:
the computer sets their volume, and the DAC applies it in its own chip, after the samples arrive.
The stream stays bit-perfect. Where a DAC has such a volume, the bridge uses it:

* the apps' volume controls set it: Audirvana, Mandarin and other UPnP controllers, the Spotify
  app, the Qobuz app and LMS;
* the DAC's page has a slider;
* a change made elsewhere on the machine (alsamixer, the Mac's or Windows' own volume) shows up
  on the page and in the apps.

While another app is playing, a different app's volume change is refused, as for everything else.
On the DAC's page, **Volume: Fixed** leaves the volume where it is, and the apps see 100: use it for
a DAC feeding an amplifier with its own volume. A DAC without a volume of its own always plays at
100% (the bridge never changes the samples to make it quieter): use the amplifier's volume.

The DAC's volume is the ALSA mixer control alsamixer shows for the card (usually *PCM*) on Linux,
Core Audio's device volume on a Mac, and the endpoint volume on Windows when the DAC applies it
itself.

### Settings (all optional)

| Variable | Default | |
|---|---|---|
| `PORT` | `55500` | the page and the UPnP devices |
| `BRIDGE_IP` | — | the address to advertise, if the machine is on several networks |
| `LOCK_GRACE_S` | `10` | how long an app keeps a DAC after it stops or pauses |
| `MANDARIN_PORT` | `3500` | Mandarin's port, used to recognise its streams |
| `BRIDGE_ALL_OUTPUTS` | — | `1` also offers non-USB outputs |
| `BRIDGE_NAME_SUFFIX` | ` (Bridge)` | added to each DAC's name on the network |
| `DATA_DIR` | `data/` beside the program | where `settings.json` lives |
| `FFMPEG` | `ffmpeg` on the PATH, or Homebrew's | the decoder |
| `SQUEEZELITE` | — | `1` starts with the Squeezebox player on (the page's switch, once used, wins) |
| `LMS_SERVER` | found by itself | the Squeezebox server, `host` or `host:port` |
| `SPOTIFY` | — | `1` starts with Spotify Connect on (Spotify Soloist, Linux) |
| `SOLOIST_API_KEY` | — | your Soloist API key (or type it on the page) |
| `SOLOIST` | downloaded by the page, or on the PATH | the soloist program |
| `QOBUZ` | — | `1` starts with Qobuz Connect on (QobuzProxy) |
| `QOBUZPROXY_SOURCE` | the QobuzProxy release on GitHub | another place to install QobuzProxy from (a local copy, a fork) |
| `CALDERA` | — | `1` starts with Plex through Caldera Headless on (Linux) |
| `MAC_APPS` | — | `1` starts with the Music and Spotify apps on (macOS) |

On the Mac these go in `~/Library/LaunchAgents/app.mandarin.dacbridge.plist`. On Windows, set them
as user environment variables (`setx PORT 55501`) and sign in again. In Docker, they go under
`environment:` in `docker-compose.yml`.

### Troubleshooting

* **The log:** `~/Mandarin-DAC-Bridge/data/bridge.log` on a Mac,
  `%LOCALAPPDATA%\Mandarin-DAC-Bridge\data\bridge.log` on Windows, `docker logs mandarin-dac-bridge`
  in Docker.
* **A DAC says "Waiting".** Another app holds it in exclusive mode, and the page names that app.
  Point the app at the bridge's UPnP device instead, or quit it.
* **The apps don't see "(Bridge)" devices.** Check that the bridge and the apps are on the same
  network. On a Mac, allow **mandarin-dac-bridge** under System Settings → Privacy & Security →
  Local Network. In Docker, check that you used `--network host`.
* **The DAC doesn't show in Spotify.** Check *Connections* on the page: it says what Soloist is
  waiting for (the key, a download, PulseAudio, a newer Linux). The phone and the bridge must be on
  the same network, without client isolation on the Wi-Fi.
* **No Squeezebox player in LMS.** Check *Connections* on the page: it says which server it's
  connected to. If it's still looking, type the server's address there (broadcasts don't cross
  subnets or VPNs).
* **The Music app plays but the DAC is silent.** Allow **mandarin-dac-bridge** under System
  Settings → Privacy & Security → Microphone, and check the Mac's output is **DAC Bridge**.
  `mandarin-dac-bridge --loopback-test` checks the output itself.
* **Qobuz doesn't list the DAC.** Check *Connections* on the page: it says whether QobuzProxy is
  installed, running and signed in.
* **The volume doesn't move, or there's no slider.** The DAC has no volume of its own that the
  machine can set (or the DAC's page is set to *Fixed*). Use the amplifier's volume.

---

## Development

.NET 10 SDK. ffmpeg is needed for the end-to-end tests; on Linux, libasound2 too (and PulseAudio
for the Soloist test). CI runs the tests and a native build on macOS, Windows and Linux.

```bash
dotnet test                                                     # unit and end-to-end tests
dotnet run --project src/MandarinDacBridge                      # run it
dotnet publish src/MandarinDacBridge -c Release -r osx-arm64    # one native program (osx-x64, linux-x64, linux-arm64, win-x64, win-arm64)
```

The end-to-end tests run the whole bridge in-process and drive it over real SOAP from two
pretend apps: play, lockout, gapless, seek, pause, rate changes, DSD→DoP, resampling and UPnP
events. A pretend Squeezebox server (gapless, stop, status) and a pretend Soloist (a script
that plays a tone through the bridge's private PulseAudio, and a WebSocket in the test) drive the other two ways in, and a shared DAC is let go and taken
back. They use a sink that plays in real time to a clock (`Audio/ClockSink.cs`), and on Linux
also the real ALSA path.

| File | What it does |
|---|---|
| `Program.cs` | starts everything (`BridgeHost`); `--list`, `--version` |
| `Manager.cs` | finds the DACs; one bridge (sink, renderer, arbiter) per DAC; settings |
| `Devices.cs` | what each DAC is and what it takes |
| `Renderer.cs` | the transport, the position, the audio path |
| `Sources.cs` | ffmpeg decoding, resampling only when needed, DSD as DoP |
| `Dsd.cs` | DSF/DFF parsing and DoP packing |
| `Audio/` | the sinks: `CoreAudioSink` (hog mode, physical format, IOProc), `AlsaSink` (`hw:`), `ClockSink` (tests) |
| `Native/` | the C calls: Core Audio, ALSA |
| `Arbiter.cs` | which app is in control |
| `Upnp/` | SSDP, the device description, SOAP control, GENA events |
| `Web.cs` | the HTTP server (Kestrel): the page, the API, UPnP |
| `Page/` | the page, built into the program |
