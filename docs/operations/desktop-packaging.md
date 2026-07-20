# Technician desktop — packaging & installers

The technician app (`apps/gateway-desktop`) is a Tauri 2 shell over a React UI.
It is a **read-only status/enrollment UI**, not the communication process —
`gatewayd` remains the on-site daemon that captures, queues, and talks to the
cloud. This runbook covers building **development** installers and the
**code-signing** step, which is a human gate (it needs your signing identities).

## Prerequisites

- The pinned Rust toolchain (`rust-toolchain.toml`) and Node/pnpm.
- Platform bundler dependencies:
  - **macOS**: Xcode command-line tools (for `.app`/`.dmg`).
  - **Windows**: the MSVC build tools; WiX is fetched by Tauri for `.msi`.
  - **Linux**: `libwebkit2gtk-4.1-dev` and friends for `.deb`/AppImage.
- Bundling is **per-OS**: you can only build a platform's installer on that
  platform (macOS installers on macOS, Windows on Windows).

## Build a development installer

```bash
# From the repo root. Builds the frontend, compiles src-tauri in release, and
# bundles the OS installer(s) into apps/gateway-desktop/src-tauri/target/release/bundle/.
pnpm --filter @lab-connect/gateway-desktop tauri build

# Faster iterations — pick specific targets:
pnpm --filter @lab-connect/gateway-desktop tauri build --bundles app,dmg   # macOS
pnpm --filter @lab-connect/gateway-desktop tauri build --bundles msi       # Windows
```

Artifacts land under `apps/gateway-desktop/src-tauri/target/release/bundle/`:
- macOS → `macos/lab-connect-technician.app`, `dmg/lab-connect-technician_<ver>_<arch>.dmg`
- Windows → `msi/lab-connect-technician_<ver>_<arch>_en-US.msi`
- Linux → `deb/…​.deb`, `appimage/…​.AppImage`

These are **unsigned development builds**. Distribute them only for internal
testing — on macOS Gatekeeper will warn, and on Windows SmartScreen will warn,
until the artifacts are signed (below).

## Code signing & notarization — HUMAN GATE

Production installers must be signed with **your** identities; this cannot be
done without your certificates and is never automated with secrets in the repo.

- **macOS**: an Apple **Developer ID Application** certificate + an
  app-specific password / notarytool credentials for notarization. Tauri reads
  `APPLE_CERTIFICATE`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`,
  `APPLE_ID`, `APPLE_PASSWORD`, `APPLE_TEAM_ID` from the environment.
- **Windows**: an **Authenticode** code-signing certificate (OV/EV). Configure
  `bundle.windows.certificateThumbprint` (or a signing command) in
  `tauri.conf.json`, with the cert available to the signer.

When you are ready to sign, provide these as CI secrets (never in the repo) and
the release job can enable signing. Until then, the release publishes the
**edge binaries** (`gatewayd`, `lab-simulator`) with SBOM + provenance, and the
desktop installers remain unsigned dev builds.

## CI

`.github/workflows/release.yml` builds and attaches the edge binaries, SBOM,
checksums, and provenance on a `v*` tag. A desktop-installer matrix job (macOS +
Windows) can be added to attach **unsigned** dev installers; wiring the signing
secrets above upgrades those to signed, notarized artifacts — that step waits on
your identities.

## What the app talks to

- The desktop app connects **only** to `gatewayd`'s loopback API for status and
  the enrollment-setup guidance (it never holds the device credential).
- CSP is locked to `self` + `http://127.0.0.1:*` (see `tauri.conf.json`), so the
  UI cannot reach arbitrary hosts.
