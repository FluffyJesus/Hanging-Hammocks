# Hanging Hammocks

A Sailwind mod that adds a buyable hammock you can hang from any ceiling — your cabin, the hold, under the deck. It swings with the waves, its height is adjustable, and you can sleep in it.

## Features

- **New furniture item** — a hammock sold at furniture shops.
- **Rolled up for transport** — in your hands and on the floor it is a compact bundle, easy to carry and store in the hold.
- **Hang it anywhere** — unroll it under a ceiling and let go. Ropes stretch to the ceiling at an angle, like a real taut hammock.
- **Adjustable height** — scroll the mouse wheel while looking at it.
- **Swings with the boat** — reacts to rolling, pitching and acceleration, like the lanterns do.
- **Sleep in it** — works like any vanilla bed, including timeskip sleep when moored.
- **Saved with your game** — hung hammocks keep their position and height after reloading.
- **No collision by default** — a hung hammock doesn't block your way in tight holds (configurable).

## Requirements

- Sailwind
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64)

## Installation

1. Install BepInEx 5 into your Sailwind folder and run the game once.
2. Download latest version of Hanging Hammocks.
3. Put it into `Sailwind/BepInEx/plugins/`.

## Where to buy

Furniture shops in:

- Gold Rock City
- Fort Aestrin
- Dragon Cliffs

## How to use

| Action | How |
|---|---|
| Unroll / roll up | Hold the bundle and press **Y** |
| Hang | Unroll it, aim under a ceiling (rope preview appears), release |
| Drop the bundle | Keep it rolled up and release |
| Change height | Look at the hung hammock, scroll the **mouse wheel** (or **PageUp / PageDown**) |
| Take down | Look at the hung hammock and **hold Y** — the bundle goes straight to your hands |
| Sleep | Use the hung hammock like a bed |

You can also take a hammock down with a **hammer**, like any nailed furniture.
You can only sleep in a hammock that is hung, not in the rolled bundle.

## Configuration

The config file is created after the first launch:
`BepInEx/config/<GUID>.cfg`

| Setting | Default | Description |
|---|---|---|
| `ModeKey` | `Y` | Roll/unroll in hands; hold on a hung hammock to take it down |
| `UseMouseWheel` | `true` | Mouse wheel adjusts height |
| `RaiseKey` / `LowerKey` | `PageUp` / `PageDown` | Height adjustment keys |
| `RopeAngle` | `30` | Rope angle from vertical, degrees |
| `EnableSwing` | `true` | Hammock swings with the boat |
| `FreezeSwingInBed` | `false` | Stop swinging while you are lying in it |
| `SolidWhenHung` | `false` | `true` gives the hung hammock a collider |
| `SellInShops` | `true` | Sell hammocks at furniture shops |
| `PrefabIndex` | `900` | Internal item index. Change only if another item mod uses the same index, and **never** after hammocks exist in your save |

## Uninstalling

Hammocks are stored in your save as a modded item. Before removing the mod, **sell or get rid of all your hammocks**, otherwise the save may fail to load them correctly.

## Compatibility

- Uses its own item index (`900`). If another mod adds items at the same index, the log will show an error — change `PrefabIndex` in the config **before** buying any hammocks.
- Does not modify vanilla items or ships. The hammock model is cloned from the vanilla ship hammock at runtime.

## Credits

- Thanks to the [Sailwind modding notes](https://github.com/ai-pop/sailwind-modding-notes)

## License

MIT
