# Changelog

All notable changes to Mandarin DAC Bridge. Versions count up on `dev`; the version on `main` when
the owner publishes a release is the release (`dev/release.md`).

## [1.1.26] — 2026-10-13

### Added

- **A release now carries checksums, and the installers check the build they download against
  them.**
  - The Mac and Windows installers downloaded the bridge from the latest release and ran it with
    nothing to check it against. A corrupted download, or a build re-uploaded wrongly, would have
    been installed and started at every login.
  - `release.yml` now attaches `SHA256SUMS`, the SHA-256 of each of the six builds. The installers
    find the latest release's tag once, and fetch the build and `SHA256SUMS` from that same
    release, so a release published mid-install can't mix them. Then:
    - a build that doesn't match, or that the sums don't list, is refused;
    - a release from before this one (no `SHA256SUMS`) is installed unchecked, as before;
    - GitHub out of reach, or an error other than "not there", stops the installer. It is never
      taken as "no checksums" or "no release".
  - In every one of those cases, the bridge already installed is left as it was. The installers
    used to stop the running bridge before downloading; now they stop it only to put the new one
    in place.
  - What this guards against: corruption, and a build and sums that don't belong together. It
    doesn't guard against someone who can replace a release's files: they can replace
    `SHA256SUMS` too. The installer script itself (`curl … | bash`, `irm … | iex`) can't check
    itself, and a build from the source isn't checked against anything.
  - Gate: `InstallerTests` runs each installer's own functions, taken from the script, against a
    pretend GitHub:
    - finding the tag, including no release and GitHub out of reach;
    - telling a file from "not there" from a failure;
    - the checksum check, including a build that can't be read.
    Seven mutants each fail a case. The PowerShell cases ran here on PowerShell 7.6 and run on the
    Windows and Linux runners.
  - Not tried: Windows PowerShell 5.1 (what the Windows one-liner runs), a real release, or a real
    install. The first release from `main` after this merges is the first with `SHA256SUMS`.
  - Found by the 2026-10-13 audit (S7, second part).

## [1.1.25] — 2026-10-13

### Added

- **Soloist, Caldera Headless and Roon Bridge are logged with their size and SHA-256 when downloaded.**
  - These programs come from their publishers and run as the bridge (as root, in Docker). None of
    the publishers offers a checksum to compare against, and nothing recorded what had been
    fetched.
  - Each is now saved whole to a file first, then unpacked from the file. A download is logged
    with where it came from, its size and its hash, for example
    `downloaded soloist-builds.spotifycdn.com/soloist_release_x86_64.tar.gz: 41235923 bytes,
    SHA-256 <hex>`. What ran, or what was refused, can then be compared with what the publisher,
    or another install, got. A signed link's query is never logged.
  - Before, Soloist and Caldera were unpacked as they streamed in. Now:
    - an archive that turns out to be wrong is still logged;
    - a part-download is deleted;
    - a body that goes on past 1 GiB (none of these programs is that big) is refused, rather
      than holding the page's Download.
    The log failing doesn't fail the download.
  - This doesn't stop a bad download: there is nothing to check it against. The release's own
    checksums, and QobuzProxy (installed by pip) pinned to known versions, are separate changes.
  - Gate: seven cases in `DownloadTests`. Seven mutants each fail one: never logged, logged
    twice, only the first read hashed, no size limit, a log failure failing the download, a
    part-download left behind, the link's query logged.
  - Found by the 2026-10-13 audit (S7, first part).

## [1.1.24] — 2026-10-13

### Fixed

- **Another website could read the bridge's page through a name of its own.**
  - A site can point a name of its own at the bridge's address (DNS rebinding). To the browser,
    its page and the bridge are then one site. Changes were already refused unless the browser
    had reached the bridge by one of its own names, but reads were not. Such a page could read
    `/api/dacs` and `/api/services`: the Plex account, the Qobuz email, the hostname, what is
    playing.
  - Anything from a browser, a read too, now goes ahead only when the browser reached the bridge
    by its address, `localhost`, or one of this machine's names (`BRIDGE_HOSTNAMES` adds one).
    Programs that aren't browsers (UPnP controllers, a Squeezebox server, `curl`) are unaffected.
  - Who notices: someone who opens the page by a name the bridge doesn't know now gets a refusal
    naming `BRIDGE_HOSTNAMES`, where before they could look but not change anything.
  - Not closed: a browser too old to send `Sec-Fetch-Site` says nothing on a read from the
    page's own site, so it can't be told from a program.
  - Class of error: a check on who is asking applied to changes but not to reads of private
    data.
  - Gate: `AReadFromABrowser_MustBeAddressedToThisMachine`, and reads added to the end-to-end
    rebinding case. Both failed before the fix (5 cases).
  - Found by the 2026-10-13 audit (S5).

## [1.1.23] — 2026-10-13

### Fixed

- **A cover could be used to reach other machines through the bridge.**
  - The bridge fetches each DAC's cover itself, from the server that has it (a Plex server named
    in Caldera's timeline, or a Roon Core), and passes it on to anyone who asks for it. The fetch
    followed redirects and took a reply with no type as a JPEG. A server could redirect the bridge
    to `127.0.0.1` or another machine on the network, and the bridge relayed the answer.
  - A cover is now only what that same server sends:
    - a redirect is followed only to another path on the same server, at most three times;
    - it must be said to be a JPEG (`image/jpg` counts), PNG, WebP or GIF;
    - it must be no more than 16 MiB, said or sent, and arrive within 15 seconds, from asking to
      its last byte.
    Anything else is no cover, and the page gets a 502. Before, a cover that couldn't be fetched
    gave an empty 200.
  - The fetch keeps no cookies and renews its connections every 5 minutes, like the bridge's
    other clients. A cover of a known size is read straight into its own buffer.
  - The Music app's covers are files on the Mac and are read as before.
  - Class of error: a fetch on another's behalf that goes wherever the server it asked sends it.
  - Gate: `CoversTests`. Ten mutants each fail a case: a redirect to another server followed;
    redirects not capped; no time limit; a reply with no type taken as a JPEG; either size check
    dropped; the limit off by one; `image/jpg` not taken.
  - Not tried against a real Plex server or Roon Core. A cover either serves through a redirect
    to another host is no longer shown.
  - Found by the 2026-10-13 audit (S4).

## [1.1.22] — 2026-10-13

### Fixed

- **Roon's token could go to any device claiming to be the Core.**
  - The bridge finds a Roon Core by asking the network, and it took the first answer. Anything on
    the network can answer first and give the real Core's id, which the Core's registry hands to
    anyone. The bridge then sent that device the extension's token. Whoever holds the token can
    register as the bridge and control every zone in Roon.
  - Each token is now kept with the address (host and port) of the Core that gave it, and sent
    only there. Of the Cores that answer, the one the bridge was enabled in is chosen first.
  - Once enabled in a Core, the bridge talks to no other: a Core at another address, or giving
    another id, is left and the enabled one looked for again (the page's Roon status says so). It
    isn't registered with either: it could say it had enabled the bridge and have its "token" kept
    in place of the real one.
  - A token is kept only under the id the Core gave as its own, and only under an id that is one:
    an id with `|` in it would have named another id's address.
  - A Core that has really moved (a new DHCP lease, `ROON_CORE` changed, or switching between
    `ROON_CORE` and finding it on the network): switch **Roon now playing** off and on. Switching
    it off forgets the addresses and keeps the tokens, so Roon needn't be asked again.
  - A token with no address (kept before this version, or since Roon now playing was switched
    off) is bound to the first Core it is sent to (trust on first use). If that binding can't be
    written, the token isn't sent.
  - Not closed: that first use, and, when the bridge is enabled in no Core yet, a device that
    answers first and fakes being enabled (it gets no token, but it is then the Core the bridge
    talks to until Roon now playing is switched off and on). With `ROON_CORE`, the address is the
    name as written, so it is as trusted as that name's lookup.
  - Class of error: a secret sent to a peer identified only by what it says about itself.
  - Gate: `RoonTokenTests` and the `Roon_` cases in `RoonTests.cs`. Ten mutants each fail at least
    one: a token sent whatever the address or port, or by host alone; another Core not left, or
    still registered with; ids not checked (the core id or the one given in Registered); no binding
    of a token with no address; switching off not forgetting, or forgetting the tokens too; no
    preference for the Core that gave the token.
  - Not tried against a real Roon Core: a pretend Core stands in. Not tested: a binding that can't
    be written (the tests run as root here).
  - Found by the 2026-10-13 audit (S3).

## [1.1.21] — 2026-10-13

### Fixed

- **A crafted DSD file could make the bridge run out of memory.**
  - A DSF file's header was checked only for positive values. With a block size of 2^30, the
    bridge waited for one whole block group before sending a sample, holding everything it read
    until it ran out of memory. Any controller can point the bridge at such a file, and DoP is the
    default for DSD-capable DACs.
  - Headers are now held to what DSD is:
    - DSF: blocks of 4096 bytes, 1 to 6 channels, 1- or 8-bit samples;
    - DFF: at least one channel;
    - both: DSD32 to DSD1024, in the 44.1 or 48 kHz family.
  - Whatever a header says, no more than 1 MiB now waits to be packed; past that the file is
    refused.
  - A DFF with an odd number of bytes per channel now ends at its last byte. Before, everything
    after the samples (a comment, cover art) was read and held until the end of the track.
  - DSD in the 48 kHz family is now named for it ("DSD64 (48 kHz)", not "DSD70").
  - Class of error: sizes from a file trusted as allocation sizes.
  - Gate: `DsdMalformedTests`. The old header checks failed 8 of the new cases; the packer
    without its limit failed 1; the old rounding failed the odd-last-byte case. Files built to the
    formats' specs at DSD32 to DSD1024, in both families, with 1 to 6 channels, still parse.
  - Not tried with real DSD files: the files are built by the tests.
  - Found by the 2026-10-13 audit (S2).

## [1.1.20] — 2026-10-13

### Fixed

- **Any device on the network could have the bridge read files on this machine.**
  - A UPnP controller's track address went to ffmpeg as given, and any device on the network can act
    as a controller.
  - `file:///…`, or ffmpeg's own file readers (`concat:`, `subfile:`, `data:`), had ffmpeg read
    files on this machine; `live:…` attached a controller to another app's sound.
  - Every track now meets one rule where it reaches ffmpeg: an `http` or `https` stream, nothing
    else, whichever way it came in.
  - UPnP controllers are checked earlier too: anything else is refused (716) and logged before it
    can take the DAC. A blank address no longer takes the DAC before failing.
  - ffmpeg may now use only the network's protocols, so a playlist it fetches (HLS) can't send it
    to a file either.
  - Class of error: input from the network handed to a program that reads more than the network.
  - Gate: `UpnpControlTests` (seven kinds of address refused, a blank one too, the DAC left
    untaken) and `SourcesTests` (the address rule, the refusal where tracks open, and ffmpeg's exact
    protocol list). Each failed with its fix taken out.
  - Found by the 2026-10-13 audit (S1).

## [1.1.19] — 2026-10-13

### Fixed

- **A release could be built under the wrong version, or published with builds missing.**
  - The version came from the tag alone. A tag that wasn't `vX.Y.Z` built "1.0.0", and one naming
    another version built that version, whatever the code said.
  - A release now builds the `.csproj`'s version (`tools/release/version.sh`). A tag that names
    another is refused, with the tag to use, and each build checks that it reports that version.
  - A build that made no file was skipped silently, and the release went out with what there was.
    Now a missing file fails its build, and the release checks all six are there before attaching
    any.
  - The whole workflow could write to the repository. Now only the job that attaches the builds can.
  - Values from the event and the matrix reach commands through `env`, never written into them
    (also in `checks.yml`).
  - Class of error: a release step trusting its input (the tag, the files made) instead of checking
    it.
  - Gate: `WorkflowTests`, on the version script and on every workflow's commands and permissions.
    They failed on the old workflows and with the old version rule.
  - Not run as a real release: that is the owner's.
  - Found by the 1.1.9 audit.

## [1.1.18] — 2026-10-13

### Fixed

- **Windows: every track's end waited five seconds, and pausing in those seconds threw the end away and
  unpaused the DAC.**
  - At a track's end the bridge waits for what the DAC still holds to play out. On Windows, the
    silence the DAC always has queued after the last samples was counted as played as soon as it
    was handed over. The end looked unplayed until the wait's five-second limit ran out.
  - So each track's end, and each change of format between tracks, waited five seconds. The
    position shown also lagged by the silence queued.
  - Silence now counts only once the DAC's clock has passed it (`Audio/Handed.cs`), so the wait ends
    when the last sample has played, plus 60 ms for what is still on its way out of the DAC (as on
    macOS). A clock that can't say for a moment no longer ends the wait.
  - A pause during that wait ended it: the rest of the track was thrown away and the DAC unpaused,
    while the page still showed it paused. The wait now goes on through a pause, and the end plays
    when the pause ends, as on macOS (on Linux, likewise on a DAC that can pause; on one that can't,
    pausing drops what the DAC holds, as it always has).
  - Its five-second limit (for a DAC that stops taking samples) counts only time spent playing
    (`Audio/DrainWait.cs`).
  - A pause now also survives setting up the next track, as on the other sinks: seeking while paused
    no longer starts playing.
  - Resuming restarts a stream that only the DAC's own buffer still held.
  - A DAC found gone on starting is reported, as it is while playing, and the feed stops writing to
    it.
  - Class of error: silence counted when handed rather than when played, and one sink's stop
    condition differing from the others' (a pause read as "nothing more to play").
  - Gate: `AudioTests`, on the counting and the wait, which failed with the old rules.
  - Not tried on a real DAC under Windows (CI has none).
  - Found by the 1.1.9 audit.

## [1.1.17] — 2026-10-13

### Fixed

- **A download that stalled left the page's Download doing nothing until the bridge restarted.**
  - Spotify Soloist, Caldera Headless and Roon Bridge were fetched with no time limit. A publisher
    that answered and then sent nothing (or a connection lost without a reset) was waited on for
    ever, and the download stayed "in progress", so each later click did nothing.
  - A download now gives up when nothing has come for a minute, says so ("… sent nothing for 60
    seconds: try again later", or that the publisher couldn't be reached), and can be started again.
    One that is slow but still sending is never cut short.
  - Setting Roon Bridge up (Roon's check, Apple's tools) gives up after ten minutes, and the step
    that hung is stopped rather than left running.
  - A download that fails no longer leaves a part-written file behind.
  - plex.tv is given 15 seconds per question: whose sign-in it is, the code to enter, and each check
    that it was entered. Before, one unanswered question held the sign-in up for its whole 15
    minutes.
  - Class of error: a request through the streaming client (`Sources.Http`, which has no timeout
    because a track streams for as long as it plays) with nothing to end it.
  - Gate: `DownloadTests` and `PlexLinkTests`, against a pretend publisher and a pretend plex.tv
    that never answer, stop sending, or are slow but sending. Each check failed with its limit taken
    out. A test lists each file that uses `Sources.Http` with what ends its requests, so a new one
    has to say. The unused `CalderaDownload.Latest`, the one other use with no limit, is removed.
  - Found by the 1.1.9 audit.

### Changed

- **A UPnP events test no longer depends on the runner's speed.** `EventsGoOneAtATime_InOrder` waited
  for exactly five events. Changes within 150 ms of each other go as one, by design, so a slow runner
  (macOS CI) sent four, and the test timed out. It now waits for the last state and checks the order
  and that one event at a time was in flight. Reproduced by shortening the gap between changes.

## [1.1.16] — 2026-10-13

### Fixed

- **Soloist's folders were open to other accounts, and your API key could reach its log, the bridge's
  log and the page.**
  - Its data folder holds its Spotify sign-in and was readable by everyone. It is now its owner's only,
    as is the folder of its log.
  - Its run folder held its sound socket and the pipe the DAC is fed from. It was readable by everyone,
    under a fixed name in `/tmp` that another account could take first. It is now made new for each
    run, under a name no one can guess, its owner's only, and removed when Soloist stops.
  - Soloist repeats the key it was given, on its output and over its WebSocket. Everything it says is
    now cleaned of the key wherever it goes: its log, the bridge's log and the page.
  - A log kept by an earlier version is cleaned of the key and made its owner's only.
  - Its log is created owner-only rather than restricted after its first line.
  - Class of error: a secret's neighbourhood left at the system's default permissions.
  - Gate: the Spotify test checks each folder and the run folder's name, and has its pretend Soloist
    say the key both ways, over an old log that holds it. Each check failed with its fix taken out.
  - Found by the 1.1.9 audit.

### Changed

- **`SoloistPlayer.cs` is under the 500-line limit** (640 → 437): its sound path moved to
  `Spotify/SoloistSound.cs` and its WebSocket to `Spotify/SoloistSocket.cs`, parts of `SoloistPlayer`,
  unchanged. The Spotify end-to-end test moved from `EndToEndTests.cs` to `SpotifyTests.cs` (815 → 669).

## [1.1.15] — 2026-10-13

### Fixed

- **Renaming a DAC didn't reach Plex.** Caldera takes the DAC's name only as it starts, and a rename
  restarted Spotify's player but not Caldera's, so Plexamp kept the old name. Both are now restarted
  (Caldera on the same port), which stops what they are playing on that DAC; a rename to the name it
  already has restarts nothing (it restarted Spotify before). Gate: the Caldera test renames the DAC
  and waits for Caldera to be set up under the new name on its port (it timed out before), then
  renames it to the same name and checks nothing restarted (it failed with the comparison removed).
- **A DAC switched off or unplugged while a connection was switched on could keep its players.** The
  bridge could be closed between a connection checking it was still there and giving it a Squeezebox
  player, a Soloist or a Caldera: those were never closed, and a Caldera left so played to the DAC
  outside the one-app-at-a-time rule. Closing a bridge now waits for that step and closes whatever it
  made. A Caldera's port is handed on only once it has stopped (it was freed while the old one could
  still hold it), and a Caldera closed while being set up no longer starts its daemon. From reading
  the code (the 1.1.9 audit and its review); the timing can't be made to happen reliably in a test.
- **On a Mac, the Music app's cover showed for every DAC, and stayed after it stopped.** It is now that
  DAC's only, while what the DAC plays is the app's. Not tried on a Mac; no test runs the Mac's apps.
- **The now-playing screen fixed on one DAC (`/now?dac=…`) froze on the last track** once the DAC was
  switched off, and showed "Untitled" once it stopped. It shows the idle screen. Checked by running
  the page's choice of DAC in Node; the page's scripts have no tests yet (`dev/quality.md`).
- **Switching Roon now playing off while a cover was asked for could fail that request.**

### Changed

- **Each DAC's players' lifecycle moved out of `Manager.cs`** into `ManagerPlayers.cs` (a part of
  `Manager`), unchanged, before the fix.

## [1.1.14] — 2026-10-13

### Fixed

- **A controller could be left showing the wrong state, or miss a change for good.** Each UPnP event
  (how Audirvana and Mandarin see a track end or the next one begin) was sent on its own, so a later
  one could overtake an earlier one and leave a controller showing PLAYING after the bridge had
  stopped. And an event that couldn't be delivered was taken as known: its change was never sent
  again. Now a subscriber's events go one at a time, in order, and what an undelivered event carried
  goes again a second later (three times running at most, then with the next change). An answer
  other than success counts as undelivered. Class of error: fire-and-forget delivery of ordered
  state. Gate: `UpnpEventsTests`, with a pretend controller that answers slowly or fails once (both
  failed before the fix). Found by the 1.1.9 audit; not tried with a real Audirvana or Mandarin.

## [1.1.13] — 2026-10-13

### Fixed

- **A DSD file with a broken size hung its track for good.** A DFF chunk (or property) whose size
  reads as negative moved the parser back to where it was, so it read the same bytes for ever: the
  track never started, nothing could stop it, and every stop afterwards waited five seconds. A DSF
  whose data was said to be smaller than its own header got a negative length. Such files are now
  refused, and sizes that would carry the position past what was read are too. Class of error: a
  size from a file trusted to move the position on. Gate: `DsdMalformedTests` (each case failed
  before the fix).
- **A DSD server that stalled held every stop up.** While a DSD track was being opened (its header,
  or skipping to the seek point on a server that ignores ranges) the reads couldn't be cancelled, a
  failed open left its connection open, and closing a response waited up to two seconds to read it
  to its end. Cancelling now closes the connection at once, and a failed open releases it. Gates:
  two stalled pretend servers, one on the header and one on the samples (both failed before).
- **Two seeks at once could leave two feeds writing to the DAC.** Each seek waited for the old feed to
  stop, then started its own, so two seeks arriving together each started one: both wrote to the
  DAC, and the first one's audio stayed queued ahead of the second's. A seek now starts its feed only
  when none is running, halting (and emptying the DAC's buffer of) any that another seek started
  meanwhile. From reading the code (the 1.1.9 audit and its review); the timing it needs couldn't be
  made to happen reliably in a test.

### Changed

- **`Renderer.cs` is under the 500-line limit** (542 → 275): its feed moved to `RendererFeed.cs` (a
  part of `Renderer`) and the track types to `Track.cs`, unchanged.

## [1.1.12] — 2026-10-13

### Fixed

- **Your Plex sign-in could be sent to a server that isn't yours.** To show what Caldera plays, the
  bridge looks the track up on the server Caldera's Plex Companion timeline names, and when the
  timeline carried no token it sent your plex.tv sign-in (an account-wide token). The server named
  comes from whoever told Caldera to play, and any device on the network can. Now:
  - a server gets the token the timeline came with; failing that, your sign-in only if it is one of
    your own servers by plex.tv's own list (its resources, every address, with its scheme: a server
    listed for HTTPS isn't sent the sign-in over plain HTTP); otherwise nothing, and what it plays
    isn't looked up (the log says why, once);
  - what is asked is a path on that very server, checked on the URL actually requested (a track key
    like `@other.host/x` would have moved the request elsewhere), with no redirects followed (a
    redirect carried the sign-in to whatever host it named) and a 15-second limit (a server that
    never answered froze what the page shows);
  - plex.tv's list is kept for the account and asked again hourly; while plex.tv can't be asked, a
    track is looked up again later rather than treated as not yours;
  - covers passed on to the page are pictures only (JPEG, PNG, WebP, GIF; never SVG, which can carry
    script), marked so the browser takes them as nothing else.
  Class of error: a secret sent to an address taken from the network. Gates: end to end, a pretend
  server named with no token is never contacted, with a token of its own gets that, and a hostile
  track key on your own server's address is refused; case by case, the paths, schemes, the owned
  list, plex.tv down, a redirect, the cover types. Each protection removed on purpose failed them.
  Found by the 1.1.9 audit and its multi-agent review; not tried with a real Plex server or Plexamp.

## [1.1.11] — 2026-10-13

### Fixed

- **Another website could still change the bridge, by pointing a name of its own at it (DNS
  rebinding).** 1.1.3 refuses a change that a browser says comes from another site. But a site that
  points its own name at the bridge's address makes its page and the bridge one site in the
  browser's eyes, and the browser says "same-origin": switching connections on, starting downloads
  and turning a DAC up went through again. Now a change from a browser must also be addressed to the
  bridge by an address, `localhost`, or this machine's name (with or without `.local`), or a name in
  the new **`BRIDGE_HOSTNAMES`** (for a name of your own in your DNS). Programs that aren't browsers
  are unaffected. Class of error: trusting what the browser says about the origin without checking
  which name it was given. Gate: the same-origin tests now send a rebound name, end to end and case
  by case (they failed with the check removed on purpose). Found by the 1.1.9 audit.

## [1.1.10] — 2026-10-13

### Fixed

- **Spotify stopped playing after its player restarted, until it was switched off and on.** A new
  Soloist key, **Download Soloist** (the usual first setup: switch Spotify on, then download) and the
  automatic re-download of an expired build all restart a DAC's Spotify player. The new player was
  made before the old one was closed, and closing the old one removed the name the renderer reaches
  Spotify's sound by (`live:spotify/<dac>`), which by then was the new player's: every play failed
  with "isn't playing". For a few seconds the two Soloists also shared one folder and pipe, and a
  restarted Caldera found its port still held by the old one. Now the old player is closed before its
  successor is made, one change at a time. Class of error: two owners of one name in the wrong
  order. Gate: `Spotify_RestartedByANewKey_StillReachesItsSound` restarts the player and opens its
  sound (it failed before the fix, and again with the order reversed on purpose). Found by the 1.1.9
  audit; not tried with a real Soloist.

### Changed

- **`Manager.cs` is under the 500-line limit** (727 → 461 lines): `Bridge` moved to `Bridge.cs` and the
  page's view to `ManagerView.cs` (a part of `Manager`), both unchanged.

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
