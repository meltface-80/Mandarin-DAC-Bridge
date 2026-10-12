# Tests

Read before writing or moving tests (`CLAUDE.md`). The rules for writing them are in
`dev/quality.md`.

## Running

```bash
tools/check.sh                                   # every gate, then the whole suite (what CI runs)
dotnet test -c Release                           # the suite alone
dotnet test -c Release --filter "FullyQualifiedName~Roon"   # one area
```

CI (`.github/workflows/checks.yml`) runs the suite on macOS, Windows and Linux on every pull
request, and on every branch push before the test image is built (`test-image.yml`); the test image
and the release both need it green.

## What the suite needs

- **ffmpeg** on the PATH, for the end-to-end tests (they decode real FLAC, DSD and WAV).
- On Linux, **ALSA's library** (`libasound2`), and **PulseAudio** for the Spotify Soloist test.
- **Python 3**, for the stand-in servers some tests run.

**A missing tool makes its tests return early, and they then pass.** Before trusting a green run
for something new, check the test really ran: its duration, or the bridge's log lines in the test
output (`dotnet test --logger "console;verbosity=detailed"`).

## Layout

- `EndToEndTests.cs` — the whole bridge in-process against stand-ins (an HTTP file server, SOAP from
  two controllers, a pretend LMS, Soloist, Plex server, Caldera, QobuzProxy, Roon Core, Roon
  Bridge), with a DAC played in real time by the clock sink or, on Linux, ALSA's null device. Being
  split into one file per area (`dev/quality.md`): it may only shrink.
- `UnitTests.cs` — the decision logic on its own. Being split by area the same way.
- New tests go in a file for their area: `<Area>Tests.cs` (for example `RoonTests.cs`), never into
  the two files above.

## Stand-ins are not evidence

Every pretend service follows what its publisher documents (Roon's extension API, Soloist's
WebSocket reference, SlimProto) and is not proof of how the real one behaves. A change says what it
was and wasn't tried against: a real DAC, a real Roon Core, the real Spotify, Plex or Qobuz.
