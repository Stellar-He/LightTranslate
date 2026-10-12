# Changelog

## 0.1.0-alpha.2

- Run low-level mouse and keyboard hooks on a dedicated message thread so clipboard or UI work does not delay input callbacks.
- Automatically reinstall hooks after missed input, session changes and resume; refresh hooks periodically while the mouse button is released.
- Coalesce drag movement events before forwarding them to the UI dispatcher.
- Add regression checks for a blocked UI dispatcher and automatic recovery after native hooks are removed.
- Write a compact health heartbeat and allow temporary diagnostics to be disabled without restarting the application.

## 0.1.0-alpha.1

- Initial open-source version of the automatic selection translation bubble.
- Local ECDICT lookup, programming term overrides, online translation and in-memory cache.
- No-activation floating window with automatic dismissal and fade transitions.
- Clipboard selection capture, injected-input marker and native password/terminal guards.
- Removal of UIAutomationClient selection handles following a native finalizer crash.
- Reproducible dictionary download, build/package scripts and offline self-test workflow.
