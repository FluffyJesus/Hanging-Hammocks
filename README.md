# Hanging Hammocks

A Sailwind mod that adds a buyable hammock you can hang from any ceiling — your cabin, the hold, under the deck. It swings with the waves, its height is adjustable, and you can sleep in it.

## Features

- **New furniture item**: a hammock sold at furniture shops.
- **Rolled up for transport**: in your hands and on the floor it is a compact bundle, easy to carry and store in the hold.
- **Hang it under a ceiling**: unroll it under a ceiling and let go. Ropes stretch up at an angle, like a real taut hammock.
- **Hang it from surfaces around you**: with no ceiling straight over it, each rope finds its own hook nearby (a beam, a wall, a tree, a wood pole) up to `MaxRope` away. The hammock turns to lie along the line between its hooks, and both ropes keep the same angle where the surfaces allow it.
- **Hang it from wood poles**: with [Tent and Plank](https://github.com/DogEggz01/Tentandplank) installed, under a level pole or between two upright ones (see [Wood poles](#wood-poles-tent-and-plank)).
- **Safe ropes**: a rope is never flatter than 30° and never leans more than `RopeSideAngle` to the side, so the knot can't flip over the hammock. This is checked both when you hang it and when you change its height.
- **Built-in iron hooks**: hooks turn toward the rope's pull, the loop sits on the bend.
- **Adjustable height**: scroll the mouse wheel while looking at it.
- **Swings with the boat**: reacts to rolling, pitching and acceleration, like the lanterns do.
- **Sleep in it**: works like any vanilla bed, including timeskip sleep when moored.
- **Saved with your game**: hung hammocks keep their position, hooks and height after reloading.
- **No collision by default**: a hung hammock doesn't block your way in tight holds (configurable).

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
`BepInEx/config/fluffyjesus.sailwind.hanginghammock.cfg`

| Setting | Default | Description |
|---|---|---|
| `ModeKey` | `Y` | Roll/unroll in hands; hold on a hung hammock to take it down |
| `UseMouseWheel` | `true` | Mouse wheel adjusts height |
| `RaiseKey` / `LowerKey` | `PageUp` / `PageDown` | Height adjustment keys |
| `RopeAngle` | `30` | Rope angle from vertical under a ceiling, degrees |
| `MaxRope` | `2` | Longest rope to a hook on a surface around the hammock, metres |
| `RopeSideAngle` | `20` | Most a rope may lean sideways out of the hammock's line, degrees |
| `EnableSwing` | `true` | Hammock swings with the boat |
| `FreezeSwingInBed` | `false` | Stop swinging while you are lying in it |
| `SolidWhenHung` | `false` | `true` gives the hung hammock a collider |
| `ShowHooks` | `true` | Show iron hooks where the ropes attach |
| `DetailedRopeTexture` | `false` | `true` uses a detailed twisted fibre rope texture instead of the vanilla style |
| `SellInShops` | `true` | Sell hammocks at furniture shops |
| `PrefabIndex` | `900` | Internal item index. Change only if another item mod uses the same index, and **never** after hammocks exist in your save |

## Uninstalling

Hammocks are stored in your save as a modded item. Before removing the mod, **sell or get rid of all your hammocks**, otherwise the save may fail to load them correctly.

If you remove Tent and Plank, hammocks hung from its poles are taken down the next time you load.

## Compatibility

- Includes Harmony patch that makes Virtual Crew NPCs swing around in a hammock.
- [Tent and Plank](https://github.com/DogEggz01/Tentandplank) and Economy Overhaul: hammocks hang from their wood poles (see above). Detected automatically, no hard dependency.
- Uses its own item index (`900`). If another mod adds items at the same index, the log will show an error — change `PrefabIndex` in the config **before** buying any hammocks.
- Does not modify vanilla items or ships. The hammock model is cloned from the vanilla ship hammock at runtime.

## Credits

- Thanks to the [Sailwind modding notes](https://github.com/ai-pop/sailwind-modding-notes)

## License

MIT
