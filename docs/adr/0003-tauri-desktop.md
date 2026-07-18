# ADR 0003 — Tauri 2 + React for the technician desktop

- Status: Accepted (Phase 0)
- Date: 2026-07-18

## Context

Technicians need a cross-platform (Windows/macOS) desktop app to add devices,
capture messages, configure ports, map fields, replay fixtures, run validation,
and export diagnostic bundles. The UI must NOT be the communication process — the
gateway daemon runs independently.

## Decision

Build the desktop app with Tauri 2 + React + TypeScript + Vite. The app is a UI
only; it talks to the local authenticated gateway API / Tauri IPC. `src-tauri` is
a separate Cargo project with a minimal capability set (`core:default` only in
Phase 1 — no filesystem/shell/network plugins until needed).

## Consequences

- Small binaries, native webview, shared React/TS skills with the web app.
- Rust available for any native desktop needs; capabilities are allowlisted.
- Requires WebView2 on Windows and system WebKit on macOS; desktop Rust build is
  compile-checked in CI on both OSes.

## Alternatives considered

- **Electron**: larger footprint, heavier update surface — rejected.
- **Native per-OS (WinUI / SwiftUI)**: duplicated effort, slower — rejected.
