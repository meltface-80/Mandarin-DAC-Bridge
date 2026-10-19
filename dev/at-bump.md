# At the next version bump

What the README and the site (`docs/`, GitHub Pages) must say once the owner bumps the version
(`dev/release.md`, *Bumping the version*). Each change on `dev` that users will see adds its item
here instead of editing them; the bump applies every item and empties this file.

## The bridge's own ffmpeg; no Homebrew (`dev/design/install.md`)

The README's Mac and Windows install sections, its *Decoding* paragraph, its `FFMPEG` row and its
*Development* paragraph, and the site's Mac and Windows install notes, its by-hand Mac install, its
uninstall notes and its `FFMPEG` row: the installers bring the bridge's own ffmpeg with SoX, the
Mac's needs no Homebrew, an earlier `ffmpeg-full` or `Gyan.FFmpeg` is left alone and how to remove
it. The exact edits, as written and checked against `dev` on 2026-10-19 (`git apply` from the
repository's root; if `dev` has moved, apply by hand):

```diff
diff --git a/README.md b/README.md
index a20adc2..7d8d3bf 100644
--- a/README.md
+++ b/README.md
@@ -54,12 +54,14 @@ macOS 14 or newer (macOS 27 included), Apple silicon or Intel. No Docker needed.
 3. **Done.** The page opens in your browser at **http://localhost:55500**. On a phone, open the
    address Terminal shows at the end. The bridge starts by itself whenever you log in.
 
-The installer uses the same Homebrew as Mandarin, and adds Homebrew's **ffmpeg-full**: ffmpeg
-built with SoX, the resampler the bridge needs (a few hundred MB, once). It sits beside the plain
-ffmpeg Mandarin uses and doesn't replace it. It downloads the bridge from this repository's latest
-**Release** into `~/Mandarin-DAC-Bridge`, and checks it against that release's checksums: a download
-that doesn't match is refused, and the bridge already there is left as it was. If there is no
-Release yet, it builds the bridge on the Mac instead, which takes a few minutes, once. If macOS asks to let **mandarin-dac-bridge** find
+The installer needs no Homebrew and installs nothing else. It downloads the bridge from this
+repository's latest **Release** into `~/Mandarin-DAC-Bridge`, with its own small ffmpeg built with
+SoX, the resampler the bridge needs (a few MB, made only of what the bridge uses), and checks them
+against that release's checksums: a download that doesn't match is refused, and the bridge already
+there is left as it was. If there is no Release yet, it builds the bridge and its ffmpeg on the Mac
+instead, which takes a few minutes, once, and asks for Apple's command line tools if they aren't
+there. Homebrew's **ffmpeg-full**, which earlier versions of the installer added, is left alone; if
+nothing else uses it, `brew uninstall ffmpeg-full` removes it. If macOS asks to let **mandarin-dac-bridge** find
 devices on your network, choose **Allow**. To update, paste the same line again.
 
 **Then point the apps at the bridge:**
@@ -84,9 +86,10 @@ Windows 10 or 11, x64 or ARM64. Open **Terminal** (right-click Start) and paste:
 powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/install.ps1 | iex"
 ```
 
-It installs ffmpeg with SoX (winget's `Gyan.FFmpeg`) if no ffmpeg with SoX is there, and the bridge into
-`%LOCALAPPDATA%\Mandarin-DAC-Bridge` (checked against the release's checksums, as on the Mac), starts it now and at every sign-in (a hidden scheduled
-task), and opens **http://localhost:55500**. If Windows asks to let **mandarin-dac-bridge** use the
+It installs the bridge and its own ffmpeg with SoX into `%LOCALAPPDATA%\Mandarin-DAC-Bridge`
+(checked against the release's checksums, as on the Mac), starts it now and at every sign-in (a
+hidden scheduled task), and opens **http://localhost:55500**. If there is no Release yet, it builds
+the bridge here, and then installs ffmpeg with SoX with winget (`Gyan.FFmpeg`) if none is there. If Windows asks to let **mandarin-dac-bridge** use the
 network, allow it on private networks. To update, paste the same line again.
 
 The bridge holds each USB DAC in **WASAPI exclusive mode**, so Windows' mixer never touches the
@@ -383,10 +386,13 @@ never touches that path.
   precision), to the nearest rate in the same family (44.1 or 48 kHz), or, on a DAC that takes
   no rate of that family, to its highest rate. SoX or nothing: with an
   ffmpeg built without SoX, such a track is refused, and the page says why (for a next track sent
-  ahead for gapless play, the log does, and playback stops after the track before it). The
-  installers and the Docker image bring an ffmpeg with SoX; Homebrew's plain `ffmpeg` and Chocolatey's `ffmpeg` have none
-  (Homebrew's `ffmpeg-full`, Chocolatey's `ffmpeg-full` and winget's `Gyan.FFmpeg` have it). Where
-  several are installed, the bridge uses the one with SoX, and its log says which at start.
+  ahead for gapless play, the log does, and playback stops after the track before it). Each
+  release carries the bridge's own ffmpeg with SoX beside it (`ffmpeg/`, built by
+  `tools/ffmpeg/build.sh` from ffmpeg's and libsoxr's pinned source, LGPL, with only what the bridge
+  uses; `ffmpeg/NOTICE.md` says what is in it), and the Docker image has Debian's, which has SoX.
+  Homebrew's plain `ffmpeg` and Chocolatey's `ffmpeg` have none (Homebrew's `ffmpeg-full`,
+  Chocolatey's `ffmpeg-full` and winget's `Gyan.FFmpeg` have it). The bridge uses its own first,
+  else one with SoX, and its log says which at start.
 * **Bit depth.** A track deeper than the DAC's widest format (24-bit on a DAC that takes 24 at most,
   or anything over 24-bit on a Mac) loses its lowest bits on the way, with no dither.
 * **DSD.** DSF and DFF files go to the DAC as DoP, untouched, where DoP is on for that DAC: on Linux
@@ -456,7 +462,7 @@ Pi-hole's DNS), add it to `BRIDGE_HOSTNAMES`, or the page is refused. An optiona
 | `BRIDGE_ALL_OUTPUTS` | — | `1` also offers non-USB outputs |
 | `BRIDGE_NAME_SUFFIX` | ` (Bridge)` | added to each DAC's name on the network |
 | `DATA_DIR` | `data/` beside the program | where `settings.json` lives |
-| `FFMPEG` | the one with SoX: Homebrew's `ffmpeg-full`, on the PATH, or Homebrew's or winget's | the decoder |
+| `FFMPEG` | its own beside it (`ffmpeg/bin`), else one with SoX: Homebrew's `ffmpeg-full`, on the PATH, or Homebrew's or winget's | the decoder |
 | `SQUEEZELITE` | — | `1` starts with the Squeezebox player on (the page's switch, once used, wins) |
 | `LMS_SERVER` | found by itself | the Squeezebox server, `host` or `host:port` |
 | `LMS_JSON_PORT` | 9000 | that server's web port, if `LMS_SERVER` is set and Lyrion's isn't 9000 |
@@ -517,8 +523,10 @@ switching between the two starts with empty settings. `docker volume ls` shows w
 
 ## Development
 
-.NET 10 SDK. ffmpeg is needed for the end-to-end tests; on Linux, libasound2 too (and PulseAudio
-for the Soloist test). CI runs the tests and a native build on macOS, Windows and Linux.
+.NET 10 SDK. ffmpeg with SoX is needed for the end-to-end tests; on Linux, libasound2 too (and
+PulseAudio for the Soloist test). CI runs the tests and a native build on macOS, Windows and Linux,
+with the bridge's own ffmpeg (`tools/ffmpeg/build.sh <rid> <folder>` builds it; put its `bin` first
+on the PATH to test with it too).
 
 `tools/check.sh` runs every gate and the whole suite (`tools/check.sh gates` the fast half).
 Changes go on their own branch from `dev`, into `dev` by a pull request the owner merges once CI is
diff --git a/docs/index.html b/docs/index.html
index 5553148..b51229c 100644
--- a/docs/index.html
+++ b/docs/index.html
@@ -283,16 +283,16 @@
         <button class="copy-btn" data-copy>Copy</button>
 <pre>/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/install.sh)"</pre>
       </div>
-      <div class="firstrun"><ol start="3"><li><b>Answer what it asks.</b> Your <b>Mac's password</b>, if Homebrew
-        needs installing (nothing shows as you type; that's normal). <b>Allow</b> if macOS asks to let
-        <b>mandarin-dac-bridge</b> find devices on your network.</li></ol></div>
+      <div class="firstrun"><ol start="3"><li><b>Answer what it asks.</b> <b>Allow</b> if macOS asks to let
+        <b>mandarin-dac-bridge</b> find devices on your network. Only if there is no ready-made build for your
+        Mac does it ask to install Apple's command line tools, to build it there.</li></ol></div>
       <div class="firstrun"><ol start="4"><li><b>Done.</b> The page opens in your browser at
         <b>http://localhost:55500</b> with a tile for each USB DAC. The bridge starts by itself whenever you log in.
         On a phone, open the address Terminal shows at the end.</li></ol></div>
-      <div class="note"><b>What it installs:</b> Homebrew (if missing; the same one Mandarin uses) and Homebrew's
-        <code>ffmpeg-full</code>, ffmpeg built with SoX, the resampler the bridge needs (a few hundred MB, once; it sits
-        beside Mandarin's ffmpeg). Then the bridge itself, one program in
-        <code>~/Mandarin-DAC-Bridge</code>. <b>To update</b>, paste the same line again; what you set on the page is
+      <div class="note"><b>What it installs:</b> the bridge, one program, and its own small ffmpeg built with SoX,
+        the resampler the bridge needs (a few MB), both in <code>~/Mandarin-DAC-Bridge</code>. No Homebrew. A
+        <code>ffmpeg-full</code> that an earlier installer added is left alone; if nothing else uses it,
+        <code>brew uninstall ffmpeg-full</code> removes it. <b>To update</b>, paste the same line again; what you set on the page is
         kept (environment variables added to the launch file by hand are not: add them again).</div>
       <details class="firstrun">
         <summary><b>Prefer to install by hand?</b></summary>
@@ -302,8 +302,7 @@
             <a href="https://github.com/meltface-80/Mandarin-DAC-Bridge/releases/latest">latest release</a>, then:</p>
           <div class="codebox">
             <button class="copy-btn" data-copy>Copy</button>
-<pre>brew install ffmpeg-full
-mkdir -p ~/Mandarin-DAC-Bridge
+<pre>mkdir -p ~/Mandarin-DAC-Bridge
 tar -xzf ~/Downloads/mandarin-dac-bridge-osx-*.tar.gz -C ~/Mandarin-DAC-Bridge
 xattr -dr com.apple.quarantine ~/Mandarin-DAC-Bridge   # downloaded by a browser: let macOS run it
 ~/Mandarin-DAC-Bridge/mandarin-dac-bridge --list    # the DACs it can see
@@ -332,8 +331,9 @@ xattr -dr com.apple.quarantine ~/Mandarin-DAC-Bridge   # downloaded by a browser
         network (private networks).</li></ol></div>
       <div class="firstrun"><ol start="4"><li><b>Done.</b> The page opens at <b>http://localhost:55500</b>. The bridge
         starts by itself at every sign-in.</li></ol></div>
-      <div class="note"><b>What it installs:</b> ffmpeg with SoX (winget's <code>Gyan.FFmpeg</code>, if none is there) and the bridge, one program in
-        <code>%LOCALAPPDATA%\Mandarin-DAC-Bridge</code>, started by a hidden scheduled task. <b>To update</b>, paste the
+      <div class="note"><b>What it installs:</b> the bridge, one program, and its own ffmpeg with SoX, in
+        <code>%LOCALAPPDATA%\Mandarin-DAC-Bridge</code>, started by a hidden scheduled task. (With no ready-made build
+        for the PC, it builds the bridge there, and gets ffmpeg with SoX from winget's <code>Gyan.FFmpeg</code>.) <b>To update</b>, paste the
         same line again; your settings are kept. In <b>Sound settings</b>, the DACs the bridge holds show as in use: that's
         the exclusive hold. Windows' own sound goes to another output.</div>
     </div>
@@ -543,8 +543,8 @@ xattr -dr com.apple.quarantine ~/Mandarin-DAC-Bridge   # downloaded by a browser
 <pre>/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/uninstall.sh)"</pre>
       </div>
       <div class="note">It stops the bridge, removes it from login, and deletes <code>~/Mandarin-DAC-Bridge</code>
-        (the program, its settings and its log). Homebrew and ffmpeg stay, because Mandarin uses them too. If
-        nothing else needs ffmpeg-full: <code>brew uninstall ffmpeg-full</code>. The DAC Bridge output, if you installed it, stays
+        (the program, its own ffmpeg, its settings and its log). Homebrew and its ffmpegs stay, because Mandarin
+        uses them too. If nothing else needs the ffmpeg-full an earlier installer added: <code>brew uninstall ffmpeg-full</code>. The DAC Bridge output, if you installed it, stays
         until <code>driver.sh</code> removes it (<a href="#mac-apps">above</a>), and a Roon Bridge the bridge downloaded
         stays in <code>~/Applications</code>.</div>
       <details class="firstrun">
@@ -564,7 +564,8 @@ rm -rf ~/Mandarin-DAC-Bridge</pre>
 <pre>powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/uninstall.ps1 | iex"</pre>
       </div>
       <div class="note">It stops the bridge, removes its scheduled task and firewall rule, and deletes
-        <code>%LOCALAPPDATA%\Mandarin-DAC-Bridge</code>. ffmpeg stays; <code>winget uninstall Gyan.FFmpeg</code> removes it.</div>
+        <code>%LOCALAPPDATA%\Mandarin-DAC-Bridge</code>, its own ffmpeg with it. An ffmpeg from winget stays;
+        <code>winget uninstall Gyan.FFmpeg</code> removes it.</div>
     </div>
 
     <div class="out" data-pane="run" hidden>
@@ -612,7 +613,7 @@ docker compose down --rmi local --volumes</pre>
           <tr><td>BRIDGE_ALL_OUTPUTS</td><td>&mdash;</td><td><code>1</code> also offers non-USB outputs.</td></tr>
           <tr><td>BRIDGE_NAME_SUFFIX</td><td>" (Bridge)"</td><td>Added to each DAC's name on the network.</td></tr>
           <tr><td>DATA_DIR</td><td>data/</td><td>Where <code>settings.json</code> lives (<code>/data</code> in Docker).</td></tr>
-          <tr><td>FFMPEG</td><td>on the PATH, or Homebrew's</td><td>The decoder.</td></tr>
+          <tr><td>FFMPEG</td><td>its own, beside it; else one with SoX</td><td>The decoder.</td></tr>
           <tr><td>SQUEEZELITE</td><td>&mdash;</td><td><code>1</code> starts with Squeezebox on (the page's switch, once used, wins).</td></tr>
           <tr><td>LMS_SERVER</td><td>&mdash;</td><td>The Squeezebox server's address, if it isn't found by itself.</td></tr>
           <tr><td>SPOTIFY</td><td>&mdash;</td><td><code>1</code> starts with Spotify Connect on (Spotify Soloist, Linux).</td></tr>
```
