# Mandarin DAC Bridge

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

```
 Audirvana ──UPnP──┐                         ┌──▶ exclusive, rate-switched ──▶ USB DAC 1
                   ├──▶  Mandarin DAC Bridge ┤
 Mandarin ───UPnP──┘      (port 55500)       └──▶ exclusive, rate-switched ──▶ USB DAC 2
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

### Publishing a build (for the repository owner)

On GitHub, go to **Releases → Draft a new release**, create a tag such as `v1.0.0` and click
**Publish**. GitHub Actions (`.github/workflows/release.yml`) then tests the code, builds it for
Apple silicon and Intel Macs and for Linux x64 and arm64, and attaches the four
`mandarin-dac-bridge-<platform>.tar.gz` files to the release. The installer always fetches the
latest release.

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

You can use `docker run` instead of Compose:

```bash
docker build -t mandarin-dac-bridge .
docker run -d --name mandarin-dac-bridge --restart unless-stopped \
  --network host \
  -v /dev/snd:/dev/snd --device-cgroup-rule='c 116:* rmw' \
  --cap-add SYS_NICE \
  -v dac-bridge-data:/data \
  mandarin-dac-bridge
```

* **`--network host` is required.** UPnP discovery uses multicast, which doesn't cross Docker's
  bridge network.
* **`/dev/snd` with the cgroup rule** gives the container the sound devices, including a DAC
  plugged in later. (`--device /dev/snd --privileged` also works, but only sees DACs that were
  connected when the container started.)
* If Mandarin runs on the same machine with `--device /dev/snd`, switch off its local output for
  the DACs. Otherwise both containers try to open them.

---

## How it works

| | macOS | Linux / Docker |
|---|---|---|
| Finding DACs | Core Audio (USB devices only) | `/proc/asound` (USB audio cards) |
| Exclusive hold | Core Audio **hog mode**, held all the time | the ALSA **`hw:`** device, held open |
| Rate switching | the DAC's physical format, set per track (integer, widest depth) | `hw` params, set per track (S32_LE, S24_LE, S24_3LE or S16_LE) |
| Bit-perfect | up to 24-bit (Core Audio's float path carries 24 bits exactly) | up to 32-bit |
| DSD | DoP (off by default; switch it on per DAC) | DoP, on automatically when the DAC reports native DSD |

The program calls Core Audio and ALSA directly from C#
(`Native/CoreAudio.cs`, `Native/Alsa.cs`). On a Mac, Core Audio's real-time thread reads the
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
* **Volume** stays at 100% (bit-perfect). Use the DAC's or amplifier's volume. Mute works.

`mandarin-dac-bridge --list` prints the DACs found and what each takes. The page's API is
`GET /api/dacs`. Each DAC's UPnP description is at `/upnp/<id>/description.xml`.

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

On the Mac these go in `~/Library/LaunchAgents/app.mandarin.dacbridge.plist`. In Docker, they go
under `environment:` in `docker-compose.yml`.

### Troubleshooting

* **The log:** `~/Mandarin-DAC-Bridge/data/bridge.log` on a Mac, `docker logs mandarin-dac-bridge`
  in Docker.
* **A DAC says "Waiting".** Another app holds it in exclusive mode, and the page names that app.
  Point the app at the bridge's UPnP device instead, or quit it.
* **The apps don't see "(Bridge)" devices.** Check that the bridge and the apps are on the same
  network. On a Mac, allow **mandarin-dac-bridge** under System Settings → Privacy & Security →
  Local Network. In Docker, check that you used `--network host`.
* **The Mac's volume for the DAC** is shown on the DAC's page. Set it to 100% in Audio MIDI Setup
  for bit-perfect playback.

---

## Development

.NET 10 SDK. ffmpeg is needed for the end-to-end tests; on Linux, libasound2 too.

```bash
dotnet test                                                     # unit and end-to-end tests
dotnet run --project src/MandarinDacBridge                      # run it
dotnet publish src/MandarinDacBridge -c Release -r osx-arm64    # one native program (osx-x64, linux-x64, linux-arm64)
```

The end-to-end tests run the whole bridge in-process and drive it over real SOAP from two
pretend apps: play, lockout, gapless, seek, pause, rate changes, DSD→DoP, resampling and UPnP
events. They use a sink that plays in real time to a clock (`Audio/ClockSink.cs`), and on Linux
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
