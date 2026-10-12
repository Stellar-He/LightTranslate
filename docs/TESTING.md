# Testing

## Automated checks

```powershell
pwsh -File .\build.ps1
pwsh -File .\scripts\download-dictionary.ps1
pwsh -File .\scripts\test.ps1
pwsh -File .\scripts\test.ps1 -Online
```

The default test does not send network requests. It creates its own password/plain
controls and renders a bubble without showing it or injecting desktop input.
The online test sends three fixed example sentences, not the user's selection.
Results are written under `dist/LightTranslate` and are excluded from Git.

## Manual checks

Record Windows version, app version and the selected application's process name.

1. Drag-select a word, double-click a word, and select with Shift + arrow keys.
2. Select a sentence while online, and query a dictionary word while offline.
3. Check that typing, scrolling outside the bubble, clicking elsewhere and switching
   applications dismiss the bubble without changing the application's focus.
4. Hover and scroll a long translation inside the bubble.
5. Before a selection, copy plain text, HTML, an image and file-drop content; verify
   the original content is still available afterward.
6. Copy something else while a translation is pending; verify it is not overwritten.
7. Quickly make several selections; verify an older response does not replace the
   current selection's result.
8. Test terminal and password fields, user-configured excluded applications, DPI
   changes, multiple monitors and monitor edges.
9. Leave the application running for several hours and verify after restarting Windows.

## Current evidence

The installed prototype was manually confirmed to show translations in the user's
Codex desktop environment. Earlier clipboard capture was also confirmed in Chrome.
The published source includes the fix removing native UIAutomationClient handles
after an observed access violation in UIAutomationCore's finalizer.

Claude desktop compatibility, restart behavior, extended uptime and a full
multi-monitor matrix remain unverified. CI checks do not prove those behaviors.
