# Farmtronics

This project is a [Stardew Valley mod](https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started) that adds the **"Farmtronics Home Computer"**, as well as programmable **Farmtronics Bots**.

The Home Computer is a computer that connects to the TV in your cabin. Despite its early-80s appearance, it actually runs a very modern and elegant language, [MiniScript](https://miniscript.org). (See [Why MiniScript](https://luminaryapps.com/blog/miniscript-why/), if you're curious.)

![Screen shot of the Farmtronics Home Computer](img/Demo-1.gif)

Bots each carry the same computer, but also have the ability to move around in the world and get things done. All you have to do is program them!

## How to Play

1. Download the mod zip file from the [Releases](https://github.com/JoeStrout/Farmtronics/releases) page (or from [NexusMods](https://www.nexusmods.com/stardewvalley/mods/10634/)), and install it in the [usual way](https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started#Find_your_game_folder).
2. To use the **Farmtronics Home Computer**:
   - Activate the TV in your house.
   - Select the bottom-most option, *Farmtronics Home Computer*.
   - Type code at the prompt. See https://miniscript.org for documentation on the language, and keep the [Quick Reference](https://miniscript.org/files/MiniScript-QuickRef.pdf) handy.
   - Try the `help` command and read through the topics there.
   - Press **Esc** to exit.
3. To obtain and use a **bot**:
   - On the Home Computer, use the `toDo` command to see the tasks you still need to complete. Complete them.
   - Check your mail the next day. You should have a letter with a Bot included. Once you have read this letter, you can purchase additional bots at Pierre's shop.
   - Place the bot down in any empty spot on the map.
   - Right-click a bot to access its computer console.
   - Type code at the prompt. This is the same code as on the Home Computer, but allows for additional commands like `me.position`, `me.left`, `me.right`, `me.forward`, `me.inventory`, `me.currentToolIndex`, and `me.useTool`.

See the [Wiki](https://github.com/JoeStrout/Farmtronics/wiki) for more documentation and sample code.

## Questions? Issues? Things to share?

Best place to discuss this mod is on the [MiniScript Discord](https://discord.gg/7s6zajx). There is a #farmtronics channel there.

## Road Map

See [ROADMAP.md](ROADMAP.md) for our development plan, including what features are expected in future versions of the mod.

## Farmtronics Supervisor Extension

This branch extends Farmtronics with a C# supervisor layer for coordinating bots in Stardew Valley.

The design goal is not unrestricted autonomy. The goal is to make bots useful, bounded helpers: bots should do only the work they are explicitly allowed to do, only where they are allowed to do it, while preserving bot identity, inventory, and world persistence.

MiniScript remains the bot-side action language. C# handles planning, scheduling, pathing, safety checks, reservations, and lifecycle management.

## Supervisor Design

Bots are treated as persistent actors, not disposable Stardew Valley objects.

The supervisor is responsible for:

- Tracking bot identity and persistence.
- Preventing duplicate or ghost bots from corrupting the world.
- Assigning jobs safely.
- Reserving targets so multiple bots do not pile onto the same job.
- Restricting bots by capability, order mode, and zone.
- Keeping machine work local to the bot's current location.
- Preventing risky actions such as planting, clearing, mining, or tool use unless explicitly enabled.

The C# supervisor decides what to do. MiniScript should only do the final physical action, such as:

```miniscript
me.forward
me.harvest
me.useTool
me.placeItem
```

## Important Safety Rules

Bots should never silently disappear, duplicate, overwrite each other, or destroy valuable state.

The supervisor enforces these principles:

- A real bot should not be deleted as part of normal gameplay.
- Duplicate or bricked bot objects may be safely removed only when they are clearly non-canonical and empty.
- A bot should not overwrite another bot or another world object.
- A bot should not be assigned a target, stand tile, destination, or home tile already reserved by another bot.
- Bots do not magically path across unrelated locations.
- Machine bots service machines only in their current `GameLocation`.
- Planting is an explicit capability.
- Clearing, mining, and other destructive actions are explicit capabilities.
- Old scripts and current plans are cleared when bots are relocated, recalled, quarantined, or moved between locations.

## Tile Terms

The supervisor uses two different tiles for most jobs:

```text
TargetTile = the tile/object/crop/machine the bot wants to affect
StandTile  = the adjacent tile where the bot must stand to affect TargetTile
```

A bot usually should not stand on the target. It should path to `StandTile`, face `TargetTile`, and then act.

Examples:

```text
TillSoil:
  TargetTile = empty diggable ground
  StandTile  = adjacent passable tile

ServiceMachine:
  TargetTile = keg / furnace / seed maker / preserves jar
  StandTile  = adjacent passable tile

HarvestCrop:
  TargetTile = crop tile
  StandTile  = adjacent passable tile
```

## Reservations

The scheduler treats work targets and stand tiles as reserved resources.

Active plans reserve:

- target tile
- stand tile
- destination tile
- home or parking tile where relevant

This prevents two bots from choosing the same crop, machine, object, stand tile, or final destination. Reservations are released when a plan completes, fails, is canceled, is invalidated, or the bot is relocated/quarantined/stopped.

`ft_bot_report` includes active reservations and stale-reservation cleanup information.

## Bot Orders

Each bot has an order state:

- mode
- capabilities
- assigned zones
- idle/status reason

Modes:

```text
off
work
home
follow
```

Capabilities:

```text
harvest
water
plant
fertilize
till
clear
mine
machines
kegs
jars
seedmakers
furnaces
all
```

`all` expands to:

```text
harvest water plant fertilize till clear mine
```

Planting must be explicitly enabled. The supervisor rejects seed inventories containing seeds that cannot mature before the end of the season.

`mine` includes mining jobs and hoeing diggable mine-floor tiles. Farm tilling still requires `till`.

## Machine Bots

Machine bots work by capability, not by farm location.

Existing name-prefix roles remain as defaults:

```text
keg / wine       -> Kegs
jar / preserves -> Preserves Jars
fruit/starfruit -> Kegs and Preserves Jars
seed            -> Seed Makers
forge           -> Furnaces and Charcoal Kilns
animal          -> Mayonnaise Machines and Cheese Presses
mayo            -> Mayonnaise Machines
cheese          -> Cheese Presses
kiln            -> Charcoal Kilns
furnace         -> Furnaces
```

Machine jobs are discovered by scanning the bot's current `GameLocation`. This allows a bot inside `FarmHouse` to service indoor Kegs and Preserves Jars without cross-location pathing.

Kegs have higher priority than Preserves Jars, so a `starfruit`/`fruit` bot uses Kegs first and Preserves Jars as overflow.

Example:

```text
ft_bot_role "Starfruit 1" kegs jars
ft_bot_mode "Starfruit 1" work
```

## Zones

Zones restrict where bots may work.

A bot with a valid capability still cannot take a crop/clear/mine job outside its assigned zone. This allows separate control over crop fields, machine rooms, tree areas, greenhouse space, and test areas.

Zone commands:

```text
ft_bot_zone_start <zone name>
ft_bot_zone_end <zone name>
ft_bot_assign_zone <bot name> <zone name>
```

Use zones to keep bots bounded. Do not rely on broad default/current-location behavior for risky work.

## Mine Behavior

Mine bots can work on the current mine floor when they have `mine` capability.

Mine jobs include:

- breaking stones
- cutting weeds
- hoeing diggable mine-floor tiles

When the local player moves from one mine floor to another, bots on the old mine floor follow to a safe tile near the player on the new floor if either:

- the bot is in `work` mode and has `mine`, or
- the bot is in `follow` mode.

This is not pathing through ladders or elevators. It is a supervised floor handoff that clears old scripts/plans and replans on the new floor.

## Commands

Useful SMAPI console commands:

```text
ft_bot_report
ft_bot_status <bot name>

ft_bot_role <bot name> <capability...>
ft_bot_mode <bot name> <off|work|home|follow>

ft_bot_zone_start <zone name>
ft_bot_zone_end <zone name>
ft_bot_assign_zone <bot name> <zone name>

ft_bot_move_here <bot name>
ft_bot_relocate <bot name>
ft_bot_send_home <bot name>

ft_bot_dedupe_dryrun
ft_bot_dedupe
```

Examples:

```text
ft_bot_role "Farmhand Bot" harvest water
ft_bot_role "Planter Bot" plant fertilize
ft_bot_role "Worker Wonka" all
ft_bot_role "Starfruit 1" kegs jars
ft_bot_role "Boy George" mine
ft_bot_mode "Boy George" work
ft_bot_status "Boy George"
```

## Recommended Roles

Safe everyday roles:

```text
Farmhand:
  harvest water

Greenhouse worker:
  harvest water

Starfruit processor:
  kegs jars

Planter:
  plant fertilize

Clearer:
  clear

Miner:
  mine
```

Avoid giving general-purpose bots broad capabilities unless you are intentionally testing. `Till`, `Clear`, `Mine`, and `Plant` should be deliberate special-purpose roles.

## No-Jobs Behavior

When no valid jobs exist, bots cooldown and rescan later. They should not pile onto the last target or share the same stand tile.

The status/report commands include idle reasons such as:

- no jobs generated for current capabilities/zones
- all generated jobs rejected by reservations, tools, inventory, or safety checks
- no reachable target found
- bot mode is off/home/follow

## Testing Checklist

Before trusting a new build:

1. Run `ft_bot_report`.
2. Confirm there are no duplicate or ghost bots.
3. Confirm active reservations are sane.
4. Confirm general farm bots do not have dangerous capabilities by default.
5. Test one machine bot in `FarmHouse`.
6. Test one harvest/water bot in a small zone.
7. Test one planter bot in a tiny zone.
8. Confirm doomed seeds are rejected near season end.
9. Test one mine bot across a mine-floor transition.
10. Save, quit, reload.
11. Run `ft_bot_report` again and compare bot count, locations, identities, inventory, orders, zones, and reservations.

## Build Notes

Typical build command from the Farmtronics project folder:

```bash
cd ~/projects/Farmtronics/Farmtronics
dotnet build Farmtronics/Farmtronics.csproj -p:EnableModDeploy=false
```

SMAPI logs are typically here on Linux:

```bash
~/.config/StardewValley/ErrorLogs/SMAPI-latest.txt
```

If `Pathoschild.Stardew.ModBuildConfig` has deploy enabled and the configured `GamePath` is writable, building may deploy directly to the Stardew Valley mods folder.

## Current Known-Good Operating Pattern

The most stable pattern is to keep bots specialized:

- One indoor Starfruit bot in `FarmHouse` for Kegs and Preserves Jars.
- One or more harvest/water bots for crop areas.
- A planter bot used only when intentionally planting.
- A clearer/miner bot only when explicitly testing or doing risky work.
- No broad cross-location automation.
- No outdoor valuable machines where NPC paths or destructive actions may affect them.

This preserves the fantasy of useful farm robots without turning the game into uncontrolled automation or debugging.
