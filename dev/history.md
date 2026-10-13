# Version history

Add a row for every version, and mark which one the owner published. The CHANGELOG tells each
version's story; this is the index.

| Version | Status | Notes |
|---------|--------|-------|
| 1.0.0 | `main`, not published as a release | The bridge (exclusive, bit-perfect DACs over UPnP for Audirvana and Mandarin), the Mac and Docker installers and the site, committed straight to `main`; Squeezebox, Spotify Soloist, share-when-idle, the now-playing screen, Caldera Headless, QobuzProxy and Windows (WASAPI) (#2); the Mac apps route and its driver, the DAC's own volume and Roon Bridge downloaded from Roon Labs, committed straight to `main` after #2; Plex sign-out (#4). The version number wasn't moved during this run of work. |
| 1.1.0 | `main` (#7, merged from its branch straight into `main`), not published as a release | Roon now playing (#5, merged before the version rule; numbered here) and the quality gates (`quality/gates-1`). `dev` gets the same commits with #8. |
| 1.1.1 | `main` (#9), not published as a release | Test images on GitHub's container registry: `<version>-test` per branch, `dev` for the dev branch (`feature/test-image`). |
| 1.1.2 | `main` (#10 into `dev`, then #11), not published as a release | A Squeezebox server's volume no longer moves the DAC on connect (`fix/volume-on-connect`). |
| 1.1.3 | `main` (#11, straight from its branch), not published as a release | Changes sent from another site's page are refused (`security/same-origin`). |
| 1.1.4 | `main` (#15, which kept the number 1.1.3) and `dev` (#12), not published as a release | Settings and secrets files: kept when unreadable, owner-only from the start; Caldera's folders owner-only (`fix/settings-and-secrets`). |
| 1.1.5 | `main` (#17, straight from its branch), not published as a release | `dev` and `main` the same code again, numbered past both (`fix/reconcile-dev-main`). |
| 1.1.6 | `main` (#19, straight from its branch), not published as a release | A Squeezebox server and the DAC share one volume; Lyrion's stored volume never reaches the DAC (`fix/lyrion-follows-dac`). The docs corrections followed it into `main` (#22, docs only). |
| 1.1.7 | `dev` (#21) | Compose files build the bridge on Update and never pull it: Dockge's and Portainer's Update work (`fix/compose-update`). |
| 1.1.8 | `dev` | `dev` and `main` the same code again: 1.1.6 and the docs from `main`, 1.1.7 from `dev` (`fix/dev-gets-main`). |
| 1.1.9 | `dev` | SoX or nothing: every install gets ffmpeg with SoX, the bridge picks it, and without it a track that needs resampling is refused (`fix/sox-everywhere`). |
| 1.1.10 | `dev` | Spotify keeps playing after its player restarts (`fix/spotify-restart`). |
| 1.1.11 | `dev` | Changes from a browser must be addressed to this machine: DNS rebinding refused (`fix/host-check`). |
| 1.1.12 | `dev` | The Plex sign-in goes only to your own servers; covers are pictures only (`fix/plex-token-servers`). |
| 1.1.13 | `dev` | Broken DSD files and stalled DSD servers can't hang a track; one feed per DAC (`fix/renderer-races`). |
| 1.1.14 | `dev` | UPnP events in order, one at a time, and resent when undelivered (`fix/gena-order`). |
| 1.1.15 | `dev` | A rename reaches Plex; closing a DAC closes all its players; the Mac's cover on its own DAC; `/now` idles on a stopped DAC (`fix/manager-lifecycle`). |
| 1.1.16 | `dev` | Soloist's folders and log its owner's only; its key never repeated (`fix/process-hygiene`). |
