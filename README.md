# Farmtronics

This project is a [Stardew Valley mod](https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started) that adds the **"Farmtronics Home Computer"**, as well as programmable **Farmtronics Bots**.

The Home Computer is a computer that connects to the TV in your cabin.  Despite its early-80s appearance, it actually runs a very modern and elegant language, [MiniScript](https://miniscript.org).  (See [Why MiniScript](https://luminaryapps.com/blog/miniscript-why/), if you're curious.)

![Screen shot of the Farmtronics Home Computer](img/Demo-1.gif)

Bots each carry the same computer, but also have the ability to move around in the world and get things done.  All you have to do is program them!

## How to Play
1. Download the mod zip file from the [Releases](https://github.com/JoeStrout/Farmtronics/releases) page (or from [NexusMods](https://www.nexusmods.com/stardewvalley/mods/10634/)), and install it in the [usual way](https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started#Find_your_game_folder).
2. To use the **Farmtronics Home Computer**:
  - Activate the TV in your house.
  - Select the bottom-most option, *Farmtronics Home Computer*.
  - Type code at the prompt.  See https://miniscript.org for documentation on the language (and in particular, be sure to keep the [Quick Reference](https://miniscript.org/files/MiniScript-QuickRef.pdf) handy).
  - Also be sure to try the `help` command, and read through the various topics there.
  4. Press **Esc** to exit.
3. To obtain and use a **bot**:
  - On the Home Computer, use the `toDo` command to see the tasks you still need to complete.  Complete them.
  - Check your mail the next day (after completing all tasks).  You should have a letter with a Bot included.  (Once you have read this letter, you can purchase additional bots at Pierre's shop.)
  - Place the bot down in any empty spot on the map.
  - Right-click a bot to access its computer console.
  - Type code at the prompt.  This is the same code as on the Home Computer, but allows for some additional commands, like `me.position`, `me.left`, `me.right`, `me.forward`, `me.inventory`, `me.currentToolIndex` (which can be assigned to), and `me.useTool`.

See the [Wiki](https://github.com/JoeStrout/Farmtronics/wiki) for more documentation and sample code.

## Questions? Issues? Things to share?

Best place to discuss this mod is on the [MiniScript Discord](https://discord.gg/7s6zajx).  There is a #farmtronics channel there.

## Road Map

See [ROADMAP.md](ROADMAP.md) for our development plan, including what features are expected in which future versions of the mod.


==================================================
# Farmtronics C# Supervisor Extension
==================================================

This is an experimental extension to Farmtronics that adds a C# supervisor layer above the normal Farmtronics MiniScript bot behavior.

The basic idea is:

* C# handles planning, scanning, pathing, safety checks, bot roles, and coordination.
* MiniScript remains the small execution layer for bot actions such as moving, using tools, harvesting, and placing items.
* Bots are treated as physical actuators inside the Stardew Valley world.
* The supervisor tries to avoid long MiniScript programs and instead sends short, concrete actions.

This project is not a full replacement for Farmtronics. It is a practical automation/control layer built around the things that became useful during play.

## Core Architecture

The supervisor owns the high-level logic:

* scan the current location
* build available jobs
* assign jobs to bots
* path bots to the correct tile
* face the target
* send a short action script
* verify the result
* remember blocked/suppressed targets
* avoid unsafe actions

MiniScript is intentionally kept small. It should only execute direct actions such as:

```miniscript
me.forward
me.harvest
me.useTool
me.placeItem
```

The C# supervisor should decide *what* to do. MiniScript should only do the final physical action.

## Important Tile Terms

The supervisor uses two different tiles for every job:

```text
TargetTile = the tile/object/crop/machine the bot wants to affect
StandTile  = the adjacent tile where the bot must stand to affect TargetTile
```

This distinction is important.

A bot usually should **not** stand on the target. It should path to `StandTile`, face `TargetTile`, and then act.

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

The intended action sequence is:

```text
path to StandTile
confirm bot is actually at StandTile
confirm TargetTile is still valid
face TargetTile
send MiniScript action
verify result
```

## Safety Rule

Bots can be destructive if they continue executing old commands in the wrong location.

The most important safety rule is:

```text
Never allow a bot to carry an active script or plan across a location change.
```

If a bot changes location, warps, or is recalled:

* clear the bot script queue
* clear the supervisor plan
* cooldown briefly
* replan from the new location

This avoids the failure mode where a bot is mining in the mines, gets warped back to the farm, and continues using a pickaxe on farm machines or placed objects.

## Bot Warping Rule

Cross-location bot warping is unsafe.

The safer rule is:

```text
Only warp bots that are already in the same location as the player.
```

For cross-location travel:

1. pick up or carry the bot
2. move/warp the player
3. place the bot in the new location
4. allow the supervisor to reinitialize/replan there

Same-location recall is useful, especially in the mines, but cross-location recall can cause duplicated or ghost bot instances.

## Known Farmtronics Bot Issue

During testing, bots sometimes appeared to duplicate or reappear without tools. The likely explanation is that Farmtronics can leave behind duplicate or stale bot actors during warps/location transitions.

Symptoms observed:

* bots disappear and later reappear
* duplicate bots appear
* some duplicates have no tools
* tools may later reappear on another instance
* bots can continue old scripts after warping
* pickaxe/axe actions can destroy placed items if old scripts keep running

The supervisor should treat tool-less bots carefully.

Recommended behavior:

```text
If a bot has no tools:
    do not control it automatically
    optionally repair/reinitialize it
    clear any active script
    force cooldown/replan
```

## Machine Role System

Machine bots are assigned by name prefix. This prevents all bots from trying to use all machines and prevents the wrong bot from consuming inputs meant for another production chain.

Current machine role map:

```csharp
private static readonly Dictionary<string, HashSet<string>> BotMachineRoles =
    new(StringComparer.OrdinalIgnoreCase)
{
    ["keg"] = new()
    {
        "Keg"
    },

    ["wine"] = new()
    {
        "Keg"
    },

    ["jar"] = new()
    {
        "Preserves Jar"
    },

    ["preserves"] = new()
    {
        "Preserves Jar"
    },

    ["seed"] = new()
    {
        "Seed Maker"
    },

    ["forge"] = new()
    {
        "Furnace",
        "Charcoal Kiln"
    },

    ["animal"] = new()
    {
        "Mayonnaise Machine",
        "Cheese Press"
    },

    ["mayo"] = new()
    {
        "Mayonnaise Machine"
    },

    ["cheese"] = new()
    {
        "Cheese Press"
    },

    ["kiln"] = new()
    {
        "Charcoal Kiln"
    },

    ["furnace"] = new()
    {
        "Furnace"
    }
};
```

Example bot names:

```text
Wine 1       -> services Kegs
Wine 2       -> services Kegs
Jar 1        -> services Preserves Jars
Seed 1       -> services Seed Makers
Forge 1      -> services Furnaces and Charcoal Kilns
Mayo 1       -> services Mayonnaise Machines
Cheese 1     -> services Cheese Presses
```

The prefix is what matters. `Wine 1`, `Wine 2`, and `wine helper` are all wine/keg bots.

Bots without a machine role should generally be treated as farm/general bots and should not service machines unless explicitly allowed.

## Machine Service Philosophy

Machine service is intentionally simple.

Rather than perfectly classifying every possible machine state, the supervisor can use a practical routine:

1. if the machine is clearly busy, skip it
2. if the machine is ready, try to harvest
3. then try to insert an allowed input
4. suppress the same machine briefly after an attempt

This avoids loops where a bot repeatedly tries a machine but lacks the right input.

Useful state checks:

```csharp
private static bool IsReadyMachine(StardewValley.Object obj) =>
    obj.heldObject.Value != null && obj.readyForHarvest.Value;

private static bool IsBusyMachine(StardewValley.Object obj) =>
    obj.heldObject.Value != null
    && obj.MinutesUntilReady > 0
    && !obj.readyForHarvest.Value;
```

A failed or unproductive machine service attempt should not cause frantic retries. Suppress the machine briefly and rescan later.

## Safe Tool Use

Raw `me.useTool` is dangerous if the bot is facing the wrong object.

A safer approach is to wrap tool use in MiniScript helpers that inspect `me.ahead` immediately before using the tool.

Example concept:

```miniscript
clearTypes = ["Grass", "Stone", "Twig", "Weeds", "Bush", "Tree"]

SafeClearAhead = function
    a = me.ahead

    if a == null then
        print "SafeClearAhead: nothing ahead"
        return false
    end if

    if not a.hasIndex("type") then
        print "SafeClearAhead: ahead has no type"
        return false
    end if

    t = a.type

    if clearTypes.indexOf(t) == -1 then
        print "SafeClearAhead: refusing to clear " + t
        return false
    end if

    // Avoid fruit trees if me.ahead exposes enough information.
    if t == "Tree" then
        if a.hasIndex("isFruitTree") and a.isFruitTree then
            print "SafeClearAhead: refusing fruit tree"
            return false
        end if

        if a.hasIndex("fruitTree") and a.fruitTree then
            print "SafeClearAhead: refusing fruit tree"
            return false
        end if

        if a.hasIndex("name") and a.name.indexOf("Fruit") != -1 then
            print "SafeClearAhead: refusing probable fruit tree " + a.name
            return false
        end if
    end if

    me.useTool
    return true
end function
```

The exact fields returned by `me.ahead` may vary. Test by standing in front of normal trees, fruit trees, kegs, chests, stones, weeds, bushes, and bots.

Recommended safety stack:

```text
C# validates plan/location/target before sending an action.
MiniScript validates me.ahead immediately before me.useTool.
```

## Hotkeys

Current debug/control hotkeys:

```text
F5:
    Report farmer position.
    Also performs mouse tile lookup/debug logging.

F6:
    Report all bots.

F7:
    Warp same-location bots to the player.
    This should only affect bots already in the player's current location.

F8:
    Warp player home.

LeftShift + F8:
    Warp player to mines.

F9:
    Start all bots / start supervisor behavior.

F10:
    Mark all ToDoManager tasks done.

F11:
    Stop supervisor.
```

Current button handler:

```csharp
private void OnButtonPressed(object sender, ButtonPressedEventArgs e) {
    switch (e.Button) {
    case SButton.F5:
        Monitor.Log("F5 pressed: return Farmer Position.");
        supervisor.ReportFarmerPosition();
        Vector2 mousePos = Helper.Input.GetCursorPosition().Tile;
        Monitor.Log($"Performing lookup at mouse position: {mousePos}");
        bool occupied = Game1.player.currentLocation.IsTileOccupiedBy(mousePos);
        string name = "null";
        var obj = Game1.player.currentLocation.getObjectAtTile(mousePos.GetIntX(), mousePos.GetIntY());
        if (obj != null) name = obj.Name;
        Monitor.Log($"Object Lookup result [occupied: {occupied}]: {name}");
        break;

    case SButton.F6:
        Monitor.Log("F6 pressed: All Bots Report.");
        supervisor.ReportAllBots();
        break;

    case SButton.F7:
        Monitor.Log("F7 pressed: Warp Local Bots to Me.");
        supervisor.WarpSameLocationBotsToPlayer();
        break;

    case SButton.F8 when !e.IsDown(SButton.LeftShift):
        Monitor.Log("F8 pressed: Warp Myself Home.");
        supervisor.WarpPlayerHome();
        break;

    case SButton.F8 when e.IsDown(SButton.LeftShift):
        Monitor.Log("F8 pressed: Warp Myself to Mines.");
        supervisor.WarpPlayerToMines();
        break;

    case SButton.F9:
        Monitor.Log("F9 pressed: starting all bots.");
        supervisor.StartAllBots();
        break;

    case SButton.F10:
        ToDoManager.MarkAllTasksDone();
        Monitor.Log("F10: Mark all tasks done.");
        break;

    case SButton.F11:
        Monitor.Log("F11 pressed: stopping supervisor.");
        supervisor.Stop();
        break;
    }
}
```

## Recommended Debug Logging

Useful things to log:

```text
bot name
bot current location
bot tile
bot inventory/tool list
current supervisor mode
current plan type
TargetTile
StandTile
path length
last movement time
whether plan is still valid
why a job was rejected
why a target was blocked/suppressed
```

Especially useful bot report fields:

```text
name
location
tile
tools present
inventory summary
current plan
mode
last movement time
```

If duplicate/ghost bots appear, compare:

```text
same name, different instance/hash
same name, different location
same name, one with tools and one without tools
bot currentLocation mismatch
```

## Job Selection Notes

Useful job types include:

```text
TillSoil
WaterCrop
PlantCrop
HarvestCrop
ServiceMachine
MineBreakStone
```

A practical job filter pipeline:

```csharp
var jobs = BuildJobsForLocation(bot.currentLocation, state)
    .Where(job => !IsTargetIgnored(location, job.TargetTile))
    .Where(job => !IsTargetBlocked(bot.currentLocation, job.TargetTile, now))
    .Where(job => !IsJobSuppressed(job, now))
    .Where(job => BotCanDoJob(state, job))
    .OrderByDescending(job => ScoreJobForBot(state, job))
    .ToList();
```

Remember:

```text
Where(...) filters jobs.
OrderByDescending(...) only prioritizes jobs that survived filtering.
```

Machine-role bots should strongly prefer matching machine jobs. General farm bots should generally avoid machine service.

## Movement and Repathing

Bots can be bumped, blocked, or end up on the wrong tile.

Before acting, verify:

```text
bot is at plan.StandTile
plan target is still valid
bot is still in the expected location
```

If the bot is not at `StandTile`, try to repath from the bot's actual current tile to `StandTile`.

After repeated repath failures, block or suppress the target briefly.

Recommended behavior:

```text
if bot is not at StandTile:
    if still moving:
        wait
    if stuck:
        repath
    if repath repeatedly fails:
        block target and cooldown

if bot is at StandTile:
    validate TargetTile
    face TargetTile
    send action
```

## No-Jobs Behavior

When no jobs are available, the supervisor should not stop permanently.

Recommended behavior:

```text
if no jobs:
    enter cooldown
    rescan later
```

Example:

```csharp
state.NextAllowedPlanAt = now + TimeSpan.FromSeconds(60);
state.Mode = BotMode.Cooldown;
```

This lets bots wake up when crops mature, machines finish, or new opportunities appear.

## Design Lessons

The useful architecture is:

```text
C# supervisor = brain, planner, safety interlock
MiniScript     = hands
Bots           = physical actors
```

The supervisor should not trust old bot scripts. Every physical action should be checked as late as possible.

The most important invariants are:

```text
A plan belongs to a location.
A plan has a TargetTile and a StandTile.
The bot must be at StandTile before acting.
The target must still be valid before acting.
A tool action must not be sent blindly.
Cross-location bot warping is unsafe.
Tool-less duplicate bots should not be controlled.
Machine bots should be role-limited by name prefix.
```

## Build Notes

Typical build command from the Farmtronics project folder:

```bash
cd ~/projects/Farmtronics/Farmtronics/Farmtronics
dotnet build
```

SMAPI logs are typically here on Linux:

```bash
~/.config/StardewValley/ErrorLogs/SMAPI-latest.txt
```

If the project uses `Pathoschild.Stardew.ModBuildConfig` and has `GamePath` configured, building may deploy directly to the Stardew Valley mods folder.

## Status

This is experimental and practical rather than polished.

It is useful for:

* testing Farmtronics bot supervision
* assigning role-based machine bots
* debugging bot state
* safely recalling same-location bots
* reducing MiniScript complexity
* proving a C# supervisor architecture

Known risk areas:

* cross-location bot warping
* duplicate/ghost Farmtronics bot instances
* tool-less bot shells
* old scripts continuing after location changes
* raw `me.useTool` hitting unsafe targets
* bot inventory not behaving exactly like player inventory

Use the supervisor as a safety-first control layer, not as permission to blindly automate everything.

