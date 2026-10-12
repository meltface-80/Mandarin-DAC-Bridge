# Version history

Add a row for every version, and mark which one the owner published. The CHANGELOG tells each
version's story; this is the index.

| Version | Status | Notes |
|---------|--------|-------|
| 1.0.0 | `main`, not published as a release | The bridge (exclusive, bit-perfect DACs over UPnP for Audirvana and Mandarin), the Mac and Docker installers and the site; then Squeezebox, Spotify Soloist, share-when-idle and the now-playing screen (#2); Caldera Headless, QobuzProxy, Windows (WASAPI), the Mac apps route and its driver, the DAC's own volume, Roon Bridge downloaded from Roon Labs, Plex sign-out (#4). The version number wasn't moved during this run of work. |
| 1.1.0 | `main` (#7, merged from its branch straight into `main`), not published as a release | Roon now playing (#5, merged before the version rule; numbered here) and the quality gates (`quality/gates-1`). `dev` gets the same commits with #8. |
| 1.1.1 | `dev` | Test images on GitHub's container registry: `<version>-test` per branch, `dev` for the dev branch (`feature/test-image`). |
| 1.1.3 | `dev` | Changes sent from another site's page are refused (`security/same-origin`). |
