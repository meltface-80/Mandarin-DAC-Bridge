# Lessons — the rules that cost a fix to learn

Read on demand from `CLAUDE.md`: before diagnosing an "it broke in version N" report, before
touching audio timing, process supervision, the Mac driver or CI on Windows/macOS, and before a
non-trivial review. Each rule here was paid for by something that went wrong. None of them can be
checked by a tool, which is why they are prose; anything a tool CAN check belongs in a gate
(`dev/quality.md`), not here.

## Development rules

- **No incomplete implementations.** Write the full code. Never leave `// rest stays the same`.
- **No silent catch.** A `catch` that stays silent has a comment saying truthfully why silence is
  safe.
- **Edit through a branch, never on GitHub's web editor.** Two commits pasted onto `main` in the
  web editor replaced `QobuzConnect.cs` and `EndToEndTests.cs` with the pasted snippet alone, and
  `main` stopped compiling (2026-10-10).
- **A pass can be a skip.** Several tests return early when a tool is missing (ffmpeg, PulseAudio,
  Python, ALSA's config). Before trusting a green run for something new, check the test really ran
  (its duration, or the bridge's log lines in the test output).
- **Stopping a program means stopping what it started.** Roon Bridge's `start.sh` cleans up only
  when it ends by itself: a SIGTERM ends the script and leaves Roon Bridge and RAATServer running.
  The bridge stops the whole tree (`Libc.Descendants`), and counts a zombie as gone. A manual check
  with `timeout` hid this, because `timeout` signals the whole process group.
- **Never `pkill -f` in a script or a session:** the pattern matches the shell running it.
- **Native AOT has no reflection for JSON.** `Dictionary<string, object>` and `object` values don't
  serialize; build bodies with `JsonObject`, or add the type to the source-generated context.
- **Numbers and comparisons are culture-free.** A machine with "," decimals broke parsing once:
  `CultureInfo.InvariantCulture` and `StringComparison.Ordinal` (now enforced by the analyzers).
- **Volume:** a headphone DAC (the DragonFly) has no knob, so "fixed at 100%" means full volume in
  someone's ears. The DAC's own volume is used; the bridge never sets a level by itself. Volume and
  mute from an app are refused while another app has the DAC, but never claim it (a volume nudge
  must not lock the other app out for the grace period).
- **Files that hold secrets go through `OwnerOnlyFile`** (`settings.json`, `roon-api.json`): created
  owner-only (never written and then restricted), written whole through a new file, and a file that
  can't be read is moved aside and logged, never replaced by the defaults. A corrupt `settings.json`
  once became the defaults without a word and was overwritten on the next change (1.1.4).
- **Identities are forever.** Changing how DAC ids or UPnP UDNs are derived renames every DAC: its
  settings are lost and every app forgets it.

## Platform lessons

- **Windows builds keep the folder separator in embedded resource names** (`Page\index.html`), so
  the page answered 404 on Windows until the lookup tried both.
- **PowerShell reads `"$f:"` as a scope-qualified variable.** Write `"${f}:"`.
- **macOS runners stall on reverse DNS.** Python's `http.server.HTTPServer` looks up its own address
  before serving; on the macOS runner that took longer than the test waited. Stand-ins use
  `socketserver.TCPServer`.
- **A loopback on a slow machine skips.** On the macOS runner the Mac route's loopback came back
  with a stretch from elsewhere in the stream at 96 and 192 kHz with a 512-frame buffer: the
  machine missed audio deadlines and macOS moved the device's clock on. With a 4096-frame buffer it
  never skipped, and the bridge reads with that. `--loopback-test` reports clock skips, so a late
  machine is told from a changed sound.
- **Spotify Soloist** takes its API key only on the command line (visible in the process list), needs
  glibc 2.38 or later and `libatomic1`, and runs one private PulseAudio per DAC.
- **QobuzProxy offers hi-res only when the renderer says so:** FLAC with `sampleRate` and
  `bitsPerSample` in the protocol info.

## When a bug is found

1. Identify the root cause (not the symptom).
2. Confirm the root cause explains ALL reported failures.
3. Fix the root cause, not the symptom.
4. Add the gate (test, analyzer rule) that would have caught it — `dev/quality.md`.
5. Document what class of error it was in the CHANGELOG.

A cause that a measurement contradicts is not the cause. The Mac loopback "clicks" looked like a
driver bug until the test recorded the device clock's skips and every bad stretch began at one.

## Code review workflow (multi-agent)

For any non-trivial change, run a full code review using parallel agents before committing
(`/code-review --effort high` automates this). It must be run on any change that touches:

- the audio path (sinks, renderer, DSD, sources);
- the arbiter, or anything that claims or lets go of a DAC;
- process supervision (Soloist, Caldera, Roon Bridge, QobuzProxy) and downloads;
- secrets, settings persistence, or a DAC's volume;
- the Mac driver.

### Review angles (run in parallel)

| Angle | What it hunts |
|-------|---------------|
| A — line-by-line diff scan | Inverted conditions, null dereference, a missing await, the wrong variable |
| B — removed-behaviour auditor | Dropped guards, deleted error paths, narrowed validation |
| C — cross-file tracer | Broken call sites, mismatched shapes between the page, the API and the bridge |
| D — reuse | Code that re-implements an existing helper |
| E — simplification | Redundant state, dead code, unnecessary nesting |
| F — efficiency | Blocking work on the audio thread, polling where an event exists |
| G — altitude | A fix layered on shared infrastructure instead of at the cause |
| H — `CLAUDE.md` and `dev/*.md` | Quote the exact rule and the exact line that breaks it |

### Verify findings

For each surviving candidate, one verifier: **CONFIRMED / PLAUSIBLE / REFUTED**. PLAUSIBLE by
default; REFUTED only when the code provably makes it unreachable. Keep CONFIRMED and PLAUSIBLE.

### Fix all confirmed findings before committing

Do not commit with known CONFIRMED or PLAUSIBLE bugs. Fix them in the same version.
