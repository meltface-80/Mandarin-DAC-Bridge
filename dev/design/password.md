# Optional password

Status: **draft for the owner's review.** No code depends on it until the open questions at the end are
answered. Found by the 2026-10-13 audit (S6); planned in `dev/quality.md` (Security, *Login*).

## The problem

Anyone who can reach port 55500 can change the bridge. `SameOrigin.cs` stops *other websites* from
doing it through a visitor's browser, but a program on the network (a script, `curl`, anything on a
guest device) sends no browser headers and is let through. It can:

- switch connections on and off, and start downloads of third-party programs (`POST /api/services`,
  `/api/services/soloist`, `/caldera`, `/roon`, `/qobuz`);
- sign the bridge out of Plex, release a DAC, rename or disable it (`/api/services/plex/signout`,
  `/api/dacs/<id>/release`, `/api/dacs/<id>/settings`);
- set a DAC's volume with `{"level":100}`. This bypasses the arbiter, so it works even while
  another app is playing: a headphone amplifier can go to full volume mid-track;
- read what the page shows: the Plex account, the Qobuz email, the hostname, what is playing.

## What a password can and can't close

It closes the page's API (`/api/...`). It can't close the ways in that the apps themselves use,
because their protocols have no password:

| Way in | Who uses it | Guarded? |
|---|---|---|
| `/api/...` (the page) | the owner, in a browser | **yes** |
| `/now` and `/api/dacs/<id>/art` | the now-playing screen beside the DAC | open question 3 |
| UPnP control and events (`/upnp/...`) | Audirvana, Mandarin, BubbleUPnP, Roon's UPnP | no: no UPnP controller can send one |
| Squeezebox (Slim), Spotify Connect, Qobuz Connect, Roon | those servers and apps | no: they talk to their own programs |

So with a password on, a UPnP controller on the network can still play to an idle DAC, and set its
volume while it holds it (`SetVolume` through the arbiter, as today). That is how UPnP works, and it
is why the README will say "keep port 55500 on your home network" even with a password.

## Design

### Off by default

No password means today's behaviour, unchanged: existing installs keep working (`CLAUDE.md`: every
connection besides UPnP is off by default; a password that locked existing users out on update would
break that spirit). The page offers to set one.

### Setting, changing and removing it

- On the page, under *Connections*, a **Password** section: set, change (the old one is asked for),
  remove (the current one is asked for).
- Minimum length 8; no other composition rules. At most 128 characters.
- It is never sent back to the page, never logged, never in `settings.json`.

### Storage

- `DATA_DIR/password.json`, owner-only (`OwnerOnlyFile`, like `roon-api.json`). One object:
  `{"pbkdf2-sha256": {"iterations": 600000, "salt": "<base64, 16 bytes>", "hash": "<base64, 32 bytes>"}}`.
- PBKDF2-HMAC-SHA256 from .NET (`Rfc2898DeriveBytes.Pbkdf2`), which works under Native AOT on all
  three platforms; the hash compared with `CryptographicOperations.FixedTimeEquals`.
- A file that isn't what it should be is kept aside (as `settings.json` is) and the password counts as
  **set but unknown**: every guarded request is refused until the owner removes the file. A corrupt
  file must never turn the password off.

### Signing in

- `POST /api/login` with `{"password": "..."}` (the one guarded route reachable without a session).
- On success: a session id, 32 random bytes (`RandomNumberGenerator`), sent as cookie
  `dacbridge_session`, `HttpOnly; SameSite=Strict; Path=/`. Not `Secure`: the page is plain HTTP on
  the home network (see *Not covered*).
- Sessions are kept server-side by the SHA-256 of the id (open question 4: in memory or on disk).
- A session lasts 30 days from its last use. `POST /api/logout` ends it. Changing or removing the
  password ends every session.
- Failed attempts: per source address, after 5 failures each further attempt waits 1 s, doubling to
  at most 60 s; a success resets it. Logged as `refused sign-in from <ip>` (never the password).

### Guarding

- One check in `Web.Handle`, after `SameOrigin.Refusal` and before any `/api/` route: no valid
  session → `401` with `{"error":"sign in"}`. The page shows its sign-in screen on a 401.
- The list of what is guarded is in one place, with a test that every `/api/` route is either
  guarded or listed as open on purpose (`/api/login`, `/api/health`), so a new route can't be added
  unguarded by accident.
- UPnP, Slim and the page's static files (HTML, fonts, scripts) are not guarded: the page has to
  load to show its sign-in screen.

### Forgotten password

Delete `password.json` from the data folder (in Docker: the `dac-bridge-data` volume, e.g.
`docker run --rm -v dac-bridge-data:/data alpine rm /data/password.json`) and restart. The README
says how for each install.

### Platforms

The same on macOS, Windows and Linux: nothing platform-specific (PBKDF2, cookies, a file).

## Tests (when built)

- A password set: each guarded route without a session → 401; with one → as today.
- The route list test above, broken once by adding an unguarded route.
- Sign-in: wrong password refused and counted; the delay applies per address; right one resets it.
- Sessions: expiry, logout, all ended on change/removal; the cookie's attributes.
- Storage: owner-only; the hash, not the password, on disk; a corrupt file refuses everything.
- No password: every existing test passes unchanged (off by default really is today's behaviour).

## Not covered

- **Plain HTTP.** The password and the cookie cross the network in clear. Anyone who can watch the
  home network's traffic can take them. TLS (a certificate the bridge makes, which browsers then
  warn about) is a separate decision.
- **UPnP and the apps' protocols**, as in the table above.
- **A user account system.** One password, for whoever owns the bridge.

## Open questions for the owner

1. **How is it set?** On the page only (as above), or also by an environment variable
   (`BRIDGE_PASSWORD`) for Docker? An environment variable is visible to anyone who can run
   `docker inspect` and sits in compose files; the page avoids that but needs a first visit.
2. **Reads too?** With a password on, should reading `/api/dacs` and `/api/services` also need a
   session (they show the Plex account, the Qobuz email and what is playing), or only changes?
3. **The now-playing screen** (`/now`, and the covers it shows): open, so a display beside the DAC
   needs no sign-in, or guarded like the rest?
4. **Sessions across restarts:** kept in memory (a restart or an update signs everyone out), or kept
   in an owner-only file (they survive, at the cost of one more secret on disk)?
5. **Scripts:** should a program be able to use the API with the password (e.g. an
   `Authorization: Bearer` header holding it), or is the API for the page only?
