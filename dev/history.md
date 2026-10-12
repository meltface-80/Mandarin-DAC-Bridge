# Version history

Add a row for every version, and mark which one the owner published. The CHANGELOG tells each
version's story; this is the index.

| Version | Status | Notes |
|---------|--------|-------|
| 1.0.0 | `main`, not published as a release | The bridge (exclusive, bit-perfect DACs over UPnP for Audirvana and Mandarin), the Mac and Docker installers and the site, committed straight to `main`; Squeezebox, Spotify Soloist, share-when-idle, the now-playing screen, Caldera Headless, QobuzProxy and Windows (WASAPI) (#2); the Mac apps route and its driver, the DAC's own volume and Roon Bridge downloaded from Roon Labs, committed straight to `main` after #2; Plex sign-out (#4). The version number wasn't moved during this run of work. |
| 1.1.0 | `main` (#7, merged from its branch straight into `main`), not published as a release | Roon now playing (#5, merged before the version rule; numbered here) and the quality gates (`quality/gates-1`). `dev` gets the same commits with #8. |
| 1.1.1 | `main` (#9), not published as a release | Test images on GitHub's container registry: `<version>-test` per branch, `dev` for the dev branch (`feature/test-image`). |
| 1.1.2 | `dev` | A Squeezebox server's volume no longer moves the DAC on connect (`fix/volume-on-connect`). |
| 1.1.4 | `dev` | Settings and secrets files: kept when unreadable, owner-only from the start; Caldera's folder owner-only (`fix/settings-and-secrets`). |
