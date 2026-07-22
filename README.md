# SqueezeParseMini

A lightweight, highly customizable damage/healing meter overlay plugin for [Advanced Combat Tracker (ACT)](https://advancedcombattracker.com/), built for **EverQuest2**.

SqueezeParseMini renders borderless, always-on-top bar charts directly on your screen — true per-pixel transparency, no bulky windows, no wasted space. Run as many independent parse windows as you want, each with its own metric, layout, and color scheme.

## Features

- **Multiple independent parse windows** — run a DPS meter and an HPS meter side by side, or as many as you like, each configured separately.
- **Wide range of metrics** — Encounter DPS/HPS, raw damage/healing totals, personal per-ability breakdowns, zone-wide (cross-encounter) totals, and combined Hybrid DPS+HPS+Cures views.
- **Landscape or portrait layout** — stack bars top-to-bottom, or arrange them left-to-right as columns.
- **Deep visual customization**
  - Single-color or multi-color palette bar coloring, including several colorblind-safe palettes (IBM, Wong, Tol)
  - Configurable bar background, text color, borders, and gradient fill
  - Adjustable bar height, spacing, gap, and overlay width
  - Optional percent-of-parse display
- **Self-highlighting** — make your own row instantly easy to spot with a dedicated bar color, text color, and/or highlight border.
- **Configurable title bar** — stack multiple info lines (time/encounter name, damage/DPS, healing/HPS/cures, highest hit, highest heal) in any order.
- **Auto-fade when idle** — overlays fade out automatically after a period of no combat, so they don't clutter your screen between pulls.
- **Drag-to-position with alignment grid** — unlock all windows to reposition them freely, with an on-screen grid overlay to help line everything up.
- **Per-window settings persistence** — every window's full configuration and screen position is saved to disk and restored on the next ACT session.
- **Built-in update checker** — checks a remote version file on startup and lets you download the latest release directly from the plugin's settings tab.

## Installation

1. Download `SqueezeParseMini.cs`.
2. Move the plugin to **C:\Users\USERNAME\AppData\Roaming\Advanced Combat Tracker\Plugins\SqueezeParseMini.cs**.
2. In ACT, go to **Plugins → Plugin Listing**, click **Browse**, select the file, and click **Add/Enable Plugin**.
3. Check the box to enable it. A new **SqueezeParseMini** tab will appear.

## Usage

- On first load, one default parse window is created automatically.
- Use **Add parse window** to create additional independent overlays.
- Each parse window has its own settings tab (rename it via the tab name field) covering the window itself, data metric, title bar, bar appearance, colors, self-highlighting, and auto-fade.
- Check **Unlock all windows** to drag overlays into position — an alignment grid appears while unlocked to help you line them up.
- Click **Save settings** to persist your configuration, or just close ACT — settings are saved automatically on shutdown.

## Updating

The plugin checks for a newer version automatically on load. If one is available, the **Check for updates** button area will show a **Download vX.X.X** option. Set your plugin's file path under **Plugin file path**, click download, then disable and re-enable the plugin in ACT's Plugin Listing tab to load the new version (a running plugin can't hot-reload its own compiled code).

## Notes

- Overlays use a true per-pixel-alpha layered window, not `TransparencyKey`, so they composite cleanly over any background including other overlays.
- When locked, overlay windows are click-through and won't steal mouse focus from the game.
