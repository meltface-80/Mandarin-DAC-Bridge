# In-app updater

Status: **draft for the owner's review.** No code depends on it until the open questions at the end
are answered. Asked for by the owner on 2026-10-14.

## What it is for

Today a bridge is updated by running its installer again (Mac, Windows) or by rebuilding its image
(Docker). The owner wants the page to say when a newer release is out and, where it can, to put it
in place with one press.

## What can be updated from inside, and what can't

| Install | How it runs today | In-app update |
|---|---|---|
| Mac, by `tools/mac/install.sh` | `~/Mandarin-DAC-Bridge/mandarin-dac-bridge`, a launchd agent with `KeepAlive` | **yes**: the program is replaced, the bridge exits, launchd starts the new one |
| Windows, by `tools/windows/install.ps1` | `%LOCALAPPDATA%\Mandarin-DAC-Bridge\mandarin-dac-bridge.exe`, a scheduled task | **yes**: the same, the task started again |
| Docker (Compose or `docker run`) | an image the owner built from the source; no image is published | **no**: the program is inside an image the bridge can't rebuild. The page says a release is out and gives the exact commands (open question 3) |
| From source (`dotnet run`), or a copy run by hand | wherever it is | **no**: the page says a release is out, nothing more |

The bridge tells which it is by a file the installers write next to the program (`install.json`:
`{"by":"installer","platform":"osx-arm64"}`). Without it, it is not an install it may replace. A
Docker image is never one (the installers don't run there).

## Design

### Finding a newer release

- From where `https://github.com/<repo>/releases/latest` leads (a redirect to `…/releases/tag/vX.Y.Z`),
  as the installers already do (1.1.26): no GitHub API, no rate limit, no token.
- Newer: `X.Y.Z` above the running `Config.Version` (numbers compared, not text). Pre-releases are
  never offered (GitHub's "latest" is never one).
- Off by default (`CLAUDE.md`: every connection besides UPnP is off by default):
  - **Check for updates**, a button on the page: asks once, when pressed;
  - **Check every day**, a switch under *Connections*, off until the owner turns it on.
- The page shows: "Mandarin DAC Bridge 1.2.0 is out (this is 1.1.28)", the release's notes link,
  and **Update** where the install can be updated.

### Proving the download is the owner's

The release's `SHA256SUMS` (1.1.26) catches a corrupted or mismatched download, but anyone who can
replace a release's files can replace it too. An updater that installs by itself needs more, so each
release is **signed by the owner**:

- An ECDSA P-256 key pair, made once by the owner (steps below). The **public** key is in the program
  (`src/MandarinDacBridge/Update/release-signing.pub`, embedded); the **private** key is a GitHub
  Actions secret that only the release workflow, on a `v*` tag, can read.
- `release.yml` signs `SHA256SUMS` and attaches `SHA256SUMS.sig` beside it.
- The updater: fetches `SHA256SUMS` and `SHA256SUMS.sig` from the release it found, checks the
  signature with the embedded key (`ECDsa.VerifyData`, built into .NET and fine under Native AOT on all
  three systems), then the build's SHA-256 against the signed `SHA256SUMS`. Any failure: nothing is
  installed, and the page says why.
- A release made before signing (no `.sig`) is **never** installed by the updater: the page offers the
  installer line instead. No fallback.
- The installers can check the signature too (a later change; open question 5).

### Putting it in place

Only when the owner presses **Update**, and only while no DAC is playing (otherwise the page asks to
stop first: a restart ends what is playing).

1. Download the build (`Download.ToFile`: size-capped, its SHA-256 logged) into `DATA_DIR/update/`.
2. Check it (above). Unpack the one program file from it.
3. Mac: `xattr -d com.apple.quarantine` and an ad-hoc `codesign`, as the installer does. Windows:
   nothing extra.
4. Keep the running program as `mandarin-dac-bridge.previous` (one, the last) and move the new one
   into its place. Mac: a rename over the running file (allowed). Windows: the running `.exe` is
   renamed aside first (allowed while it runs), then the new one moved in.
5. The bridge says "updating to 1.2.0: back in a few seconds" to the page and logs it, lets go of the
   DACs as on a normal stop, and exits.
6. Started again: Mac by launchd (`KeepAlive`); Windows by a detached
   `schtasks /Run /TN "Mandarin DAC Bridge"` started a few seconds after it exits (the task's own
   restart is only for a failure, and a minute later).
7. The new bridge starts with the same settings (`DATA_DIR` is untouched). The page reloads when it
   answers again.

**Going back:** **Use the previous version** on the page (shown while `.previous` exists) swaps the two
back the same way. And the installer line always puts the latest release in place.

Never touched by the updater: the settings, the third-party programs in `DATA_DIR`, the Mac driver
(`tools/mac/driver`), ffmpeg, the launch agent or the scheduled task themselves.

### Who may press it

`/api/update/check`, `/api/update/install` and `/api/update/previous` are write routes: refused to other
sites (`SameOrigin`) like every change, and behind the password once it exists
(`dev/design/password.md`). They change no DAC's volume or settings.

### Platforms

Mac and Windows as above. Linux has no installer (Docker, or by hand), so it only ever gets the
notice. Said plainly on the page and in the README.

## Tests (when built)

- Finding the release: newer, same, older, a pre-release tag, no release, GitHub out of reach, against a
  pretend GitHub on a local port (as `InstallerTests`).
- The signature: a good one; one by another key; `SHA256SUMS` changed after signing; a `.sig` missing; a
  build not matching the signed sums. Each refused but the first, and each broken once on purpose.
- Putting it in place, in a temporary folder standing for the install: the program swapped, `.previous`
  kept, going back swaps them again; an install without `install.json`, or in Docker, never touched.
- Refused while a DAC plays; refused to another site.
- Not testable here: launchd and the scheduled task starting the new program; a real Mac's Gatekeeper
  with the swapped file. Those are tried on the owner's machines before the first release that has it.

## Open questions for the owner

1. **Install by itself, or only when pressed?** Recommended: only when pressed (a restart ends what is
   playing). A daily check may *say* a release is out, if its switch is on.
2. **Signing:** agree to the signed-release step above (it needs the key steps below), or install on
   `SHA256SUMS` alone (which a replaced release defeats)? Recommended: signed.
3. **Docker:** keep building from the source and show the commands (recommended for now), or publish
   release images to `ghcr.io` so a tool such as Watchtower could update containers? Publishing images
   changes `dev/release.md` ("no release image"), and would want the same signing.
4. **Linux without Docker:** add an installer (a systemd service) so the updater can serve it too, or
   leave Linux to Docker?
5. **The installers:** should they check `SHA256SUMS.sig` as well, once releases carry it?

## Setting it up (the owner's steps)

Done once, in this order. Nothing here is needed before the questions are answered.

1. **Make the signing key**, on your own computer (not on a server), with OpenSSL:
   ```bash
   openssl ecparam -name prime256v1 -genkey -noout -out release-signing.pem
   openssl ec -in release-signing.pem -pubout -out release-signing.pub
   ```
   `release-signing.pem` is the private key: keep it offline (a password manager or an encrypted USB
   stick), never in the repository. `release-signing.pub` is public.
2. **Give the release workflow the private key**, and only it: GitHub → the repository → *Settings* →
   *Environments* → **New environment** `release` → *Deployment branches and tags*: **Selected** → add
   the tag rule `v*` → *Environment secrets* → **Add secret** `RELEASE_SIGNING_KEY`, pasting the whole
   of `release-signing.pem` (from `-----BEGIN` to `END-----`). Optionally add yourself under *Required
   reviewers*, so each release waits for your approval before it is signed.
3. **Send the public key**: paste the contents of `release-signing.pub` in the pull request (it is
   public). It goes into the program as `Update/release-signing.pub`.
4. **Answer the open questions** in the pull request.
5. After the code is merged and in `main`: **publish a release as usual** (*Releases* → *Draft a new
   release* → tag `vX.Y.Z`). Check that it has `SHA256SUMS.sig` beside the six builds.
6. **Try it** on your Mac and Windows machines before telling anyone: from the release before, press
   *Check for updates*, then *Update*, then *Use the previous version*.

If the private key is ever lost or exposed: make a new pair (step 1), replace the secret (step 2) and
the public key (step 3), and release; installs older than that release then update with the installer
line, once.
