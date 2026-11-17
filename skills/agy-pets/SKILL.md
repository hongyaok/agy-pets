---
name: agy-pets
description: >-
  Open and customize Antigravity Pets (powered by petdex.dev) in the auxiliary
  pane or desktop overlay (Windows and macOS), and check live model remaining usage.
---

# Antigravity Pets

Open the auxiliary pane when the user asks to see, switch, or configure their pet, start the desktop overlay, or check model quota:

[Open Antigravity Pets](sidecar://agy-pets/pets/)

## Panel Controls

- **Stage & quota (`GET /api/usage`)**: Click the pet (or **Usage**) to inspect remaining quota per model and reset times. **Wave** and **Jump** trigger animations on demand.
- **Behavior (`POST /api/config`)**: Set movement (`roam`, `idle`, `follow`), size (`48–192 px`), and speed (`0.5×–3.0×`). Changes save to `sidecars/pets/cache/config.json`.
- **Catalog (`GET /api/pets`)**: Search the petdex.dev catalog by name or creator and filter by kind (`creature`, `character`, `object`). Selecting a tile swaps the active sprite (`GET /api/sprite`).
- **Desktop overlay (`POST /api/overlay/start`, `/api/overlay/stop`)**: Launches a transparent always-on-top sprite on Windows (`AgyPetsOverlay.exe`) and macOS (`overlay-mac.jxa.js` via Cocoa/WebKit). Left-click the desktop pet for quota, drag to move, or right-click for the menu.

