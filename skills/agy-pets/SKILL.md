---
name: agy-pets
description: >-
  Open and customize Antigravity Pets (powered by petdex.dev) in the auxiliary
  pane or Windows desktop overlay, and check live model remaining usage.
---

# Antigravity Pets

Use this skill when the user wants to view, customize, or interact with their Antigravity Pet, launch the Windows desktop overlay, or check model usage via the pet.

## Open the Pet Panel

Surface the sidecar link so the user can open the interactive auxiliary pane:

[Open Antigravity Pets](sidecar://agy-pets/pets/)

## What You Can Do in the Panel

- **Interactive Pet Stage**: Watch your pixel-art pet roam, idle, wave, and jump. Click the pet to trigger a wave/jump and display a speech bubble with live **model remaining usage** (`GET /api/usage`) and prompt/flow credits.
- **Customize Pet Behavior**: Adjust **Size** (`48–192 px`), **Speed** (`0.5x–3.0x`), and **Behavior** (`roam`, `idle`, `follow`) — persisted automatically via `POST /api/config`.
- **Browse petdex.dev**: Search by name/creator (`?q=`), filter by kind (`character`, `creature`, `object`), and click any pet to switch immediately.
- **Desktop Overlay**: Click **Desktop Overlay** (`POST /api/overlay/start` / `POST /api/overlay/stop`) to float the pet as an always-on-top transparent character across your Windows screen. Left-click the floating pet to check model usage, drag to reposition, or right-click for the context menu.
