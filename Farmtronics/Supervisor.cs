using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Farmtronics.Bot;
using Farmtronics.M1;
using Farmtronics.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.Xna.Framework;
using Miniscript;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Network;
using StardewValley.Objects;
using StardewValley.Tools;

namespace Farmtronics {
    internal sealed class Supervisor
    {

        private enum JobType
        {
            HarvestCrop,
            WaterCrop,
            PlantCrop,
            ClearDeadCrop,
            ClearDebris,
            ServiceMachine,
            TillSoil,
            MineBreakStone,
            MineCutWeeds,
            MineDigFloor,
        }
        private enum SupervisorMode
        {
            Idle,
            AllBots,
        }

        private enum BotMode
        {
            Idle,           // bot is supervised but has no current work
            Planning,       // supervisor should try to create a plan
            Queued,         // plan exists but script has not been sent yet
            Running,        // script has been queued/sent; shell is executing
            Cooldown,       // bot is waiting briefly before planning again
            Paused,         // bot cannot currently act, e.g. wrong location
        }

        [Flags]
        internal enum BotCapability
        {
            None = 0,
            Harvest = 1 << 0,
            Water = 1 << 1,
            Plant = 1 << 2,
            Fertilize = 1 << 3,
            Till = 1 << 4,
            Clear = 1 << 5,
            Machines = 1 << 6,
            Kegs = 1 << 7,
            Jars = 1 << 8,
            SeedMakers = 1 << 9,
            Furnaces = 1 << 10,
            Mine = 1 << 11,
        }

        private enum BotOrderMode
        {
            Off,
            Work,
            ReturnHome,
            Follow,
            Quarantine,
        }

        private sealed class BotZone
        {
            public string Name { get; init; }
            public string LocationName { get; init; }
            public Rectangle Bounds { get; init; }
        }

        private sealed class BotOrders
        {
            public BotCapability Capabilities { get; set; }
            public HashSet<string> AllowedZones { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AllowedMachines { get; } = new(StringComparer.OrdinalIgnoreCase);
            public BotOrderMode Mode { get; set; } = BotOrderMode.Work;
            public bool HasCapabilityOverride { get; set; }
        }
        private sealed class BotJob
        {
            public JobType Type { get; init; }
            public string JobKey { get; init; }
            public string TargetName { get; init; }
            public Vector2 TargetTile { get; init; }
            public Vector2 AdjacentTile { get; init; }
            public int BasePriority { get; init; }

            public List<MachineInputRule> InputRules { get; init; }   // Fruit, Vegetable, Seed, Artisan Goods     // Wood, Copper Ore, Truffle
            public bool RequiresScythe { get; init; }
            public bool CanFertilize { get; init; }
        }

        private sealed class BotSupervisorState
        {
            public BotObject Bot;
            public string BotName;
            public BotMode Mode;
            public PendingPlan CurrentPlan;
            public Vector2 LastObservedTile;
            public TimeSpan LastMovementAt;
            public TimeSpan NextAllowedPlanAt;
            public string LastNoPlanReason;
        }

        private sealed class PendingPlan
        {
            public JobType JobType { get; init; }
            public string JobKey { get; init; }
            public string PlanId { get; init; }
            public string LocationName { get; init; }

            public Vector2 TargetTile { get; init; }
            public Vector2 StandTile { get; init; }
            public string TargetName { get; init; }
            public Vector2 AdjacentTile { get; init; }
            public Vector2 StartTile { get; init; }

            public string Script { get; init; }
            public TimeSpan? QueuedAt { get; set; }
            public int RepathAttempts { get; set; }
        }

        private sealed class BotReservation
        {
            public string BotName { get; init; }
            public string BotGuid { get; init; }
            public string JobType { get; init; }
            public string LocationName { get; init; }
            public Vector2 TargetTile { get; init; }
            public Vector2 StandTile { get; init; }
            public Vector2 ReservedTile { get; init; }
            public TimeSpan CreatedAt { get; init; }
            public string PlanId { get; init; }
        }

        private sealed class JobMemory
        {
            public int FailureCount { get; set; }
            public TimeSpan SuppressedUntil { get; set; }
            public bool IgnoreForRun { get; set; }
            public string LastReason { get; set; }
        }

        private readonly Dictionary<string, JobMemory> jobMemory = new();
        private static readonly TimeSpan shortJobCooldown = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan mediumJobCooldown = TimeSpan.FromSeconds(10);
        private const int maxJobFailuresBeforeIgnore = 3;

        private SupervisorMode mode = SupervisorMode.Idle;
        private readonly Dictionary<string, BotSupervisorState> botStates = new();
        private readonly Dictionary<string, TimeSpan> blockedTargets = new();
        private readonly Dictionary<string, TimeSpan> rateLimitedLogTimes = new();
        private readonly Dictionary<string, BotReservation> ReservedTargetTiles = new();
        private readonly Dictionary<string, BotReservation> ReservedStandTiles = new();
        private readonly Dictionary<string, BotReservation> ReservedDestinationTiles = new();
        private readonly Dictionary<string, BotReservation> ReservedHomeTiles = new();
        private readonly Dictionary<string, BotOrders> botOrders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BotZone> botZones = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string LocationName, Vector2 Tile)> zoneDraftStarts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan reservationTimeout = TimeSpan.FromMinutes(2);
        private static readonly string[] resetBotNames = { "keg 1", "forge 1", "seed 1" };
        private static readonly HashSet<string> resetBotNameSet = new(resetBotNames, StringComparer.OrdinalIgnoreCase);
        private sealed record BotWorldEntry(BotObject Bot, GameLocation Location, Vector2 Tile);
        private sealed record BotStoredEntry(BotObject Bot, string Container);
        private sealed record BotSnapshot(
            BotObject Bot,
            string Classification,
            GameLocation Location,
            Vector2 Tile,
            string Container,
            bool IsTracked,
            bool IsPlaced,
            bool IsStored,
            bool InSupervisorState
        );
        private sealed class DedupeSummary
        {
            public int DeletedBrickedDuplicates { get; set; }
            public int QuarantinedDuplicates { get; set; }
            public int SkippedValuableDuplicates { get; set; }
            public int RemainingUnknownBots { get; set; }
        }
        private readonly Dictionary<string, BotObject> knownCanonicalByGuid = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Vector2> namedHomeTiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["keg 1"] = new Vector2(64, 15),
            ["seed 1"] = new Vector2(66, 15),
            ["forge 1"] = new Vector2(68, 15),
            ["farm 1"] = new Vector2(64, 17),
            ["farm 2"] = new Vector2(66, 17),
        };

        private sealed record MachineServiceMemory(
            MachineFingerprint Fingerprint,
            TimeSpan SuppressedUntil
        );

        private readonly Dictionary<Vector2, MachineServiceMemory> _machineServiceMemory = new();
        private const int AutoPauseTime = 2400; // midnight
        private bool autoPausedTonight = false;

        private bool IsControllableBot(BotObject bot)
        {
            if (bot == null)
                return false;

            if (bot.farmer == null || bot.inventory == null)
                return false;

            EnsureInventorySlots(bot);
            SyncToolsForOrders(bot, GetOrdersForBot(bot));
            return true;
        }

        private static bool RequiresNormalTools(BotOrders orders)
        {
            if (orders == null)
                return false;

            const BotCapability nonMachineCapabilities =
                BotCapability.Harvest
                | BotCapability.Water
                | BotCapability.Plant
                | BotCapability.Fertilize
                | BotCapability.Till
                | BotCapability.Clear
                | BotCapability.Mine;

            return (orders.Capabilities & nonMachineCapabilities) != 0;
        }

        private static void EnsureInventorySlots(BotObject bot)
        {
            if (bot?.farmer?.Items == null)
                return;

            for (int i = bot.farmer.Items.Count; i < bot.GetActualCapacity(); i++)
                bot.farmer.Items.Add(null);
        }

        private void SyncToolsForOrders(BotObject bot, BotOrders orders)
        {
            if (bot?.farmer?.Items == null || orders == null)
                return;

            if (RequiresNormalTools(orders))
            {
                EnsureToolsForOrders(bot, orders);
                return;
            }

            if ((orders.Capabilities & BotCapability.Machines) != 0)
                RemoveStarterToolsForMachineOnlyBot(bot);
        }

        private void EnsureToolsForOrders(BotObject bot, BotOrders orders)
        {
            if (bot?.farmer?.Items == null || !RequiresNormalTools(orders))
                return;

            EnsureInventorySlots(bot);

            bool addedAnyTool = false;
            foreach (Item starterTool in Farmer.initialTools())
            {
                if (starterTool == null || BotHasEquivalentTool(bot, starterTool))
                    continue;

                if (!TryAddItemToBotInventory(bot, starterTool))
                {
                    ModEntry.instance.Monitor.Log(
                        $"Tool provisioning skipped for {bot.name}: no empty slot for {starterTool.Name}.",
                        LogLevel.Warn);
                    continue;
                }

                addedAnyTool = true;
            }

            if (addedAnyTool)
            {
                bot.data.Update();
                ModEntry.instance.Monitor.Log(
                    $"Issued normal starter tools to {bot.name} for non-machine work.",
                    LogLevel.Warn);
            }
        }

        private void RemoveStarterToolsForMachineOnlyBot(BotObject bot)
        {
            if (bot?.farmer?.Items == null)
                return;

            var starterTools = Farmer.initialTools().Where(item => item is Tool).ToList();
            bool removedAnyTool = false;
            for (int i = 0; i < bot.farmer.Items.Count; i++)
            {
                Item item = bot.farmer.Items[i];
                if (item == null)
                    continue;

                if (!starterTools.Any(starterTool => IsEquivalentTool(item, starterTool)))
                    continue;

                bot.farmer.Items[i] = null;
                removedAnyTool = true;
            }

            if (removedAnyTool)
            {
                bot.data.Update();
                ModEntry.instance.Monitor.Log(
                    $"Removed normal starter tools from machine-only bot {bot.name}.",
                    LogLevel.Warn);
            }
        }

        private static bool BotHasEquivalentTool(BotObject bot, Item starterTool)
        {
            return bot?.farmer?.Items?.Any(item =>
                IsEquivalentTool(item, starterTool)) == true;
        }

        private static bool IsEquivalentTool(Item item, Item starterTool)
        {
            return item != null
                && starterTool != null
                && item is Tool
                && item.GetType() == starterTool.GetType()
                && string.Equals(item.Name, starterTool.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryAddItemToBotInventory(BotObject bot, Item item)
        {
            if (bot?.farmer?.Items == null || item == null)
                return false;

            for (int i = 0; i < bot.farmer.Items.Count; i++)
            {
                if (bot.farmer.Items[i] != null)
                    continue;

                bot.farmer.Items[i] = item;
                return true;
            }

            if (bot.farmer.Items.Count < bot.GetActualCapacity())
            {
                bot.farmer.Items.Add(item);
                return true;
            }

            return false;
        }

        private void LoadBotStatesToSupervisor(bool singleBot = false)
        {
            botStates.Clear();

            foreach (var bot in BotManager.GetAllBots())
            {
                if (bot == null)
                {
                    ModEntry.instance.Monitor.Log("Supervisor skipped null bot.");
                    continue;
                }

                if (IsControllableBot(bot)) {
                    bot.InitShell();
                }

                var botState = new BotSupervisorState
                {
                    Bot = bot,
                    BotName = bot.name,
                    Mode = BotMode.Planning,
                    CurrentPlan = null,
                    LastObservedTile = bot.TileLocation,
                    LastMovementAt = Game1.currentGameTime.TotalGameTime,
                    NextAllowedPlanAt = TimeSpan.Zero,
                };

                botStates[bot.name] = botState;

                ModEntry.instance.Monitor.Log(
                    $"Supervisor loaded bot {bot.name} at tile {bot.TileLocation.X},{bot.TileLocation.Y}.");

                if (singleBot) break;
            }

            if (botStates.Count == 0)
                ModEntry.instance.Monitor.Log("Supervisor found no farm bots to supervise.");
        }
        private readonly Dictionary<Vector2, int> _machineCooldownUntilTick = new();

        bool IsMachineOnCooldown(Vector2 tile)
        {
            return _machineCooldownUntilTick.TryGetValue(tile, out var untilTick)
                && Game1.ticks < untilTick;
        }

        private void MarkMachineServiced(Vector2 tile, Farm farm)
        {
            if (!farm.objects.TryGetValue(tile, out StardewValley.Object obj))
                return;

            _lastServicedMachines[tile] = Fingerprint(obj);

            ModEntry.instance.Monitor.Log(
                $"Marked machine serviced: {obj.Name} at {tile.X},{tile.Y}, " +
                $"held={obj.heldObject.Value?.Name ?? "null"}, " +
                $"minutes={obj.MinutesUntilReady}, " +
                $"ready={obj.readyForHarvest.Value}",
                LogLevel.Trace);
        }

        internal bool TryParseCapability(string text, out BotCapability capability)
        {
            capability = BotCapability.None;
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "all":
                    capability = BotCapability.Harvest
                        | BotCapability.Water
                        | BotCapability.Plant
                        | BotCapability.Fertilize
                        | BotCapability.Till
                        | BotCapability.Clear
                        | BotCapability.Mine;
                    return true;
                case "harvest":
                    capability = BotCapability.Harvest;
                    return true;
                case "water":
                    capability = BotCapability.Water;
                    return true;
                case "plant":
                    capability = BotCapability.Plant;
                    return true;
                case "fertilize":
                case "fertilizer":
                    capability = BotCapability.Fertilize;
                    return true;
                case "till":
                    capability = BotCapability.Till;
                    return true;
                case "clear":
                case "chop":
                    capability = BotCapability.Clear;
                    return true;
                case "mine":
                case "mining":
                    capability = BotCapability.Mine;
                    return true;
                case "all_machines":
                case "all-machines":
                case "allmachines":
                case "machines":
                case "machine":
                    capability = BotCapability.Machines;
                    return true;
                case "kegs":
                case "keg":
                case "wine":
                    capability = BotCapability.Kegs;
                    return true;
                case "jars":
                case "jar":
                case "preserves":
                    capability = BotCapability.Jars;
                    return true;
                case "seedmakers":
                case "seedmaker":
                case "seed":
                    capability = BotCapability.SeedMakers;
                    return true;
                case "furnaces":
                case "furnace":
                case "forge":
                    capability = BotCapability.Furnaces;
                    return true;
                default:
                    if (TryGetAllowedMachinesForRoleToken(text, out _))
                    {
                        capability = BotCapability.Machines;
                        return true;
                    }
                    return false;
            }
        }

        public void SetBotRole(string botName, IEnumerable<string> capabilityNames)
        {
            botName = NormalizeBotName(botName);
            if (string.IsNullOrWhiteSpace(botName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_role refused: missing bot name.", LogLevel.Warn);
                return;
            }

            BotCapability capabilities = BotCapability.None;
            var unknown = new List<string>();
            var allowedMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool allowAllMachines = false;
            foreach (var name in capabilityNames ?? Enumerable.Empty<string>())
            {
                if (TryParseCapability(name, out var capability))
                {
                    capabilities |= capability;
                    if (TryGetAllowedMachinesForRoleToken(name, out var tokenMachines))
                    {
                        foreach (string machineName in tokenMachines)
                            allowedMachines.Add(machineName);
                    }
                    else if (IsAllMachinesToken(name))
                    {
                        allowAllMachines = true;
                    }
                }
                else
                    unknown.Add(name);
            }

            if (unknown.Count > 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"ft_bot_role refused for {botName}: unknown capabilities {string.Join(", ", unknown)}.",
                    LogLevel.Warn);
                return;
            }

            var orders = GetMutableOrders(botName);
            orders.Capabilities = ExpandMachineCapabilities(capabilities);
            orders.AllowedMachines.Clear();
            if (!allowAllMachines)
            {
                foreach (string machineName in allowedMachines)
                    orders.AllowedMachines.Add(machineName);
            }
            orders.HasCapabilityOverride = true;
            var state = FindBotStateByName(botName);
            if (state?.Bot != null)
            {
                SyncToolsForOrders(state.Bot, orders);
                ClearBotScriptQueue(state.Bot);
                ClearCurrentPlan(state);
                state.Mode = BotMode.Planning;
                state.NextAllowedPlanAt = Game1.currentGameTime.TotalGameTime + TimeSpan.FromSeconds(1);
                state.LastNoPlanReason = "Bot role changed.";
            }
            ModEntry.instance.Monitor.Log(
                $"Orders updated for {botName}: capabilities={FormatCapabilities(orders.Capabilities)} machines={FormatMachines(orders)}.",
                LogLevel.Warn);
        }

        public void SetBotOrderMode(string botName, string modeName)
        {
            botName = NormalizeBotName(botName);
            if (string.IsNullOrWhiteSpace(botName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_mode refused: missing bot name.", LogLevel.Warn);
                return;
            }

            if (!TryParseOrderMode(modeName, out var orderMode))
            {
                ModEntry.instance.Monitor.Log(
                    $"ft_bot_mode refused for {botName}: unknown mode '{modeName}'. Use off, work, home, or follow.",
                    LogLevel.Warn);
                return;
            }

            var orders = GetMutableOrders(botName);
            orders.Mode = orderMode;
            if (orderMode != BotOrderMode.Work)
            {
                var state = FindBotStateByName(botName);
                if (state?.Bot != null)
                {
                    ClearBotScriptQueue(state.Bot);
                    ClearCurrentPlan(state);
                    state.Mode = BotMode.Cooldown;
                    state.NextAllowedPlanAt = Game1.currentGameTime.TotalGameTime + TimeSpan.FromSeconds(1);
                    state.LastNoPlanReason = $"Bot mode is {orderMode}.";
                }
            }
            ModEntry.instance.Monitor.Log($"Orders updated for {botName}: mode={orderMode}.", LogLevel.Warn);
        }

        public void StartZoneDraft(string zoneName)
        {
            var player = Game1.player;
            if (player?.currentLocation == null || string.IsNullOrWhiteSpace(zoneName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_zone_start refused: missing zone name or player location.", LogLevel.Warn);
                return;
            }

            zoneDraftStarts[zoneName.Trim()] = (player.currentLocation.NameOrUniqueName, player.Tile);
            ModEntry.instance.Monitor.Log(
                $"Zone start set for {zoneName}: {player.currentLocation.NameOrUniqueName} {(int)player.Tile.X},{(int)player.Tile.Y}.",
                LogLevel.Warn);
        }

        public void EndZoneDraft(string zoneName)
        {
            var player = Game1.player;
            if (player?.currentLocation == null || string.IsNullOrWhiteSpace(zoneName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_zone_end refused: missing zone name or player location.", LogLevel.Warn);
                return;
            }

            zoneName = zoneName.Trim();
            if (!zoneDraftStarts.TryGetValue(zoneName, out var start))
            {
                ModEntry.instance.Monitor.Log($"ft_bot_zone_end refused: no start recorded for zone {zoneName}.", LogLevel.Warn);
                return;
            }

            if (!string.Equals(start.LocationName, player.currentLocation.NameOrUniqueName, StringComparison.Ordinal))
            {
                ModEntry.instance.Monitor.Log(
                    $"ft_bot_zone_end refused for {zoneName}: start was in {start.LocationName}, current location is {player.currentLocation.NameOrUniqueName}.",
                    LogLevel.Warn);
                return;
            }

            int minX = Math.Min((int)start.Tile.X, (int)player.Tile.X);
            int minY = Math.Min((int)start.Tile.Y, (int)player.Tile.Y);
            int maxX = Math.Max((int)start.Tile.X, (int)player.Tile.X);
            int maxY = Math.Max((int)start.Tile.Y, (int)player.Tile.Y);
            botZones[zoneName] = new BotZone
            {
                Name = zoneName,
                LocationName = start.LocationName,
                Bounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1),
            };
            zoneDraftStarts.Remove(zoneName);

            ModEntry.instance.Monitor.Log(
                $"Zone saved: {zoneName} loc={start.LocationName} rect={minX},{minY}..{maxX},{maxY}.",
                LogLevel.Warn);
        }

        public void AssignZoneToBot(string botName, string zoneName)
        {
            botName = NormalizeBotName(botName);
            if (string.IsNullOrWhiteSpace(botName) || string.IsNullOrWhiteSpace(zoneName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_assign_zone refused: usage ft_bot_assign_zone <bot name> <zone name>.", LogLevel.Warn);
                return;
            }

            zoneName = zoneName.Trim();
            if (!botZones.ContainsKey(zoneName))
            {
                ModEntry.instance.Monitor.Log($"ft_bot_assign_zone refused: unknown zone {zoneName}.", LogLevel.Warn);
                return;
            }

            var orders = GetMutableOrders(botName);
            orders.AllowedZones.Add(zoneName);
            ModEntry.instance.Monitor.Log($"Assigned zone {zoneName} to {botName}.", LogLevel.Warn);
        }

        public void ReportBotStatus(string botName)
        {
            botName = NormalizeBotName(botName);
            if (string.IsNullOrWhiteSpace(botName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_status refused: missing bot name.", LogLevel.Warn);
                return;
            }

            var state = FindBotStateByName(botName);
            var bot = state?.Bot ?? BotManager.GetAllBots().FirstOrDefault(bot => string.Equals(bot?.name, botName, StringComparison.OrdinalIgnoreCase));
            var orders = GetOrdersForBot(bot?.name ?? botName);

            ModEntry.instance.Monitor.Log(
                $"Bot status {botName}: loc={bot?.currentLocation?.NameOrUniqueName ?? "(unknown)"} tile={bot?.TileLocation.X},{bot?.TileLocation.Y} supervisorMode={state?.Mode.ToString() ?? "(untracked)"} orderMode={orders.Mode} capabilities={FormatCapabilities(orders.Capabilities)} machines={FormatMachines(orders)} zones={FormatZones(orders)} idleReason={state?.LastNoPlanReason ?? "(none)"} currentPlan={DescribePlan(state?.CurrentPlan)}.",
                LogLevel.Warn);
        }

        private static bool TryParseOrderMode(string text, out BotOrderMode mode)
        {
            mode = BotOrderMode.Work;
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "off":
                    mode = BotOrderMode.Off;
                    return true;
                case "work":
                    mode = BotOrderMode.Work;
                    return true;
                case "home":
                case "return-home":
                    mode = BotOrderMode.ReturnHome;
                    return true;
                case "follow":
                    mode = BotOrderMode.Follow;
                    return true;
                case "quarantine":
                    mode = BotOrderMode.Quarantine;
                    return true;
                default:
                    return false;
            }
        }

        private BotOrders GetMutableOrders(string botName)
        {
            botName = NormalizeBotName(botName);
            if (!botOrders.TryGetValue(botName, out var orders))
            {
                orders = BuildDefaultOrders(botName);
                botOrders[botName] = orders;
            }

            return orders;
        }

        private BotOrders GetOrdersForBot(BotObject bot)
        {
            return GetOrdersForBot(bot?.name);
        }

        private BotOrders GetOrdersForBot(string botName)
        {
            botName = NormalizeBotName(botName);
            if (botOrders.TryGetValue(botName, out var orders))
                return orders;

            return BuildDefaultOrders(botName);
        }

        private static string NormalizeBotName(string botName)
        {
            botName = (botName ?? "").Trim();
            if (botName.Length >= 2
                && ((botName[0] == '"' && botName[^1] == '"')
                    || (botName[0] == '\'' && botName[^1] == '\'')))
            {
                botName = botName[1..^1].Trim();
            }

            return botName;
        }

        private BotSupervisorState FindBotStateByName(string botName)
        {
            botName = NormalizeBotName(botName);
            return botStates.Values.FirstOrDefault(state =>
                string.Equals(NormalizeBotName(state.BotName), botName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeBotName(state.Bot?.name), botName, StringComparison.OrdinalIgnoreCase));
        }

        private BotOrders BuildDefaultOrders(string botName)
        {
            var orders = new BotOrders
            {
                Capabilities = BotCapability.Harvest | BotCapability.Water | BotCapability.Till | BotCapability.Clear | BotCapability.Mine,
                Mode = BotOrderMode.Work,
            };

            if (TryGetAllowedMachinesForBot(botName, out var allowedMachines))
            {
                orders.Capabilities = CapabilitiesForMachines(allowedMachines);
                foreach (string machineName in allowedMachines)
                    orders.AllowedMachines.Add(machineName);
            }

            return orders;
        }

        private static BotCapability CapabilitiesForMachines(HashSet<string> allowedMachines)
        {
            BotCapability capabilities = BotCapability.Machines;
            if (allowedMachines.Contains("Keg"))
                capabilities |= BotCapability.Kegs;
            if (allowedMachines.Contains("Preserves Jar"))
                capabilities |= BotCapability.Jars;
            if (allowedMachines.Contains("Seed Maker"))
                capabilities |= BotCapability.SeedMakers;
            if (allowedMachines.Contains("Furnace") || allowedMachines.Contains("Charcoal Kiln"))
                capabilities |= BotCapability.Furnaces;
            return capabilities;
        }

        private static BotCapability ExpandMachineCapabilities(BotCapability capabilities)
        {
            if ((capabilities & (BotCapability.Kegs | BotCapability.Jars | BotCapability.SeedMakers | BotCapability.Furnaces)) != 0)
                capabilities |= BotCapability.Machines;
            return capabilities;
        }

        private static string FormatCapabilities(BotCapability capabilities)
        {
            return capabilities == BotCapability.None ? "none" : capabilities.ToString();
        }

        private static string FormatZones(BotOrders orders)
        {
            return orders == null || orders.AllowedZones.Count == 0
                ? "(default/current location)"
                : string.Join(",", orders.AllowedZones.OrderBy(zone => zone));
        }

        private static string FormatMachines(BotOrders orders)
        {
            if (orders == null || (orders.Capabilities & BotCapability.Machines) == 0)
                return "(none)";

            if (orders.AllowedMachines.Count > 0)
                return string.Join(",", orders.AllowedMachines.OrderBy(machine => machine));

            bool hasSpecificMachineCapabilities =
                (orders.Capabilities & (BotCapability.Kegs | BotCapability.Jars | BotCapability.SeedMakers | BotCapability.Furnaces)) != 0;
            if (!hasSpecificMachineCapabilities)
                return "all";

            var machines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if ((orders.Capabilities & BotCapability.Kegs) != 0)
                machines.Add("Keg");
            if ((orders.Capabilities & BotCapability.Jars) != 0)
                machines.Add("Preserves Jar");
            if ((orders.Capabilities & BotCapability.SeedMakers) != 0)
                machines.Add("Seed Maker");
            if ((orders.Capabilities & BotCapability.Furnaces) != 0)
            {
                machines.Add("Furnace");
                machines.Add("Charcoal Kiln");
            }

            return string.Join(",", machines.OrderBy(machine => machine));
        }

        private static string DescribePlan(PendingPlan plan)
        {
            if (plan == null)
                return "(none)";

            return $"{plan.JobType} {plan.LocationName} {(int)plan.TargetTile.X},{(int)plan.TargetTile.Y}";
        }

		private static readonly HashSet<string> clearTypes = new() { "Tree", "Stone", "Twig", "Weeds", "Grass" };
		private static readonly TimeSpan blockedCooldown = TimeSpan.FromSeconds(5);
		private static readonly TimeSpan stuckTimeout = TimeSpan.FromSeconds(2);
        private readonly Dictionary<string, int> targetFailureCounts = new();
        private static readonly int maxTargetFailures = 2;
        private sealed record MachineFingerprint(
            string Name,
            string HeldObjectName,
            int MinutesUntilReady,
            bool ReadyForHarvest
        );

        private static MachineFingerprint Fingerprint(StardewValley.Object obj)
        {
            return new MachineFingerprint(
                obj.Name,
                obj.heldObject.Value?.Name,
                obj.MinutesUntilReady,
                obj.readyForHarvest.Value
            );
        }
        private readonly Dictionary<Vector2, MachineFingerprint> _lastServicedMachines = new();
        private bool ShouldCreateServiceMachineJob(Vector2 tile, StardewValley.Object obj)
        {
            var now = Fingerprint(obj);

            if (_lastServicedMachines.TryGetValue(tile, out var last)
                && last.Equals(now))
            {
                return false;
            }

            return true;
        }

		public void Reset()
        {
            mode = SupervisorMode.Idle;
            botStates.Clear();
            blockedTargets.Clear();
            targetFailureCounts.Clear();
            jobMemory.Clear();
            _machineServiceMemory.Clear();
            _machineCooldownUntilTick.Clear();
            _lastServicedMachines.Clear();
            botOrders.Clear();
            botZones.Clear();
            zoneDraftStarts.Clear();
            ReservedTargetTiles.Clear();
            ReservedStandTiles.Clear();
            ReservedDestinationTiles.Clear();
            ReservedHomeTiles.Clear();
            ModEntry.instance.Monitor.Log("Supervisor reset.");
        }

        public void Stop()
        {
            mode = SupervisorMode.Idle;
            botStates.Clear();
            blockedTargets.Clear();
            targetFailureCounts.Clear();
            botOrders.Clear();
            botZones.Clear();
            zoneDraftStarts.Clear();
            ReservedTargetTiles.Clear();
            ReservedStandTiles.Clear();
            ReservedDestinationTiles.Clear();
            ReservedHomeTiles.Clear();
            ModEntry.instance.Monitor.Log("Supervisor stopped.");
        }

		public void StartAllBots() {
			mode = SupervisorMode.AllBots;
			blockedTargets.Clear();
			targetFailureCounts.Clear();
            ReservedTargetTiles.Clear();
            ReservedStandTiles.Clear();
            ReservedDestinationTiles.Clear();
            ReservedHomeTiles.Clear();
			LoadBotStatesToSupervisor(singleBot: false);
			ModEntry.instance.Monitor.Log("Supervisor started: all-bots.");
		}

        private void CheckAutoPauseAtMidnight()
        {
            if (!Context.IsWorldReady)
                return;

            if (Game1.timeOfDay < 600)
            {
                autoPausedTonight = false;
                return;
            }

            if (autoPausedTonight)
                return;

            if (Game1.timeOfDay < AutoPauseTime)
                return;

            autoPausedTonight = true;

            ModEntry.instance.Monitor.Log(
                $"Auto-pause skipped at time {Game1.timeOfDay}; direct menu opens during save/night transitions can unbalance UI mode.",
                LogLevel.Warn);
        }

        public void Update(GameTime gameTime)
        {
            if (!Context.IsMainPlayer) return;
            if (!Context.IsWorldReady) return;

            CheckAutoPauseAtMidnight();
            ValidateBotPersistence();

            if (mode == SupervisorMode.Idle) return;

            TimeSpan now = gameTime.TotalGameTime;
            CleanupBlockedTargets(now);
            CleanupStaleReservations(now);

            foreach (var state in botStates.Values.ToList())
            {
                UpdateBotState(state, now);
            }
        }

        private void UpdateBotState(BotSupervisorState state, TimeSpan now)
        {
            var bot = state.Bot;
            if (bot == null)
            {
                state.Mode = BotMode.Idle;
                state.LastNoPlanReason = "Bot object is null.";
                return;
            }

            switch (state.Mode)
            {
                case BotMode.Planning:
                    if (now < state.NextAllowedPlanAt)
                    {
                        state.LastNoPlanReason = $"Waiting for cooldown: {state.NextAllowedPlanAt - now:mm\\:ss}";
                        break;
                    }

                    TryCreatePlanForBot(state, now);
                break;

                case BotMode.Queued:
                    if (bot.shell.IsReadyForCommand())
                    {
                        if (!ValidatePlanForExecution(state, out string reason))
                        {
                            LogPlanToolSafety(bot, state.CurrentPlan, allowed: false, reason);
                            ClearBotScriptQueue(bot);
                            ClearCurrentPlan(state);
                            state.Mode = BotMode.Cooldown;
                            state.NextAllowedPlanAt = now + TimeSpan.FromSeconds(1);
                            state.LastNoPlanReason = reason;
                            break;
                        }

                        LogPlanToolSafety(bot, state.CurrentPlan, allowed: true, "validated");
                        ModEntry.instance.Monitor.Log($"Supervisor sending script to {bot.name} for target tile {state.CurrentPlan.TargetTile.X},{state.CurrentPlan.TargetTile.Y} name {state.CurrentPlan.TargetName}");
                        bot.shell.QueueCommand(state.CurrentPlan.Script);
                        state.CurrentPlan.QueuedAt = now;
                        state.LastObservedTile = bot.TileLocation;
                        state.LastMovementAt = now;
                        state.Mode = BotMode.Running;
                    }
                    break;

                case BotMode.Running:
                    UpdateMovementTracking(state, now);

                    if (!bot.shell.IsReadyForCommand() || bot.shell.HasQueuedCommands())
                        break;

                    VerifyPlanResult(state, now);
                    break;

                case BotMode.Cooldown:
                    if (now >= state.NextAllowedPlanAt)
                    {
                        state.Mode = BotMode.Planning;
                        state.LastNoPlanReason = null;
                    }
                    break;
                case BotMode.Idle:
                    ClearCurrentPlan(state);
                    state.NextAllowedPlanAt = now + TimeSpan.FromSeconds(10);
                    state.Mode = BotMode.Cooldown;
                    break;
            }
        }   
        private static readonly TimeSpan machineFailedAttemptCooldown = TimeSpan.FromSeconds(10);

        private void MarkMachineServiceAttempted(GameLocation location, Vector2 tile, TimeSpan now)
        {
            if (!location.objects.TryGetValue(tile, out StardewValley.Object obj))
                return;

            _machineServiceMemory[tile] = new MachineServiceMemory(
                Fingerprint(obj),
                now + machineFailedAttemptCooldown
            );

            ModEntry.instance.Monitor.Log(
                $"Marked machine attempt: {obj.Name} at {tile.X},{tile.Y}, " +
                $"held={obj.heldObject.Value?.Name ?? "null"}, " +
                $"minutes={obj.MinutesUntilReady}, " +
                $"ready={obj.readyForHarvest.Value}, " +
                $"suppressedUntil={now + machineFailedAttemptCooldown}",
                LogLevel.Trace);
        }
        private static Vector2 GetBotTile(BotObject bot)
        {
            return new Vector2(
                (int)(bot.Position.X / 64),
                (int)(bot.Position.Y / 64));
        }
        
        private bool TryRepathCurrentPlan(BotSupervisorState state, TimeSpan now)
        {
            var bot = state.Bot;
            var plan = state.CurrentPlan;

            if (plan.RepathAttempts >= 3)
            {
                ModEntry.instance.Monitor.Log(
                    $"Supervisor: giving up after {plan.RepathAttempts} repaths for {bot.name}: " +
                    $"{plan.JobType} at {plan.TargetTile.X},{plan.TargetTile.Y}",
                    LogLevel.Trace);

                BlockTarget(bot.currentLocation, plan.TargetTile, now);
                ClearCurrentPlan(state);
                state.NextAllowedPlanAt = now + blockedCooldown;
                state.Mode = BotMode.Cooldown;
                return false;
            }

            plan.RepathAttempts++;

            if (bot == null || plan == null)
                return false;

            var location = bot.currentLocation;
            var botTile = GetBotTile(bot);

            // Target changed? Don't repath to a bad job.
            if (!IsPlanStillValid(location, plan))
            {
                ModEntry.instance.Monitor.Log(
                    $"Supervisor: cannot repath {bot.name}; target no longer valid: " +
                    $"{plan.JobType} at {plan.TargetTile.X},{plan.TargetTile.Y}",
                    LogLevel.Trace);

                return false;
            }

            var path = FindPath(location, botTile, plan.StandTile);

            if (path == null || path.Count == 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"Supervisor: repath failed for {bot.name}: " +
                    $"from {botTile.X},{botTile.Y} to stand {plan.StandTile.X},{plan.StandTile.Y}",
                    LogLevel.Trace);

                return false;
            }

            ModEntry.instance.Monitor.Log(
                $"Supervisor: repath succeeded for {bot.name}: " +
                $"from {botTile.X},{botTile.Y} to {plan.StandTile.X},{plan.StandTile.Y}, steps={path.Count}",
                LogLevel.Trace);

            state.Mode = BotMode.Running;
            state.NextAllowedPlanAt = now;
            return true;
        }
        private void VerifyPlanResult(BotSupervisorState state, TimeSpan now)
        {
            var bot = state.Bot;
            var plan = state.CurrentPlan;

            if (bot == null || plan == null)
            {
                ClearCurrentPlan(state);
                state.Mode = BotMode.Planning;
                return;
            }

            if (plan.JobType == JobType.ServiceMachine)
            {
                MarkMachineServiceAttempted(bot.currentLocation, plan.TargetTile, now);

                ModEntry.instance.Monitor.Log(
                    $"Supervisor: machine service attempt complete for {bot.name}: " +
                    $"{plan.TargetName} at {plan.TargetTile.X},{plan.TargetTile.Y}",
                    LogLevel.Trace);

                ClearCurrentPlan(state);
                state.Mode = BotMode.Cooldown;
                state.NextAllowedPlanAt = now + TimeSpan.FromSeconds(2);
                return;
            }

            if ((plan.JobType == JobType.TillSoil || plan.JobType == JobType.MineDigFloor) && GetBotTile(bot) != plan.StandTile)
            {
                ModEntry.instance.Monitor.Log(
                    $"Supervisor: {bot.name} drifted from expected stand tile for {plan.JobType}. " +
                    $"bot={GetBotTile(bot).X},{GetBotTile(bot).Y} " +
                    $"expected={plan.StandTile.X},{plan.StandTile.Y}; recalculating path.",
                    LogLevel.Trace);

                if (TryRepathCurrentPlan(state, now))
                    return;

                // If repath fails, then give up/block.
                BlockTarget(bot.currentLocation, plan.TargetTile, now);

                ClearCurrentPlan(state);
                state.NextAllowedPlanAt = now + blockedCooldown;
                state.Mode = BotMode.Cooldown;
                return;
            }

            if (plan.JobType == JobType.ServiceMachine)
            {
                MarkMachineServiceAttempted(bot.currentLocation, plan.TargetTile, now);

                ModEntry.instance.Monitor.Log(
                    $"Supervisor machine service attempt finished for {bot.name}: " +
                    $"{plan.TargetName} at {plan.TargetTile.X},{plan.TargetTile.Y}",
                    LogLevel.Trace);

                ClearCurrentPlan(state);
                state.NextAllowedPlanAt = now + TimeSpan.FromMilliseconds(250);
                state.Mode = BotMode.Cooldown;
                return;
            }

            if (IsJobStillNeeded(state))
            {
                ModEntry.instance.Monitor.Log(
                    $"Supervisor: job still needed after attempt for {bot.name}: {plan.JobType} at {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");

                RecordTargetFailure(bot.currentLocation, plan.TargetTile);
                BlockTarget(bot.currentLocation, plan.TargetTile, now);

                ClearCurrentPlan(state);
                state.NextAllowedPlanAt = now + blockedCooldown;
                state.Mode = BotMode.Cooldown;
                return;
            }

            ModEntry.instance.Monitor.Log(
                $"Supervisor plan completed for {bot.name}: {plan.JobType} target {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");

            ClearCurrentPlan(state);
            state.Mode = BotMode.Planning;
        }

        private List<BotJob> BuildJobsForLocation(GameLocation location, BotSupervisorState state)
        {
            var orders = GetOrdersForBot(state?.Bot);
            if (orders.Mode != BotOrderMode.Work)
            {
                state.LastNoPlanReason = $"Bot mode is {orders.Mode}.";
                return new List<BotJob>();
            }

            var jobs = new List<BotJob>();
            if ((orders.Capabilities & BotCapability.Machines) != 0)
                jobs.AddRange(BuildMachineJobsForLocation(location));

            if (orders.AllowedZones.Count > 0)
            {
                jobs.AddRange(BuildZoneJobsForLocation(location, state, orders));
                return jobs.Where(job => IsJobAllowedByOrders(state, job, orders)).ToList();
            }

            var defaultJobs = location switch
            {
                Farm farm => BuildFarmJobs(farm, state),
                GameLocation greenhouse
                    when greenhouse.NameOrUniqueName == "Greenhouse"
                    => BuildFarmJobs(greenhouse, state),
                MineShaft mine => BuildMineJobs(mine, state),
                Woods woods => BuildClearAllJobs(location, state),
                Mountain mountain => BuildClearAllJobs(location, state),
                Forest forest => BuildClearAllJobs(location, state),
                _ => new List<BotJob>(),
            };

            jobs.AddRange(defaultJobs);
            return jobs.Where(job => IsJobAllowedByOrders(state, job, orders)).ToList();
        }

        private List<BotJob> BuildZoneJobsForLocation(GameLocation location, BotSupervisorState state, BotOrders orders)
        {
            var jobs = new List<BotJob>();
            if (location == null || orders == null)
                return jobs;

            foreach (var zoneName in orders.AllowedZones)
            {
                if (!botZones.TryGetValue(zoneName, out var zone))
                    continue;

                if (!string.Equals(zone.LocationName, location.NameOrUniqueName, StringComparison.Ordinal))
                    continue;

                int maxX = Math.Min(zone.Bounds.Right, location.map.Layers[0].LayerWidth);
                int maxY = Math.Min(zone.Bounds.Bottom, location.map.Layers[0].LayerHeight);
                for (int y = Math.Max(0, zone.Bounds.Top); y < maxY; y++)
                {
                    for (int x = Math.Max(0, zone.Bounds.Left); x < maxX; x++)
                    {
                        var tile = new Vector2(x, y);
                        if (TryGetTileJobForOrders(location, tile, state, orders, out var job))
                            jobs.Add(job);
                    }
                }
            }

            return jobs;
        }

        private bool TryGetTileJobForOrders(GameLocation location, Vector2 tile, BotSupervisorState state, BotOrders orders, out BotJob job)
        {
            job = null;
            if (orders == null)
                return false;

            if ((orders.Capabilities & (BotCapability.Harvest | BotCapability.Water | BotCapability.Plant | BotCapability.Till | BotCapability.Clear | BotCapability.Mine)) != 0
                && TryGetFarmJob(location, tile, state, out var farmJob)
                && IsJobAllowedByOrders(state, farmJob, orders))
            {
                job = farmJob;
                return true;
            }

            if ((orders.Capabilities & (BotCapability.Clear | BotCapability.Mine)) != 0
                && TryGetClearAllJob(location, tile, out string targetName))
            {
                var adjacentTiles = GetAdjacentPassableTiles(location, tile).ToList();
                if (adjacentTiles.Count == 0)
                    return false;

                job = new BotJob
                {
                    Type = targetName == "Stone" ? JobType.MineBreakStone : targetName == "Weeds" ? JobType.MineCutWeeds : JobType.ClearDebris,
                    JobKey = GetJobKey(location, targetName == "Stone" ? JobType.MineBreakStone : targetName == "Weeds" ? JobType.MineCutWeeds : JobType.ClearDebris, tile),
                    TargetName = targetName,
                    TargetTile = tile,
                    AdjacentTile = adjacentTiles.First(),
                    BasePriority = 100,
                };
                return IsJobAllowedByOrders(state, job, orders);
            }

            return false;
        }

        private List<BotJob> BuildMachineJobsForLocation(GameLocation location)
        {
            var jobs = new List<BotJob>();
            if (location == null)
                return jobs;

            TimeSpan timestamp = Game1.currentGameTime?.TotalGameTime ?? new TimeSpan(DateTime.Now.Ticks);
            foreach (var pair in location.objects.Pairs)
            {
                if (TryBuildMachineJob(location, pair.Key, timestamp, out BotJob machineJob))
                    jobs.Add(machineJob);
            }

            return jobs;
        }
        private List<BotJob> BuildClearAllJobs(GameLocation location, BotSupervisorState state)
        {
            var jobs = new List<BotJob>();

            int width = location.map.Layers[0].LayerWidth;
            int height = location.map.Layers[0].LayerHeight;

            for (int y = 1; y < height; y++)
            {
                for (int x = 1; x < width; x++)
                {
                    var tile = new Vector2(x, y);

                    if (TryGetClearAllJob(location, tile, out string targetName))
                    {
                        var adjacentTiles = GetAdjacentPassableTiles(location, tile).ToList();
                        if (adjacentTiles.Count == 0)
                            continue;
                        jobs.Add(new BotJob
                        {
                            Type = JobType.ClearDebris,
                            JobKey = GetJobKey(location, JobType.ClearDebris, tile),
                            TargetName = targetName,
                            TargetTile = tile,
                            AdjacentTile = adjacentTiles.First(),
                            BasePriority = 100,
                        });
                    }
                }
            }

            return jobs;
        }
        private void ReportBotInventory(BotObject bot)
        {
            if (bot == null)
                return;

            ModEntry.instance.Monitor.Log($"Inventory for {bot.name}:", LogLevel.Warn);

            for (int slot = 0; slot < bot.farmer.Items.Count; slot++) // replace Items if needed
            {
                var item = bot.farmer.Items[slot];

                if (item == null)
                {
                    ModEntry.instance.Monitor.Log($"  slot {slot}: empty", LogLevel.Warn);
                    continue;
                }

                ModEntry.instance.Monitor.Log(
                    $"  slot {slot}: qty={item.Stack} " +
                    $"name={item.Name} display={item.DisplayName} " +
                    $"type={item.GetType().FullName} " +
                    $"category={item.Category} " +
                    $"itemId={item.ItemId} qualified={item.QualifiedItemId}",
                    LogLevel.Warn);
            }
        }
        private bool TryGetFarmJob(GameLocation location, Vector2 tile, BotSupervisorState state, out BotJob job){
            job = null;
            var adjacentTiles = GetAdjacentPassableTiles(location, tile).ToList();
            if (adjacentTiles.Count == 0){   
                return false;
            }
            if(IsHarvestableCropTile(location, tile, out string targetName))  {
                job = new BotJob
                {
                    Type = JobType.HarvestCrop,
                    JobKey = GetJobKey(location, JobType.HarvestCrop, tile),
                    TargetName = targetName,
                    TargetTile = tile,
                    AdjacentTile = adjacentTiles.First(), // improve later
                    BasePriority = 120,
                };   
                return true;                   
            }
            if (isClearDeadCropTile(location, tile))  {
                job = new BotJob
                {
                    Type = JobType.ClearDeadCrop,
                    JobKey = GetJobKey(location, JobType.ClearDeadCrop, tile),
                    TargetName = "Dead Crop",
                    TargetTile = tile,
                    AdjacentTile = adjacentTiles.First(), // improve later
                    RequiresScythe = true,
                    BasePriority = 125,
                };   
                return true;                                     
            }
            if(IsPlantCropTile(location, tile, out string targetPlanting, out bool canFertilize))  {
                bool botCanFertilize = (GetOrdersForBot(state?.Bot).Capabilities & BotCapability.Fertilize) != 0;
                job = new BotJob
                {
                    Type = JobType.PlantCrop,
                    JobKey = GetJobKey(location, JobType.PlantCrop, tile),
                    TargetName = targetPlanting,
                    TargetTile = tile,
                    AdjacentTile = adjacentTiles.First(), // improve later
                    CanFertilize = canFertilize && botCanFertilize,
                    BasePriority = 100 + (canFertilize ? 25 : 0),
                };    
                return true;                                     
            }
            if (IsDryCropTile(location, tile))  {
                job = new BotJob
                {
                    Type = JobType.WaterCrop,
                    JobKey = GetJobKey(location, JobType.WaterCrop, tile),
                    TargetName = "Dry Crop",
                    TargetTile = tile,
                    AdjacentTile = adjacentTiles.First(), // improve later
                    BasePriority = 100,
                };     
                return true;                                     
            }      
            if (IsClearableTile(location, tile, out string harvestName)) {
                job = new BotJob
                {
                    Type = JobType.ClearDebris,
                    JobKey = GetJobKey(location, JobType.ClearDebris, tile),
                    TargetName = harvestName,
                    TargetTile = tile,
                    AdjacentTile = adjacentTiles.First(), // improve later
                    BasePriority = 100,
                };   
                return true;                  
            }                 
            if (TryBuildTillSoilJob(location, tile, out BotJob tillSoilJob))
            {
                job = tillSoilJob;  
                return true;                
            };    
            return false;
        }  
        private List<BotJob> BuildMineJobs(MineShaft mine, BotSupervisorState state)
        {
            var jobs = new List<BotJob>();

            int width = mine.map.Layers[0].LayerWidth;
            int height = mine.map.Layers[0].LayerHeight;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var tile = new Vector2(x, y);

                    if (TryGetMineJob(mine, tile, out JobType jobType, out string targetName, out int priority))
                    {
                        var adjacentTiles = GetAdjacentPassableTiles(mine, tile).ToList();
                        if (adjacentTiles.Count == 0)
                            continue;
                        jobs.Add(new BotJob
                        {
                            Type = jobType,
                            JobKey = GetJobKey(mine, jobType, tile),
                            TargetName = targetName,
                            TargetTile = tile,
                            AdjacentTile = adjacentTiles.First(),
                            BasePriority = priority,
                        });
                        continue;
                    }

                    if (TryBuildMineDigFloorJob(mine, tile, out BotJob mineDigJob))
                    {
                        jobs.Add(mineDigJob);
                    }
                }
            }

            return jobs;
        }
        private bool TryBuildMineDigFloorJob(MineShaft mine, Vector2 tile, out BotJob job)
        {
            if (TryBuildTillSoilJob(mine, tile, out job, basePriority: 250, targetName: "Mine Floor"))
            {
                job = new BotJob
                {
                    Type = JobType.MineDigFloor,
                    JobKey = GetJobKey(mine, JobType.MineDigFloor, tile),
                    TargetName = "Mine Floor",
                    TargetTile = job.TargetTile,
                    AdjacentTile = job.AdjacentTile,
                    BasePriority = job.BasePriority,
                };
                return true;
            }

            return false;
        }
        private List<BotJob> BuildFarmJobs(GameLocation location, BotSupervisorState state)
        {
            var jobs = new List<BotJob>();

            int width = location.map.Layers[0].LayerWidth;
            int height = location.map.Layers[0].LayerHeight;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var tile = new Vector2(x, y);
                    if (TryGetFarmJob(location, tile, state, out BotJob farmJob))
                        jobs.Add(farmJob);
                }
            }

            return jobs;
        }
        private static string GetJobKey(GameLocation location, JobType type, Vector2 tile)
        {
            string locationName = location?.NameOrUniqueName ?? "";
            return $"{locationName}:{type}:{(int)tile.X},{(int)tile.Y}";
        }
        private bool IsPlanStillValid(GameLocation location, PendingPlan plan)
        {
            if (location == null || plan == null)
                return false;

            return plan.JobType switch
            {
                JobType.TillSoil => IsStillTillable(location, plan.TargetTile),
                JobType.ClearDebris => IsClearableTile(location, plan.TargetTile, out _)
                    && !IsUnsafePlacedObject(location, plan.TargetTile),
                JobType.WaterCrop => IsDryCropTile(location, plan.TargetTile),
                JobType.HarvestCrop => IsHarvestableCropTile(location, plan.TargetTile, out _),
                JobType.PlantCrop => IsPlantCropTile(location, plan.TargetTile, out _, out _),
                JobType.ClearDeadCrop => isClearDeadCropTile(location, plan.TargetTile),
                JobType.ServiceMachine => IsExpectedMachine(location, plan.TargetTile, plan.TargetName),
                JobType.MineBreakStone => IsNamedObjectTile(location, plan.TargetTile, "Stone"),
                JobType.MineCutWeeds => IsNamedObjectTile(location, plan.TargetTile, "Weeds"),
                JobType.MineDigFloor => IsStillTillable(location, plan.TargetTile),
                _ => false,
            };
        }

        private bool ValidatePlanForExecution(BotSupervisorState state, out string reason)
        {
            reason = null;

            var bot = state?.Bot;
            var plan = state?.CurrentPlan;

            if (bot == null)
            {
                reason = "bot is null";
                return false;
            }

            if (plan == null)
            {
                reason = "plan is null";
                return false;
            }

            var location = bot.currentLocation;
            if (location == null)
            {
                reason = "bot location is null";
                return false;
            }

            if (!string.Equals(location.NameOrUniqueName, plan.LocationName, StringComparison.Ordinal))
            {
                reason = $"location changed from {plan.LocationName} to {location.NameOrUniqueName}";
                return false;
            }

            if (bot.TileLocation != plan.StartTile)
            {
                reason = $"bot moved before script start: expected {plan.StartTile.X},{plan.StartTile.Y}; got {bot.TileLocation.X},{bot.TileLocation.Y}";
                return false;
            }

            if (!IsPlanStillValid(location, plan))
            {
                reason = "target is no longer valid for job";
                return false;
            }

            var pseudoJob = new BotJob
            {
                Type = plan.JobType,
                TargetTile = plan.TargetTile,
                AdjacentTile = plan.StandTile,
                TargetName = plan.TargetName,
            };
            reason = GetJobReservationRejectionReason(state, pseudoJob, location);
            if (reason != null)
                return false;

            return true;
        }

        private void LogPlanToolSafety(BotObject bot, PendingPlan plan, bool allowed, string reason)
        {
            if (bot == null || plan == null)
                return;

            var location = bot.currentLocation;
            ValMap info = location == null ? null : TileInfo.GetInfo(location, plan.TargetTile);

            string type = info?.GetString("type") ?? "(null)";
            string name = info?.GetString("name") ?? "(null)";
            string crop = "(none)";

            if (info != null && info.map.TryGetValue(new ValString("crop"), out Value cropValue) && cropValue is ValMap cropInfo)
                crop = cropInfo.GetString("name") ?? "(crop)";

            ModEntry.instance.Monitor.Log(
                $"Tool safety {(allowed ? "allowed" : "refused")}: bot={bot.name}; " +
                $"loc={location?.NameOrUniqueName ?? "(null)"}; botTile={bot.TileLocation.X},{bot.TileLocation.Y}; " +
                $"facing={bot.facingDirection}; target={plan.TargetTile.X},{plan.TargetTile.Y}; " +
                $"job={plan.JobType}; targetType={type}; targetName={name}; crop={crop}; reason={reason}",
                allowed ? LogLevel.Trace : LogLevel.Warn);
        }

        private void TryCreatePlanForBot(BotSupervisorState state, TimeSpan now)
        {
            var bot = state.Bot;

            if (bot == null)
            {
                state.Mode = BotMode.Paused;
                state.LastNoPlanReason = "Bot is null.";
                return;
            }

            var location = bot.currentLocation;
            state.LastNoPlanReason = null;

            var allJobs = BuildJobsForLocation(bot.currentLocation, state).ToList();
            if (allJobs.Count == 0 && string.IsNullOrWhiteSpace(state.LastNoPlanReason))
                state.LastNoPlanReason = $"No jobs generated for capabilities/zones in {location?.NameOrUniqueName ?? "(null)"}.";

            var jobs = new List<BotJob>();

            foreach (var job in allJobs)
            {
                    
                var score = ScoreJobForBot(state, job);

                if (IsTargetIgnored(location, job.TargetTile))
                {
                    LogRejectedJob(job, "target ignored", score);
                    continue;
                }

                if (IsTargetBlocked(bot.currentLocation, job.TargetTile, now))
                {
                    LogRejectedJob(job, "target blocked", score);
                    continue;
                }

                if (IsJobSuppressed(job, now))
                {
                    LogRejectedJob(job, "job suppressed", score);
                    continue;
                }

                string reservationReason = GetJobReservationRejectionReason(state, job, location);
                if (reservationReason != null)
                {
                    ModEntry.instance.Monitor.Log(
                        $"Rejecting {job.Type} at {location?.NameOrUniqueName ?? "(null)"} {(int)job.TargetTile.X},{(int)job.TargetTile.Y} for {bot.name}: {reservationReason}.",
                        LogLevel.Trace);
                    LogRejectedJob(job, reservationReason, score);
                    continue;
                }

                if (!BotCanDoJob(state, job))
                {
                    LogRejectedJob(job, "bot cannot do job", score);
                    continue;
                }

                jobs.Add(job);
            }

            jobs = jobs
                .OrderByDescending(job => ScoreJobForBot(state, job))
                .ToList();

            if (allJobs.Count > 0 && jobs.Count == 0)
                state.LastNoPlanReason = "All generated jobs were rejected by orders, reservations, tools, inventory, or safety checks.";

			foreach (var job in jobs)
            {
                ModEntry.instance.Monitor.Log(
                    $"job name {job.Type}: target tile {job.TargetTile.X},{job.TargetTile.Y} name: {job.TargetName}");

                var path = FindPath(location, bot.TileLocation, job.AdjacentTile);
                if (path == null)
                {
                    ModEntry.instance.Monitor.Log(
                        $"No path for {bot.name}: {job.Type} target {job.TargetTile.X},{job.TargetTile.Y} " +
                        $"adjacent {job.AdjacentTile.X},{job.AdjacentTile.Y} from {bot.TileLocation.X},{bot.TileLocation.Y}",
                        LogLevel.Trace);

                    BlockTarget(location, job.TargetTile, now);
                    continue;
                }

                var script = BuildScript(
                    bot.TileLocation,
                    bot.facingDirection,
                    path,
                    job);

                if (string.IsNullOrWhiteSpace(script))
                {
                    ModEntry.instance.Monitor.Log(
                        $"No script for {bot.name}: {job.Type} target {job.TargetTile.X},{job.TargetTile.Y} " +
                        $"adjacent {job.AdjacentTile.X},{job.AdjacentTile.Y}",
                        LogLevel.Trace);

                    BlockTarget(location, job.TargetTile, now);
                    continue;
                }

                bot.InitShell();

                state.CurrentPlan = new PendingPlan
                {
                    JobType = job.Type,
                    JobKey = job.JobKey,
                    PlanId = Guid.NewGuid().ToString("N"),
                    LocationName = location.NameOrUniqueName,
                    TargetTile = job.TargetTile,
                    StandTile = job.AdjacentTile,
                    TargetName = job.TargetName,
                    StartTile = bot.TileLocation,
                    Script = script,
                    QueuedAt = null,
                };

                if (!TryReservePlanForBot(state, state.CurrentPlan, now, out string reserveReason))
                {
                    ModEntry.instance.Monitor.Log(
                        $"Rejecting {job.Type} at {location?.NameOrUniqueName ?? "(null)"} {(int)job.TargetTile.X},{(int)job.TargetTile.Y} for {bot.name}: {reserveReason}.",
                        LogLevel.Trace);
                    ClearCurrentPlan(state);
                    continue;
                }

                state.Mode = BotMode.Queued;
                state.LastNoPlanReason = null;

                ModEntry.instance.Monitor.Log(
                    $"Supervisor created plan for {bot.name}: target tile {job.TargetTile.X},{job.TargetTile.Y} name {job.TargetName}");

                return;
            }

            ClearCurrentPlan(state);
            state.Mode = BotMode.Idle;
            if (string.IsNullOrWhiteSpace(state.LastNoPlanReason))
                state.LastNoPlanReason = "No reachable target found.";

            ModEntry.instance.Monitor.Log(
                $"Supervisor has no plan for {bot.name}: {state.LastNoPlanReason}");
        }
        private void LogRejectedJob(BotJob job, string reason, double score)
        {
            var prefix = job.Type == JobType.ServiceMachine
                ? "MACHINE JOB REJECTED"
                : "Rejected job";

            /*ModEntry.instance.Monitor.Log(
                $"{prefix}: {job.GetType().Name} target={job.TargetTile.X},{job.TargetTile.Y} " +
                $"name={job.TargetName} score={score} reason={reason}",
                LogLevel.Trace);*/
        }
        private bool BotCanDoJob(BotSupervisorState state, BotJob job)
        {
            var bot = state.Bot;
            var orders = GetOrdersForBot(bot);

            if (!IsJobAllowedByOrders(state, job, orders))
                return false;

            if (job.Type == JobType.ServiceMachine)
            {
                return BotHasMachineInput(bot, job);
            }

            EnsureToolsForOrders(bot, orders);

            if (job.Type == JobType.PlantCrop && !BotHasSeasonSafeSeed(bot))
                return false;

            return job.Type switch
            {
                JobType.HarvestCrop => BotHasToolType(bot, "Scythe") || BotHasInventoryCategory(bot, "Crop"),
                JobType.ClearDeadCrop => BotHasToolType(bot, "Scythe"),
                JobType.PlantCrop => BotHasInventoryCategory(bot, "Seed"),
                JobType.WaterCrop => BotHasToolType(bot, "WateringCan"),
                JobType.TillSoil => BotHasToolType(bot, "Hoe"),
                JobType.MineDigFloor => BotHasToolType(bot, "Hoe"),
                JobType.MineBreakStone => BotHasToolType(bot, "Pickaxe"),
                _ => true,
            };
        }

        private bool IsJobAllowedByOrders(BotSupervisorState state, BotJob job, BotOrders orders)
        {
            if (job == null || orders == null || orders.Mode != BotOrderMode.Work)
                return false;

            if (!IsJobInsideAllowedZones(state?.Bot?.currentLocation, job, orders))
                return false;

            return job.Type switch
            {
                JobType.HarvestCrop => (orders.Capabilities & BotCapability.Harvest) != 0,
                JobType.WaterCrop => (orders.Capabilities & BotCapability.Water) != 0,
                JobType.PlantCrop => (orders.Capabilities & BotCapability.Plant) != 0,
                JobType.ClearDeadCrop => (orders.Capabilities & BotCapability.Clear) != 0,
                JobType.ClearDebris => (orders.Capabilities & BotCapability.Clear) != 0,
                JobType.TillSoil => (orders.Capabilities & BotCapability.Till) != 0
                    || (state?.Bot?.currentLocation is MineShaft && (orders.Capabilities & BotCapability.Mine) != 0),
                JobType.MineBreakStone => (orders.Capabilities & BotCapability.Mine) != 0,
                JobType.MineCutWeeds => (orders.Capabilities & (BotCapability.Mine | BotCapability.Clear)) != 0,
                JobType.MineDigFloor => (orders.Capabilities & BotCapability.Mine) != 0,
                JobType.ServiceMachine => IsMachineAllowedByOrders(job.TargetName, orders),
                _ => false,
            };
        }

        private bool IsJobInsideAllowedZones(GameLocation location, BotJob job, BotOrders orders)
        {
            if (orders.AllowedZones.Count == 0)
                return true;

            if (location == null)
                return false;

            return orders.AllowedZones
                .Select(zoneName => botZones.TryGetValue(zoneName, out var zone) ? zone : null)
                .Where(zone => zone != null && string.Equals(zone.LocationName, location.NameOrUniqueName, StringComparison.Ordinal))
                .Any(zone => zone.Bounds.Contains((int)job.TargetTile.X, (int)job.TargetTile.Y));
        }

        private static bool IsMachineAllowedByOrders(string machineName, BotOrders orders)
        {
            if ((orders.Capabilities & BotCapability.Machines) == 0)
                return false;

            if (orders.AllowedMachines.Count > 0)
                return orders.AllowedMachines.Contains(machineName);

            bool hasSpecificMachineCapabilities =
                (orders.Capabilities & (BotCapability.Kegs | BotCapability.Jars | BotCapability.SeedMakers | BotCapability.Furnaces)) != 0;
            if (!hasSpecificMachineCapabilities)
                return true;

            return machineName switch
            {
                "Keg" => (orders.Capabilities & BotCapability.Kegs) != 0,
                "Preserves Jar" => (orders.Capabilities & BotCapability.Jars) != 0,
                "Seed Maker" => (orders.Capabilities & BotCapability.SeedMakers) != 0,
                "Furnace" => (orders.Capabilities & BotCapability.Furnaces) != 0,
                "Charcoal Kiln" => (orders.Capabilities & BotCapability.Furnaces) != 0,
                _ => false,
            };
        }

        private bool BotHasMachineInput(BotObject bot, BotJob job)
        {
            if (!IsMachineAllowedByOrders(job.TargetName, GetOrdersForBot(bot)))
                return false;

            if (job.InputRules == null || job.InputRules.Count == 0)
                return true;

            foreach (var input in job.InputRules)
            {
                if (!string.IsNullOrWhiteSpace(input.Name) &&
                    BotHasInventoryName(bot, input.Name))
                    return true;

                if (!string.IsNullOrWhiteSpace(input.Category) &&
                    BotHasInventoryCategory(bot, input.Category))
                    return true;

                if (!string.IsNullOrWhiteSpace(input.EndsWith) &&
                    BotHasInventoryNameEndingWith(bot, input.EndsWith))
                    return true;
            }
            return false;
        }

        private bool BotHasInventoryName(BotObject bot, string name)
        {
            if (bot?.farmer == null)
                return false;

            foreach (Item item in bot.farmer.Items)
            {
                if (item == null) continue;

                // Option A: real Stardew item properties
                if (item.getCategoryName() == name || item.Name == name)
                    return true;

                // Option B: Farmtronics ItemInfo if easier
                // ValMap info = ItemInfo.GetInfo(item);
                // if (info.GetString("category") == category) return true;
            }

            return false;
        }

        private bool BotHasInventoryNameEndingWith(BotObject bot, string suffix)
        {
            if (bot?.farmer == null)
                return false;

            foreach (Item item in bot.farmer.Items)
            {
                if (item == null) continue;

                if ((item.Name?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.DisplayName?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ?? false))
                    return true;
            }

            return false;
        }

        private bool BotHasInventoryCategory(BotObject bot, string category)
        {
            if (bot?.farmer == null)
                return false;

            foreach (Item item in bot.farmer.Items)
            {
                if (item == null) continue;

                // Option A: real Stardew item properties
                if (item.getCategoryName() == category)
                    return true;

                // Option B: Farmtronics ItemInfo if easier
                // ValMap info = ItemInfo.GetInfo(item);
                // if (info.GetString("category") == category) return true;
            }

            return false;
        }

        private bool BotHasSeasonSafeSeed(BotObject bot)
        {
            if (bot?.farmer == null)
                return false;

            bool foundSeed = false;
            foreach (Item item in bot.farmer.Items)
            {
                if (item == null || item.getCategoryName() != "Seed")
                    continue;

                foundSeed = true;
                if (!CanSeedMatureThisSeason(item))
                    return false;
            }

            return foundSeed;
        }

        private bool CanSeedMatureThisSeason(Item item)
        {
            if (item == null)
                return false;

            try
            {
                var crops = Game1.content.Load<Dictionary<string, StardewValley.GameData.Crops.CropData>>("Data/Crops");
                string seedId = item.ItemId;
                if (string.IsNullOrWhiteSpace(seedId) || !crops.TryGetValue(seedId, out var cropData) || cropData == null)
                    return false;

                if (cropData.Seasons != null
                    && cropData.Seasons.Count > 0
                    && !cropData.Seasons.Any(season => string.Equals(season.ToString(), Game1.currentSeason, StringComparison.OrdinalIgnoreCase)))
                    return false;

                int daysToMature = cropData.DaysInPhase?.Sum() ?? int.MaxValue;
                int daysRemaining = 28 - Game1.dayOfMonth + 1;
                return daysToMature <= daysRemaining;
            }
            catch (Exception ex)
            {
                LogOncePerInterval(
                    "crop-data-read-failed",
                    $"Planting safety: could not read crop data; refusing unknown seed maturity. {ex.Message}",
                    LogLevel.Warn,
                    TimeSpan.FromSeconds(30));
                return false;
            }
        }

        private bool BotHasToolType(BotObject bot, string toolType)
        {
            if (bot?.farmer == null)
                return false;

            foreach (Item item in bot.farmer.Items)
            {
                if (item == null)
                    continue;

                if (toolType == "WateringCan" && item is WateringCan)
                    return true;

                if (toolType == "Pickaxe" && item is Pickaxe)
                    return true;

                if (toolType == "Hoe" && item is Hoe)
                    return true;

                if (toolType == "Axe" && item is Axe)
                    return true;

                // Scythe is weird; in Stardew it may behave more like a weapon.
                // For now, your slot-4 convention is probably better than type-detecting it.
                if (toolType == "Scythe" && item.Name.Contains("Scythe"))
                    return true;
            }

            return false;
        }
		private bool IsClearableTile(GameLocation location, Vector2 tile, out string targetName) {
			targetName = null;
			ValMap info = TileInfo.GetInfo(location, tile);
			if (info == null) return false;

			string typeName = info.GetString("type");
			string objectName = info.GetString("name");
			string match = clearTypes.FirstOrDefault(name => name == typeName || name == objectName);
			if (match == null) return false;

			targetName = match;
			return true;
		}

        private static bool IsUnsafePlacedObject(GameLocation location, Vector2 tile)
        {
            if (location == null)
                return true;

            if (!location.objects.TryGetValue(tile, out StardewValley.Object obj) || obj == null)
                return false;

            if (obj is Chest || obj is BotObject)
                return true;

            if (obj.bigCraftable.Value)
                return true;

            return false;
        }

        private static bool IsExpectedMachine(GameLocation location, Vector2 tile, string expectedName)
        {
            if (location == null || string.IsNullOrWhiteSpace(expectedName))
                return false;

            if (!location.objects.TryGetValue(tile, out StardewValley.Object obj) || obj == null)
                return false;

            if (obj is Chest || obj is BotObject)
                return false;

            return obj.Name == expectedName && obj.bigCraftable.Value;
        }

        private static bool IsNamedObjectTile(GameLocation location, Vector2 tile, string expectedName)
        {
            if (location == null || string.IsNullOrWhiteSpace(expectedName))
                return false;

            if (!location.objects.TryGetValue(tile, out StardewValley.Object obj) || obj == null)
                return false;

            if (obj is Chest || obj is BotObject || obj.bigCraftable.Value)
                return false;

            return obj.Name == expectedName;
        }

        static bool IsDryCropTile(GameLocation location, Vector2 tile) {
			ValMap info = TileInfo.GetInfo(location, tile);
			if (info == null) return false;
            bool isDryCrop = (info.GetString("dry") == "1") && (info.GetString("crop") != null);
            if (isDryCrop) {
                return true;
            }
            return false;
		}
        private bool IsJobSuppressed(BotJob job, TimeSpan now)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.JobKey))
                return false;

            if (!jobMemory.TryGetValue(job.JobKey, out var memory))
                return false;

            if (memory.IgnoreForRun)
                return true;

            if (now < memory.SuppressedUntil)
                return true;

            return false;
        }
        private sealed class MachineRule
        {
            public string MachineName { get; init; }
            public JobType JobType { get; init; } = JobType.ServiceMachine;
            public int BasePriority { get; init; }

            public List<MachineInputRule> InputRules { get; init; } = new();
        }

        private sealed class MachineInputRule
        {
            public string Category { get; init; }
            public string Name { get; init; }
            public string EndsWith { get; init; }
        }

        private static readonly List<MachineRule> machineRules = new()
        {
            new()
            {
                MachineName = "Cheese Press",
                BasePriority = 750,
                InputRules =
                {
                    new MachineInputRule { Name = "Milk" },
                    new MachineInputRule { Name = "Goat Milk" },
                    new MachineInputRule { Name = "Large Milk" },
                    new MachineInputRule { Name = "L. Goat Milk" },
                }
            },
            new()
            {
                MachineName = "Keg",
                BasePriority = 750,
                InputRules =
                {
                    new MachineInputRule { Name = "Coffee Bean" },
                    new MachineInputRule { Category = "Fruit" },
                    new MachineInputRule { Name = "Hops" },
                    new MachineInputRule { Name = "Wheat" },
                }
            },
            new()
            {
                MachineName = "Preserves Jar",
                BasePriority = 650,
                InputRules =
                {
                    new MachineInputRule { Category = "Fruit" },
                    new MachineInputRule { Category = "Vegetable" },
                }
            },
            new()
            {
                MachineName = "Furnace",
                BasePriority = 600,
                InputRules =
                {
                    new MachineInputRule { Name = "Copper Ore" },
                    new MachineInputRule { Name = "Iron Ore" },
                    new MachineInputRule { Name = "Gold Ore" },
                    new MachineInputRule { Name = "Iridium Ore" },      
                }
            },
            new()
            {
                MachineName = "Seed Maker",
                BasePriority = 550,
                InputRules =
                {
                    new MachineInputRule { Category = "Vegetable" },
                    new MachineInputRule { Category = "Fruit" },
                }
            },
            new()
            {
                MachineName = "Loom",
                BasePriority = 550,
                InputRules =
                {
                    new MachineInputRule { Name = "Wool" },
                }
            },
            new()
            {
                MachineName = "Oil Maker",
                BasePriority = 550,
                InputRules =
                {
                    new MachineInputRule { Name = "Truffle" },
                    new MachineInputRule { Name = "Sunflower" },
                    new MachineInputRule { Name = "Sunflower Seeds" },
                    new MachineInputRule { Name = "Corn" },
                }
            },
            new()
            {
                MachineName = "Crystalarium",
                BasePriority = 550,
                InputRules =
                {
                    new MachineInputRule { Category = "Gem" },
                    new MachineInputRule { Category = "Mineral" },
                    new MachineInputRule { Category = "Minerals" },
                }
            },
            new()
            {
                MachineName = "Charcoal Kiln",
                BasePriority = 500,
                InputRules =
                {
                    new MachineInputRule { Name = "Wood" },
                }
            },
            new()
            {
                MachineName = "Mayonnaise Machine",
                BasePriority = 650,
                InputRules =
                {
                    new MachineInputRule { Name = "Egg" },
                    new MachineInputRule { Name = "Large Egg" },
                    new MachineInputRule { Name = "Duck Egg" },
                    new MachineInputRule { Name = "Void Egg" },
                    new MachineInputRule { Name = "Dinosaur Egg" },
                    new MachineInputRule { Name = "Ostrich Egg" },
                    new MachineInputRule { Name = "Golden Egg" },
                }
            },
        };
        private bool IsMachineSuppressed(Vector2 tile, StardewValley.Object obj, TimeSpan now)
        {
            if (!_machineServiceMemory.TryGetValue(tile, out var memory))
                return false;

            var current = Fingerprint(obj);

            // If the machine changed state, let it be considered again.
            if (!memory.Fingerprint.Equals(current))
                return false;

            // If it is still the same state and still cooling down, skip it.
            return now < memory.SuppressedUntil;
        }
        private void FailJobAndMoveOn(BotSupervisorState state, TimeSpan now, string reason, TimeSpan? cooldown = null)
        {
            var plan = state.CurrentPlan;

            if (plan == null)
            {
                state.Mode = BotMode.Planning;
                return;
            }

            RecordJobFailure(plan.JobKey, reason, now, cooldown);

            ModEntry.instance.Monitor.Log(
                $"Supervisor job failed for {state.BotName}: {plan.JobType} at {(int)plan.TargetTile.X},{(int)plan.TargetTile.Y} name {plan.TargetName}; reason: {reason}");

            ClearCurrentPlan(state);
            state.Mode = BotMode.Planning;
        }
        private static bool IsReadyMachine(StardewValley.Object obj)
        {
            return obj.heldObject.Value != null
                && obj.readyForHarvest.Value;
        }

        private static bool IsBusyMachine(StardewValley.Object obj)
        {
            return obj.heldObject.Value != null
                && obj.MinutesUntilReady > 0
                && !obj.readyForHarvest.Value;
        }
        private bool TryBuildTillSoilJob(GameLocation location, Vector2 tile, out BotJob job, int basePriority = 1, string targetName = "Empty for Tilling")
        {
            job = null;

            if (location.objects.TryGetValue(tile, out StardewValley.Object obj))
                return false;

            // Already has terrain feature.
            if (location.terrainFeatures.TryGetValue(tile, out var feature))
            {
                // HoeDirt means already tilled.
                if (feature is StardewValley.TerrainFeatures.HoeDirt)
                    return false;

                // Anything else means blocked for now: tree, grass, etc.
                return false;
            }

            var diggable = location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back");

            if (diggable == null)
                return false;

            var adjacentTiles = GetAdjacentPassableTiles(location, tile).ToList();
            if (adjacentTiles.Count == 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"Tilling job rejected: no adjacent passable tile from {(int)tile.X},{(int)tile.Y}");
                return false;
            }

            job = new BotJob
            {
                Type = JobType.TillSoil,
                JobKey = GetJobKey(location, JobType.TillSoil, tile),
                TargetName = targetName,
                TargetTile = tile,
                AdjacentTile = adjacentTiles.First(),
                BasePriority = basePriority,
            };

            return true;
        }
        private bool TryBuildMachineJob(GameLocation location, Vector2 tile, TimeSpan now, out BotJob job)
        {
            job = null;

            if (!location.objects.TryGetValue(tile, out StardewValley.Object obj))
                return false;

            // 1. Always allow ready machines through.
            // They have output waiting and should be harvested.
            if (IsReadyMachine(obj))
            {
                return TryCreateMachineJobFromRule(location, tile, obj, out job);
            }

            // 2. Skip machines that are clearly busy/running.
            if (IsBusyMachine(obj))
            {
                /*ModEntry.instance.Monitor.Log(
                    $"Machine busy, skipping: {obj.Name} at {tile.X},{tile.Y}, " +
                    $"held={obj.heldObject.Value?.Name ?? "null"}, " +
                    $"minutes={obj.MinutesUntilReady}, ready={obj.readyForHarvest.Value}",
                    LogLevel.Trace);*/

                return false;
            }

            // 3. For idle/empty machines, obey cooldown/fingerprint suppression.
            if (IsMachineSuppressed(tile, obj, now))
            {
                ModEntry.instance.Monitor.Log(
                    $"Machine suppressed, skipping: {obj.Name} at {tile.X},{tile.Y}, " +
                    $"held={obj.heldObject.Value?.Name ?? "null"}, " +
                    $"minutes={obj.MinutesUntilReady}, ready={obj.readyForHarvest.Value}",
                    LogLevel.Trace);

                return false;
            }

            return TryCreateMachineJobFromRule(location, tile, obj, out job);
        }
        private bool TryCreateMachineJobFromRule(GameLocation location, Vector2 tile, StardewValley.Object obj, out BotJob job)
        {
            job = null;

            if (IsBusyMachine(obj))
            {
                ModEntry.instance.Monitor.Log(
                    $"Machine busy, skipping job: {obj.Name} at {tile.X},{tile.Y}, " +
                    $"held={obj.heldObject.Value?.Name ?? "null"}, " +
                    $"minutes={obj.MinutesUntilReady}, " +
                    $"ready={obj.readyForHarvest.Value}",
                    LogLevel.Trace);

                return false;
            }

            if (!ShouldCreateServiceMachineJob(tile, obj))
            {
                ModEntry.instance.Monitor.Log(
                    $"Machine already serviced in same state, skipping job: {obj.Name} at {tile.X},{tile.Y}",
                    LogLevel.Trace);

                return false;
            }

            ValMap info = TileInfo.GetInfo(location, tile);

            if (info == null)
                return false;

            string type = info.GetString("type");
            string name = info.GetString("name");

            if (string.IsNullOrWhiteSpace(name))
                return false;

            var rule = machineRules.FirstOrDefault(r => r.MachineName == name);
            if (rule == null)
            {
                return false;
            }

            var adjacentTiles = GetAdjacentPassableTiles(location, tile).ToList();
            if (adjacentTiles.Count == 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"Machine job rejected: no adjacent passable tile for {name} at {(int)tile.X},{(int)tile.Y}");
                return false;
            }

            var adjacentTile = adjacentTiles.First();

            job = new BotJob
            {
                Type = JobType.ServiceMachine,
                JobKey = GetJobKey(location, JobType.ServiceMachine, tile),
                TargetName = name,
                TargetTile = tile,
                AdjacentTile = adjacentTile,
                BasePriority = rule.BasePriority,
                InputRules = rule.InputRules,
            };

            return true;
        }
        private bool TryGetClearAllJob(
            GameLocation location,
            Vector2 tile,
            out string targetName)
        {
            targetName = null;
            if (IsClearableTile(location, tile, out string resourceName)) {
                targetName = resourceName;
                return true;
            }
            return false;
        }
        private bool TryGetMineJob(
            MineShaft mine,
            Vector2 tile,
            out JobType jobType,
            out string targetName,
            out int priority)
        {
            jobType = default;
            targetName = null;
            priority = 0;

            ValMap info = TileInfo.GetInfo(mine, tile);
            if (info == null)
                return false;

            string typeName = info.GetString("type");
            string objectName = info.GetString("name");

            // Do not fight monsters yet. Just detect/report later.
            if (typeName == "Character" && info.GetBool("isMonster"))
                return false;

            if (typeName != "Object")
                return false;

            if (objectName == "Stone")
            {
                jobType = JobType.MineBreakStone;
                targetName = "Stone";
                priority = 600;
                return true;
            }

            if (objectName == "Weeds")
            {
                jobType = JobType.MineCutWeeds;
                targetName = "Weeds";
                priority = 300;
                return true;
            }

            return false;
        }
        private bool isClearDeadCropTile(GameLocation location, Vector2 tile) {
            ValMap info = TileInfo.GetInfo(location, tile);
      	    if (info == null) return false;

            if (!info.map.TryGetValue(new ValString("crop"), out Value cropValue))
                return false;

            if (cropValue == null || cropValue is ValNull)
                return false;

            if (cropValue is not ValMap crop)
                return false;

            return crop.GetBool("dead");
        }   
        private bool IsHarvestableCropTile(GameLocation location, Vector2 tile, out string harvestName)
        {
            harvestName = null;

            ValMap info = TileInfo.GetInfo(location, tile);
            if (info == null) return false;

            if (!info.map.TryGetValue(new ValString("crop"), out Value cropValue))
                return false;

            if (cropValue == null || cropValue is ValNull)
                return false;

            if (cropValue is not ValMap crop)
                return false;

            if (crop.GetBool("dead"))
                return false;

            if (!crop.GetBool("harvestable"))
                return false;

            harvestName = info.GetString("name") ?? "Harvestable Crop";
            return true;
        }

        private bool IsPlantCropTile(GameLocation location, Vector2 tile, out string plantName, out bool canFertilize)
        {
            plantName = null;
            canFertilize = false;

			ValMap info = TileInfo.GetInfo(location, tile);
			if (info == null) return false;
            info.map.TryGetValue(new ValString("type"), out Value typeValue);
            if (typeValue == null || typeValue is ValNull || typeValue.ToString() != "HoeDirt") return false;

            info.map.TryGetValue(new ValString("crop"), out Value cropValue);

            if (cropValue != null && cropValue is not ValNull)
                return false;

            bool hasFertilizer = info.GetBool("hasFertilizer");
            bool isTilled = info.GetBool("tilled");

            canFertilize = !hasFertilizer && isTilled;

            plantName = "Plantable Crop";

            return true;
        }
        private bool IsJobStillNeeded(BotSupervisorState state)
        {
            var plan = state.CurrentPlan;
            var bot = state.Bot;
            if (plan == null) return false;

            if (plan.JobType == JobType.ServiceMachine)
                {
                    MarkMachineServiced(bot.TileLocation, bot.currentLocation as Farm );
                    ClearCurrentPlan(state);
                    state.Mode = BotMode.Planning;
                    return false;
                }
                
            return plan.JobType switch
            {
                JobType.TillSoil => IsStillTillable(bot.currentLocation, plan.TargetTile),
                JobType.MineDigFloor => IsStillTillable(bot.currentLocation, plan.TargetTile),
                JobType.ClearDebris => IsClearableTile(bot.currentLocation, plan.TargetTile, out _),
                JobType.WaterCrop => IsDryCropTile(bot.currentLocation, plan.TargetTile),
                JobType.HarvestCrop => IsHarvestableCropTile(bot.currentLocation, plan.TargetTile, out _),
                JobType.PlantCrop => IsPlantCropTile(bot.currentLocation, plan.TargetTile, out _, out  _),
                JobType.ClearDeadCrop => isClearDeadCropTile(bot.currentLocation, plan.TargetTile), 
                _ => false,
            };
        }
        private void RecordJobFailure(string jobKey, string reason, TimeSpan now, TimeSpan? cooldown = null)
        {
            if (string.IsNullOrWhiteSpace(jobKey))
                return;

            if (!jobMemory.TryGetValue(jobKey, out var memory))
            {
                memory = new JobMemory();
                jobMemory[jobKey] = memory;
            }

            memory.FailureCount++;
            memory.LastReason = reason;

            if (memory.FailureCount >= maxJobFailuresBeforeIgnore)
            {
                memory.IgnoreForRun = true;
                memory.SuppressedUntil = TimeSpan.MaxValue;

                ModEntry.instance.Monitor.Log(
                    $"Supervisor ignoring job for this run after {memory.FailureCount} failures: {jobKey}; reason: {reason}");
                return;
            }

            TimeSpan actualCooldown = cooldown ?? (
                memory.FailureCount == 1
                    ? shortJobCooldown
                    : mediumJobCooldown
            );

            memory.SuppressedUntil = now + actualCooldown;

            ModEntry.instance.Monitor.Log(
                $"Supervisor suppressing job for {actualCooldown.TotalSeconds:0}s after failure {memory.FailureCount}: {jobKey}; reason: {reason}");
        }
        private static bool IsSmeltableOre(Item item)
        {
            return item is StardewValley.Object obj
                && (
                    obj.Name == "Copper Ore" ||
                    obj.Name == "Iron Ore" ||
                    obj.Name == "Gold Ore" ||
                    obj.Name == "Iridium Ore"
                );
        }
        private static int ManhattanDistance(Vector2 a, Vector2 b)
        {
            return Math.Abs((int)a.X - (int)b.X) + Math.Abs((int)a.Y - (int)b.Y);
        }
        static int ScoreJobForBot(BotSupervisorState state, BotJob job)
        {
            double score = job.BasePriority;

            int distance = ManhattanDistance(state.Bot.TileLocation, job.AdjacentTile);
            return (int)(score - distance * 10);
        }
        private bool IsStillTillable(GameLocation location, Vector2 tile)
        {
            if (location.objects.ContainsKey(tile))
                return false;

            if (location.terrainFeatures.TryGetValue(tile, out var feature))
                return false; // includes HoeDirt, grass, trees, etc.

            var diggable = location.doesTileHaveProperty(
                (int)tile.X,
                (int)tile.Y,
                "Diggable",
                "Back");

                    ModEntry.instance.Monitor.Log(
                    $"Return from 1454 doesTileHaveProperty: _{diggable}_ at {tile.X},{tile.Y}",
                    LogLevel.Trace);

            return diggable == "T";
        }
		private IEnumerable<Vector2> GetAdjacentPassableTiles(GameLocation location, Vector2 tile) {
			var candidates = new[] {
				tile + new Vector2(0, -1),
				tile + new Vector2(1, 0),
				tile + new Vector2(0, 1),
				tile + new Vector2(-1, 0),
			};
            foreach (var adjacentTile in candidates)
            {
                if (!IsWithinMap(location, adjacentTile)) continue;
                if (IsSupervisorPassable(location, adjacentTile)) yield return adjacentTile;
            }
		}

		private List<Vector2> FindPath(GameLocation location, Vector2 start, Vector2 goal) {
			var queue = new Queue<Vector2>();
			var cameFrom = new Dictionary<Vector2, Vector2?>();
			queue.Enqueue(start);
			cameFrom[start] = null;

			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current == goal) break;

				foreach (var next in GetNeighbors(current, location)) {
					if (cameFrom.ContainsKey(next)) continue;
					cameFrom[next] = current;
					queue.Enqueue(next);
				}
			}

			if (!cameFrom.ContainsKey(goal)) return null;

			var path = new List<Vector2>();
			Vector2? cursor = goal;
			while (cursor.HasValue && cursor.Value != start) {
				path.Add(cursor.Value);
				cursor = cameFrom[cursor.Value];
			}
			path.Reverse();
			return path;
		}

		private IEnumerable<Vector2> GetNeighbors(Vector2 tile, GameLocation location) {
			var candidates = new[] {
				tile + new Vector2(0, -1),
				tile + new Vector2(1, 0),
				tile + new Vector2(0, 1),
				tile + new Vector2(-1, 0),
			};

			foreach (var candidate in candidates) {
				if (!IsWithinMap(location, candidate)) continue;
				if (IsSupervisorPassable(location, candidate)) yield return candidate;
			}
		}
        private static void AddLines(List<string> lines, params string[] scriptLines)
        {
            foreach (var line in scriptLines)
                lines.Add(line);
        }
        private string BuildScript(Vector2 startTile, int startFacing, List<Vector2> path, BotJob job)
        {
            var lines = new List<string>();
            int facing = startFacing;
            Vector2 current = startTile;

            lines.Add("run = function");

            AddScriptHelpers(lines);
            AddPathMovement(lines, path, ref facing, ref current);

            int finalFacing = FacingTowardTarget(current, job.TargetTile);
            if (finalFacing < 0) return null;

            AppendTurns(lines, ref facing, finalFacing);

            AddPositionCheck(lines, current);
            AddAheadSafetyCheck(lines, job);
            AddJobAction(lines, job);

            AddScriptFooter(lines);

            return string.Join("\n", lines);
        }
        private static void AddScriptHelpers(List<string> lines)
        {
                AddLines(lines,
                "    faceNorth = function()",
                "        while not me.facing == north",
                "            me.right",
                "       end while",
                "    end function",
                "    faceEast = function()",
                "        while not me.facing == east",
                "           me.right",
                "        end while",
                "    end function",
                "    faceSouth = function()",
                "       while not me.facing == south",
                "            me.right",
                "       end while",
                "    end function",
                "    faceWest = function()",
                "        while not me.facing == west",
                "            me.right",
                "        end while",
                "    end function",
                "    selectInventoryByName = function(name=\"Fertilizer\")",
                "        for i in range(0,11)",
                "            item = me.inventory[i]",
                "            if item and item.hasIndex(\"name\") and item.name == name then",
                "                me.select i",
                "                return true",
                "            end if",
                "        end for",
                "        return false",
                "    end function",
                "",
                "    selectInventoryByNameEndingWith = function(suffix)",
                "        for i in range(0,11)",
                "            item = me.inventory[i]",
                "            if item and item.hasIndex(\"name\") and item.name.len >= suffix.len and item.name[-suffix.len:] == suffix then",
                "                me.select i",
                "                return true",
                "            end if",
                "        end for",
                "        return false",
                "    end function",
                "",
                "    selectInventoryByCategory = function(cat=\"Fertilizer\")",
                "        for i in range(0,11)",
                "            item = me.inventory[i]",
                "            if item and item.hasIndex(\"category\") and item.category == cat then",
                "                me.select i",
                "                if item.hasIndex(\"name\") then return item.name",
                "                return true",
                "            end if",
                "        end for",
                "        return false",
                "    end function",
                "",
                "    selectInventoryByType = function(type)",
                "        if type == \"Scythe\" then",
                "            me.select 4",
                "            return true",
                "        end if",
                "        for i in range(0,11)",
                "            item = me.inventory[i]",
                "            if item and item.hasIndex(\"type\") and item.type == type then",
                "                me.select i",
                "                if item.hasIndex(\"name\") then return item.name",
                "                return true",
                "            end if",
                "        end for",
                "        return false",
                "    end function",
                "",
                "    moveStep = function(expectedFacing, dx, dy)",
                "        startX = me.position.x",
                "        startY = me.position.y",
                "        if me.facing != expectedFacing then",
                "            print \"Facing mismatch: expected \" + expectedFacing + \" got \" + me.facing",
                "            return false",
                "        end if",
                "        me.forward",
                "        if me.position.x != startX + dx or me.position.y != startY + dy then",
                "            print \"Movement mismatch: expected \" + (startX + dx) + \",\" + (startY + dy) + \" got \" + me.position.x + \",\" + me.position.y",
                "            return false",
                "        end if",
                "        return true",
                "    end function"
            );
        }
        private static void AddWaterCropAction(List<string> lines)
{
    AddLines(lines,
        "    print \"Watering crop\"",
        "    if not selectInventoryByType(\"WateringCan\") then",
        "        print \"No watering can\"",
        "        return false",
        "    end if",
        "    me.useTool",
        "    wait 0",
        "    return true"
    );
}
private static void AddClearDebrisAction(List<string> lines)
{
    AddLines(lines,
        "    print \"Clearing tile\"",
        "    me.clearAhead",
        "    wait .1",
        "    return true"
    );
}
private static void AddHarvestCropAction(List<string> lines, BotJob job)
{
    if (job.RequiresScythe)
    {
        AddLines(lines,
            "    print \"Harvesting crop with scythe\"",
            "    me.select 4",
            "    me.useTool",
            "    wait .1",
            "    return true"
        );
    }
    else
    {
        AddLines(lines,
            "    print \"Harvesting crop normally\"",
            "    if me.ahead and me.ahead.name then",
            "        crop_name = me.ahead.name",
            "    end if",
            "    me.harvest",
            "    wait .1",
            "    if me.ahead and me.ahead.name == crop_name then",
            "      me.select 4",
            "      me.useTool",
            "    end if",   
            "    return true"
        );
    }
}
private static void AddPlantCropAction(List<string> lines, BotJob job)
{
    AddLines(lines,
        "    print \"Planting crop\"",
        "    seedName = selectInventoryByCategory(\"Seed\")",
        "    trellised = [\"Hops Starter\",\"Grape Starter\",\"Bean Starter\"]",
        "    if not seedName then",
        "        print \"No seeds\"",
        "        return false",
        "    end if"
    );

    if (job.CanFertilize)
        lines.Add("    if selectInventoryByCategory(\"Fertilizer\") then me.placeItem");

    AddLines(lines,
        "    if not selectInventoryByName(seedName) then",
        "        print \"Seed disappeared before planting\"",
        "        return false",
        "    end if",
        "    if(trellised.hasIndex(seedName)) then",
        "       if me.position.x%2==1 and me.position.y%2==1 then",
        "           faceNorth",
        "           me.placeItem",
        "           faceSouth",   
        "       end if",
        "    end if",
        "    me.placeItem",
        "    wait .1",
        "    return true"
    );
}

private static readonly Dictionary<string, HashSet<string>> BotMachineRoles =
    new(StringComparer.OrdinalIgnoreCase)
{
    ["all_machines"] = new()
    {
        "Keg",
        "Preserves Jar",
        "Seed Maker",
        "Furnace",
        "Charcoal Kiln",
        "Mayonnaise Machine",
        "Cheese Press",
        "Loom",
        "Oil Maker",
        "Crystalarium"
    },

    ["all-machines"] = new()
    {
        "Keg",
        "Preserves Jar",
        "Seed Maker",
        "Furnace",
        "Charcoal Kiln",
        "Mayonnaise Machine",
        "Cheese Press",
        "Loom",
        "Oil Maker",
        "Crystalarium"
    },

    ["allmachines"] = new()
    {
        "Keg",
        "Preserves Jar",
        "Seed Maker",
        "Furnace",
        "Charcoal Kiln",
        "Mayonnaise Machine",
        "Cheese Press",
        "Loom",
        "Oil Maker",
        "Crystalarium"
    },

    ["machine"] = new()
    {
        "Keg",
        "Preserves Jar",
        "Seed Maker",
        "Furnace",
        "Charcoal Kiln",
        "Mayonnaise Machine",
        "Cheese Press",
        "Loom",
        "Oil Maker",
        "Crystalarium"
    },

    ["machines"] = new()
    {
        "Keg",
        "Preserves Jar",
        "Seed Maker",
        "Furnace",
        "Charcoal Kiln",
        "Mayonnaise Machine",
        "Cheese Press",
        "Loom",
        "Oil Maker",
        "Crystalarium"
    },

    ["keg"] = new()
    {
        "Keg"
    },

    ["kegs"] = new()
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

    ["jars"] = new()
    {
        "Preserves Jar"
    },

    ["preserves"] = new()
    {
        "Preserves Jar"
    },

    ["starfruit"] = new()
    {
        "Keg",
        "Preserves Jar"
    },

    ["fruit"] = new()
    {
        "Keg",
        "Preserves Jar"
    },

    ["seed"] = new()
    {
        "Seed Maker"
    },

    ["seedmaker"] = new()
    {
        "Seed Maker"
    },

    ["seedmakers"] = new()
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
    },

    ["furnaces"] = new()
    {
        "Furnace",
        "Charcoal Kiln"
    },

    ["loom"] = new()
    {
        "Loom"
    },

    ["oil"] = new()
    {
        "Oil Maker"
    },

    ["oilmaker"] = new()
    {
        "Oil Maker"
    },

    ["crystal"] = new()
    {
        "Crystalarium"
    },

    ["crystalarium"] = new()
    {
        "Crystalarium"
    }
};

private static bool IsAllMachinesToken(string text)
{
    string normalized = (text ?? "").Trim();
    return string.Equals(normalized, "all_machines", StringComparison.OrdinalIgnoreCase)
        || string.Equals(normalized, "all-machines", StringComparison.OrdinalIgnoreCase)
        || string.Equals(normalized, "allmachines", StringComparison.OrdinalIgnoreCase)
        || string.Equals(normalized, "machines", StringComparison.OrdinalIgnoreCase)
        || string.Equals(normalized, "machine", StringComparison.OrdinalIgnoreCase);
}

private static bool TryGetAllowedMachinesForRoleToken(string text, out HashSet<string> allowedMachines)
{
    allowedMachines = null;
    string normalized = (text ?? "").Trim();
    if (IsAllMachinesToken(normalized))
        return false;

    return BotMachineRoles.TryGetValue(normalized, out allowedMachines);
}

private static bool TryGetAllowedMachinesForBot(
    string botName,
    out HashSet<string> allowedMachines)
{
    allowedMachines = null;

    if (string.IsNullOrWhiteSpace(botName))
        return false;

    var normalized = botName.Trim().ToLowerInvariant();

    foreach (var pair in BotMachineRoles.OrderByDescending(p => p.Key.Length))
    {
        if (normalized.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
        {
            allowedMachines = pair.Value;
            return true;
        }
    }

    return false;
}

private static bool IsMachineBotFor(
    string botName,
    string machineName)
{
    if (string.IsNullOrWhiteSpace(botName))
        return false;

    var normalized = botName.Trim().ToLowerInvariant();


    if (normalized.StartsWith(machineName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return false;
}
private static void AddServiceMachineAction(List<string> lines, BotJob job)
{
    lines.Add($"    print \"Servicing machine: {job.TargetName}\"");

    // First try to harvest whatever is ready.
    lines.Add("    me.harvest");

    if (job.InputRules == null || job.InputRules.Count == 0)
    {
        lines.Add("    print \"No input rule for this machine\"");
        lines.Add("    return true");
        return;
    }

    for (int i = 0; i < job.InputRules.Count; i++)
    {
        var input = job.InputRules[i];

        string prefix = i == 0 ? "    if " : "    else if ";

        if (!string.IsNullOrWhiteSpace(input.Category))
        {
            lines.Add($"{prefix}selectInventoryByCategory(\"{EscapeMiniScript(input.Category)}\") then");
        }
        else if (!string.IsNullOrWhiteSpace(input.Name))
        {
            lines.Add($"{prefix}selectInventoryByName(\"{EscapeMiniScript(input.Name)}\") then");
        }
        else if (!string.IsNullOrWhiteSpace(input.EndsWith))
        {
            lines.Add($"{prefix}selectInventoryByNameEndingWith(\"{EscapeMiniScript(input.EndsWith)}\") then");
        }
        else
        {
            continue;
        }

        lines.Add("        me.placeItem");
        lines.Add("        return true");
    }

    lines.Add("    else");
    lines.Add($"        print \"No input available for {EscapeMiniScript(job.TargetName)}\"");
    lines.Add("        return false");
    lines.Add("    end if");

}

private static string EscapeMiniScript(string text)
{
    return (text ?? "").Replace("\"", "\"\"");
}

private static void AddAheadSafetyCheck(List<string> lines, BotJob job)
{
    string expected = EscapeMiniScript(job.TargetName);

    AddLines(lines,
        "    ahead = me.ahead",
        "    if ahead and ahead.hasIndex(\"name\") and (ahead.name == \"Bot\" or ahead.name == \"Farmtronics Bot\") then",
        "        print \"Safety refused: bot ahead\"",
        "        return false",
        "    end if",
        "    if ahead and ahead.hasIndex(\"type\") and ahead.type == \"Crafting\" and (not ahead.hasIndex(\"name\") or ahead.name != \"" + expected + "\") then",
        "        print \"Safety refused: protected placed object ahead\"",
        "        return false",
        "    end if"
    );

    switch (job.Type)
    {
        case JobType.ClearDebris:
        case JobType.MineBreakStone:
        case JobType.MineCutWeeds:
            AddLines(lines,
                "    if not ahead then",
                "        print \"Safety refused: no target ahead\"",
                "        return false",
                "    end if",
                $"    if (not ahead.hasIndex(\"name\") or ahead.name != \"{expected}\") and (not ahead.hasIndex(\"type\") or ahead.type != \"{expected}\") then",
                "        print \"Safety refused: clear target changed\"",
                "        return false",
                "    end if"
            );
            break;

        case JobType.ClearDeadCrop:
            AddLines(lines,
                "    if not ahead or not ahead.hasIndex(\"crop\") or not ahead.crop or not ahead.crop.dead then",
                "        print \"Safety refused: dead crop target changed\"",
                "        return false",
                "    end if"
            );
            break;

        case JobType.HarvestCrop:
        case JobType.WaterCrop:
            AddLines(lines,
                "    if not ahead or not ahead.hasIndex(\"crop\") or not ahead.crop then",
                "        print \"Safety refused: crop target changed\"",
                "        return false",
                "    end if"
            );
            break;

        case JobType.PlantCrop:
        case JobType.TillSoil:
        case JobType.MineDigFloor:
            AddLines(lines,
                "    if ahead and ahead.hasIndex(\"type\") and ahead.type != \"unknown\" and ahead.type != \"HoeDirt\" then",
                "        print \"Safety refused: ground target is occupied\"",
                "        return false",
                "    end if"
            );
            break;

        case JobType.ServiceMachine:
            AddLines(lines,
                $"    if not ahead or not ahead.hasIndex(\"name\") or ahead.name != \"{expected}\" then",
                "        print \"Safety refused: machine target changed\"",
                "        return false",
                "    end if"
            );
            break;
    }
}

private static void AddTillSoilAction(List<string> lines, BotJob job)
{
    AddLines(lines,
        "    print \"Tilling soil\"",
        "    if not selectInventoryByType(\"Hoe\") then",
        "        print \"No hoe\"",
        "        return false",
        "    end if",
        "    me.useTool",
        "    wait 0",
        "    return true"
    );
}
private static void AddMineBreakStoneAction(List<string> lines)
{
    AddLines(lines,
        "    print \"Breaking mine stone\"",
        "    me.clearAhead",
        "    wait 0",
        "    return true"
    );
}
private static void AddMineCutWeedsAction(List<string> lines)
{
    AddLines(lines,
        "    print \"Cutting mine weeds\"",
        "    me.clearAhead",
        "    wait 0",
        "    return true"
    );
}
private static void AddMineDigFloorAction(List<string> lines)
{
    AddLines(lines,
        "    print \"Diggin mine floor\"",
        "    if not selectInventoryByType(\"Hoe\") then",
        "        print \"No hoe\"",
        "        return false",
        "    end if",
        "    me.useTool",
        "    wait 0",
        "    return true"
    );
}
private static void AddScriptFooter(List<string> lines)
{
    AddLines(lines,
        "end function",
        "run"
    );
}
        private void AddJobAction(List<string> lines, BotJob job)
        {
            lines.Add($"    print \"Job: {job.Type} target {(int)job.TargetTile.X},{(int)job.TargetTile.Y} name: {job.TargetName}\"");

            switch (job.Type)
            {
                case JobType.HarvestCrop:
                    AddHarvestCropAction(lines, job);
                    break;

                case JobType.ClearDeadCrop:// force scythe for dead crops to avoid accidentally harvesting live ones
                    AddHarvestCropAction(lines, job);
                    break;

                case JobType.PlantCrop:
                    AddPlantCropAction(lines, job);
                    break;

                case JobType.WaterCrop:
                    AddWaterCropAction(lines);
                    break;

                case JobType.ClearDebris:
                    AddClearDebrisAction(lines);
                    break;

                case JobType.ServiceMachine:
                    AddServiceMachineAction(lines, job);
                    break;
                case JobType.TillSoil:
                    AddTillSoilAction(lines, job);
                    break;
                case JobType.MineBreakStone:
                    AddMineBreakStoneAction(lines);
                    break;
                case JobType.MineCutWeeds:
                    AddMineCutWeedsAction(lines);
                    break;
                case JobType.MineDigFloor:
                    AddMineDigFloorAction(lines);
                    break;

                default:
                    lines.Add("    print \"Unknown job type\"");
                    lines.Add("    return false");
                    break;
            }
        }
        private static void AddPositionCheck(List<string> lines, Vector2 expectedTile)
        {
            AddLines(lines,
                $"    expectedX = {(int)expectedTile.X}",
                $"    expectedY = {(int)expectedTile.Y}",
                "    if me.position.x != expectedX or me.position.y != expectedY then",
                "        print \"Target mismatch: expected position \" + expectedX + \",\" + expectedY + \" got \" + me.position.x + \",\" + me.position.y",
                "        return false",
                "    end if"
            );
        }
        private void AddPathMovement(List<string> lines, List<Vector2> path, ref int facing, ref Vector2 current)
        {
            foreach (var step in path)
            {
                int targetFacing = FacingForStep(step - current);
                if (targetFacing < 0)
                    throw new InvalidOperationException("Invalid path step.");

                AppendTurns(lines, ref facing, targetFacing);

                Vector2 delta = step - current;
                lines.Add($"    if not moveStep({targetFacing}, {(int)delta.X}, {(int)delta.Y}) then return false");

                current = step;
            }
        }
		private static int FacingForStep(Vector2 delta) {
			if (delta == new Vector2(0, -1)) return 0;
			if (delta == new Vector2(1, 0)) return 1;
			if (delta == new Vector2(0, 1)) return 2;
			if (delta == new Vector2(-1, 0)) return 3;
			return -1;
		}

		private static int FacingTowardTarget(Vector2 fromTile, Vector2 targetTile) {
			if (targetTile.X > fromTile.X) return 1;
			if (targetTile.X < fromTile.X) return 3;
			if (targetTile.Y > fromTile.Y) return 2;
			if (targetTile.Y < fromTile.Y) return 0;
			return -1;
		}

		private static void AppendTurns(List<string> lines, ref int currentFacing, int targetFacing) {
			int diff = (targetFacing - currentFacing + 4) % 4;
			switch (diff) {
			case 1:
				lines.Add("me.right");
				break;
			case 2:
				lines.Add("me.right");
				lines.Add("me.right");
				break;
			case 3:
				lines.Add("me.left");
				break;
			}
			currentFacing = targetFacing;
		}

		private static bool IsWithinMap(GameLocation location, Vector2 tile) {
			int width = location.map.Layers[0].LayerWidth;
			int height = location.map.Layers[0].LayerHeight;
			return tile.X >= 0 && tile.Y >= 0 && tile.X < width && tile.Y < height;
		}

		private void UpdateMovementTracking(BotSupervisorState state, TimeSpan now) {
			if (state?.Bot == null) return;
			var currentTile = state.Bot.TileLocation;
			if (currentTile == state.LastObservedTile) return;
			state.LastObservedTile = currentTile;
			state.LastMovementAt = now;
		}

		private void RecordTargetFailure(GameLocation location, Vector2 tile) {
			if (location == null) return;
			string key = GetBlockedTargetKey(location, tile);
			targetFailureCounts.TryGetValue(key, out int count);
			targetFailureCounts[key] = count + 1;
			if (count + 1 >= maxTargetFailures)
				ModEntry.instance.Monitor.Log($"Supervisor: target {tile.X},{tile.Y} failed {count + 1} times; permanently ignoring this run.");
		}
        
        public void ReportFarmerPosition()
        {
            var player = Game1.player;
            var loc = player.currentLocation;

            if (player == null || loc == null)
                return;

            var pos = player.position;
            var x = (int)(pos.X / 64);
            var y = (int)(pos.Y / 64);

            var farmerTile = new Vector2(x, y);
            var aheadTile = GetTileAhead(farmerTile, player.FacingDirection);

            ValMap aheadInfo = TileInfo.GetInfo(loc, aheadTile);

            string msg =
                $"Farmer: {loc.NameOrUniqueName} tile {x},{y} " +
                $"facing {player.FacingDirection}; " +
                $"ahead {aheadTile.X},{aheadTile.Y}";

            string aheadMsg =
                $"Ahead info: {aheadInfo}";

            ModEntry.instance.Monitor.Log(msg);
            ModEntry.instance.Monitor.Log(aheadMsg);

            Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.newQuest_type));
            Game1.addHUDMessage(new HUDMessage(aheadMsg, HUDMessage.newQuest_type));
        }
        private static Vector2 GetTileAhead(Vector2 tile, int facingDirection)
        {
            return facingDirection switch
            {
                0 => tile + new Vector2(0, -1), // up
                1 => tile + new Vector2(1, 0),  // right
                2 => tile + new Vector2(0, 1),  // down
                3 => tile + new Vector2(-1, 0), // left
                _ => tile
            };
        }

        private bool LogOncePerInterval(string key, string message, LogLevel level, TimeSpan interval)
        {
            var now = Game1.currentGameTime?.TotalGameTime ?? TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
            if (rateLimitedLogTimes.TryGetValue(key, out var lastLogged) && now - lastLogged < interval)
                return false;

            rateLimitedLogTimes[key] = now;
            ModEntry.instance.Monitor.Log(message, level);
            return true;
        }

        public void ValidateBotPersistence()
        {
            AssignMissingBotIdentities();
            var physicalBots = ScanWorldForBotObjects();
            DetectDuplicateBotNames(physicalBots);
            RescueMissingBots(physicalBots);
            RegisterUntrackedWorldBots(physicalBots);
            SeparateOverlappingBots();
            SafeCleanupDuplicateBots(dryRun: false, automatic: true);
        }

        private void AssignMissingBotIdentities()
        {
            foreach (var snapshot in BuildBotSnapshots(validateFirst: false))
            {
                _ = snapshot.Bot.BotGuid;
                _ = snapshot.Bot.CreatedTick;
                snapshot.Bot.data.Update();
            }
        }

        private List<BotWorldEntry> ScanWorldForBotObjects()
        {
            var result = new List<BotWorldEntry>();
            if (!Context.IsWorldReady)
                return result;

            foreach (var location in GetLocationsForBotScan())
            {
                foreach (var pair in location.objects.Pairs)
                {
                    if (pair.Value is BotObject bot)
                        result.Add(new BotWorldEntry(bot, location, pair.Key));
                }
            }

            return result;
        }

        private List<GameLocation> GetLocationsForBotScan()
        {
            var locations = new List<GameLocation>();
            void AddLocation(GameLocation location)
            {
                if (location == null)
                    return;

                if (!locations.Any(existing => ReferenceEquals(existing, location)))
                    locations.Add(location);
            }

            foreach (var location in Game1.locations)
                AddLocation(location);

            foreach (var farm in Game1.locations.OfType<Farm>())
            {
                foreach (var building in farm.buildings)
                    AddLocation(GetBuildingIndoorLocation(building));
            }

            AddLocation(Game1.player?.currentLocation);

            foreach (var bot in BotManager.GetAllBots().Where(bot => bot != null))
                AddLocation(bot.currentLocation);

            return locations;
        }

        private static GameLocation GetBuildingIndoorLocation(Building building)
        {
            if (building == null)
                return null;

            try
            {
                return building.GetIndoors();
            }
            catch
            {
                return null;
            }
        }

        private List<BotStoredEntry> ScanStoredBotObjects()
        {
            var result = new List<BotStoredEntry>();
            if (!Context.IsWorldReady)
                return result;

            AddStoredBotsFromItems(Game1.player.Items, "player inventory", result);
            if (Game1.player.recoveredItem is BotObject recoveredBot)
                result.Add(new BotStoredEntry(recoveredBot, "player recoveredItem"));

            foreach (var location in GetLocationsForBotScan())
            {
                foreach (var pair in location.objects.Pairs)
                {
                    if (pair.Value is Chest chest)
                        AddStoredBotsFromItems(chest.Items, $"chest {location.NameOrUniqueName} {pair.Key.X},{pair.Key.Y}", result);
                    else if (pair.Value is BotObject bot)
                        AddStoredBotsFromItems(bot.inventory, $"bot inventory {bot.name}", result);
                }
            }

            return result;
        }

        private static void AddStoredBotsFromItems(IList<Item> items, string container, List<BotStoredEntry> result)
        {
            if (items == null)
                return;

            foreach (var item in items)
            {
                if (item is BotObject bot)
                    result.Add(new BotStoredEntry(bot, container));
            }
        }

        private List<BotSnapshot> BuildBotSnapshots(bool validateFirst = true)
        {
            if (validateFirst)
                AssignMissingBotIdentities();

            var physicalBots = ScanWorldForBotObjects();
            var storedBots = ScanStoredBotObjects();
            var trackedBots = BotManager.GetAllBots().Where(bot => bot != null).ToList();
            var allBots = trackedBots
                .Concat(physicalBots.Select(entry => entry.Bot))
                .Concat(storedBots.Select(entry => entry.Bot))
                .Where(bot => bot != null)
                .Distinct<BotObject>(ReferenceEqualityComparer.Instance)
                .ToList();

            var snapshots = new List<BotSnapshot>();
            foreach (var bot in allBots)
            {
                var physical = physicalBots.FirstOrDefault(entry => ReferenceEquals(entry.Bot, bot));
                var stored = storedBots.FirstOrDefault(entry => ReferenceEquals(entry.Bot, bot));
                bool tracked = trackedBots.Any(trackedBot => ReferenceEquals(trackedBot, bot));
                bool placed = physical != null;
                bool storedInContainer = stored != null;
                bool inSupervisorState = botStates.Values.Any(state => ReferenceEquals(state.Bot, bot));
                snapshots.Add(new BotSnapshot(
                    bot,
                    "Unclassified",
                    physical?.Location,
                    physical?.Tile ?? bot.TileLocation,
                    stored?.Container,
                    tracked,
                    placed,
                    storedInContainer,
                    inSupervisorState));
            }

            MarkKnownCanonicals(snapshots);
            return snapshots
                .Select(snapshot => snapshot with { Classification = ClassifyBotSnapshot(snapshot, snapshots) })
                .ToList();
        }

        private string ClassifyBotSnapshot(BotSnapshot snapshot, List<BotSnapshot> allSnapshots)
        {
            if (snapshot == null)
                return "UnknownBot";

            if (snapshot.IsStored)
                return "StoredBot";

            bool hasSameNameDuplicates = allSnapshots.Any(other =>
                !ReferenceEquals(other.Bot, snapshot.Bot)
                && string.Equals(other.Bot.name, snapshot.Bot.name, StringComparison.OrdinalIgnoreCase));

            var canonical = hasSameNameDuplicates
                ? ChooseCanonicalBot(allSnapshots
                    .Where(other => string.Equals(other.Bot.name, snapshot.Bot.name, StringComparison.OrdinalIgnoreCase))
                    .ToList())
                : snapshot;

            bool isCanonical = canonical != null && ReferenceEquals(canonical.Bot, snapshot.Bot);

            if (snapshot.Bot.IsQuarantined && hasSameNameDuplicates && !isCanonical)
                return "QuarantinedDuplicate";

            if (IsBrickedDuplicate(snapshot, canonical, allSnapshots))
                return "BrickedDuplicate";

            if (snapshot.IsPlaced && !snapshot.IsTracked)
                return "PlacedButNotRegistered";

            if (!snapshot.IsPlaced && snapshot.IsTracked)
                return "RegisteredGhost";

            if (IsFunctionalWorldBotSnapshot(snapshot, isCanonical))
                return "FunctionalWorldBot";

            if (snapshot.Bot.IsQuarantined)
                return "QuarantinedDuplicate";

            return "UnknownBot";
        }

        private bool IsFunctionalWorldBotSnapshot(BotSnapshot snapshot, bool isCanonical)
        {
            return snapshot != null
                && isCanonical
                && snapshot.IsPlaced
                && snapshot.IsTracked
                && !snapshot.IsStored
                && !snapshot.Bot.IsQuarantined
                && snapshot.Bot.shell != null;
        }

        private void MarkKnownCanonicals(List<BotSnapshot> snapshots)
        {
            foreach (var snapshot in snapshots.Where(snapshot => snapshot.IsPlaced && !string.IsNullOrWhiteSpace(snapshot.Bot.BotGuid)))
                knownCanonicalByGuid[snapshot.Bot.BotGuid] = snapshot.Bot;
        }

        private void DetectDuplicateBotNames(List<BotWorldEntry> physicalBots)
        {
            foreach (var group in BotManager.GetAllBots()
                .Concat(physicalBots.Select(entry => entry.Bot))
                .Where(bot => bot != null)
                .GroupBy(bot => bot.name ?? bot.Name ?? "(unnamed)", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(bot => bot).Distinct<BotObject>(ReferenceEqualityComparer.Instance).Count() > 1))
            {
                LogOncePerInterval(
                    $"duplicate-name:{group.Key}",
                    $"Bot persistence WARN: duplicate bot name '{group.Key}' count={group.Select(bot => bot).Distinct<BotObject>(ReferenceEqualityComparer.Instance).Count()}.",
                    LogLevel.Warn,
                    TimeSpan.FromSeconds(30));
            }
        }

        private void RescueMissingBots(List<BotWorldEntry> physicalBots)
        {
            var physicallyPresent = new HashSet<BotObject>(
                physicalBots.Select(entry => entry.Bot),
                ReferenceEqualityComparer.Instance);
            var snapshots = BuildBotSnapshots(validateFirst: false);

            foreach (var bot in BotManager.GetAllBots().ToList())
            {
                if (bot == null)
                    continue;

                var location = GetPreferredRescueLocation(bot) ?? bot.currentLocation ?? Game1.player?.currentLocation;
                if (location == null)
                    continue;

                if (physicallyPresent.Contains(bot))
                    continue;

                var snapshot = snapshots.FirstOrDefault(snapshot => ReferenceEquals(snapshot.Bot, bot));
                if (snapshot?.IsStored == true)
                {
                    LogOncePerInterval(
                        $"tracked-stored:{bot.BotGuid}",
                        $"Bot persistence WARN: tracked bot {bot.name} guid={bot.BotGuid} is stored in {snapshot.Container}; not rescuing it into the world.",
                        LogLevel.Warn,
                        TimeSpan.FromSeconds(30));
                    continue;
                }

                var placedCanonical = snapshots.FirstOrDefault(snapshot =>
                    snapshot.IsPlaced
                    && !ReferenceEquals(snapshot.Bot, bot)
                    && (
                        string.Equals(snapshot.Bot.BotGuid, bot.BotGuid, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(snapshot.Bot.name, bot.name, StringComparison.OrdinalIgnoreCase)
                    ));
                if (placedCanonical != null)
                {
                    LogOncePerInterval(
                        $"registered-ghost:{bot.BotGuid}:{placedCanonical.Bot.BotGuid}",
                        $"Bot persistence WARN: registered ghost {bot.name} guid={bot.BotGuid} has placed canonical candidate {placedCanonical.Bot.name} guid={placedCanonical.Bot.BotGuid}; not rescuing duplicate.",
                        LogLevel.Warn,
                        TimeSpan.FromSeconds(30));
                    if (!HasValuableInventory(bot))
                        RemoveBotFromRegistries(bot);
                    else
                        QuarantineBot(bot, "Registered-but-not-placed duplicate with valuable inventory.");
                    continue;
                }

                LogOncePerInterval(
                    $"tracked-missing:{bot.BotGuid}",
                    $"Bot persistence WARN: tracked bot missing from location.objects: {bot.name} loc={location.NameOrUniqueName} tile={bot.TileLocation.X},{bot.TileLocation.Y}. Rescuing.",
                    LogLevel.Warn,
                    TimeSpan.FromSeconds(10));
                EnsureBotPlacedSafely(bot, location, GetPreferredRescueTile(bot));
            }
        }

        private void RegisterUntrackedWorldBots(List<BotWorldEntry> physicalBots)
        {
            var tracked = new HashSet<BotObject>(BotManager.GetAllBots(), ReferenceEqualityComparer.Instance);

            foreach (var entry in physicalBots)
            {
                var bot = entry.Bot;
                if (bot == null || tracked.Contains(bot))
                    continue;

                LogOncePerInterval(
                    $"placed-not-registered:{bot.BotGuid}",
                    $"Bot persistence WARN: physical bot not tracked: {bot.name} loc={entry.Location.NameOrUniqueName} tile={entry.Tile.X},{entry.Tile.Y}. Re-registering.",
                    LogLevel.Warn,
                    TimeSpan.FromSeconds(30));

                if (bot.owner.Value == Game1.player.UniqueMultiplayerID)
                {
                    BotManager.RegisterLocalBot(bot, "world scan re-register");
                }
                else
                {
                    if (!BotManager.remoteInstances.TryGetValue(bot.owner.Value, out var remoteBots))
                    {
                        remoteBots = new List<BotObject>();
                        BotManager.remoteInstances[bot.owner.Value] = remoteBots;
                    }
                    remoteBots.Add(bot);
                }

                bot.currentLocation = entry.Location;
                bot.TileLocation = entry.Tile;
                bot.Position = entry.Tile.GetAbsolutePosition();
                bot.data.Update();
                tracked.Add(bot);
            }
        }

        private Vector2 GetPreferredRescueTile(BotObject bot)
        {
            if (bot == null)
                return Game1.player?.Tile ?? Vector2.Zero;

            if (IsStaleMineLocation(bot.currentLocation))
                return Game1.player?.Tile ?? Vector2.Zero;

            if (namedHomeTiles.TryGetValue(bot.name ?? "", out var homeTile))
                return homeTile;

            return bot.TileLocation != Vector2.Zero ? bot.TileLocation : (Game1.player?.Tile ?? Vector2.Zero);
        }

        private GameLocation GetPreferredRescueLocation(BotObject bot)
        {
            if (IsStaleMineLocation(bot?.currentLocation))
                return Game1.player?.currentLocation ?? Game1.getLocationFromName("Farm") ?? bot?.currentLocation;

            if (bot != null && namedHomeTiles.ContainsKey(bot.name ?? ""))
                return Game1.getLocationFromName("Farm") ?? bot.currentLocation;

            return bot?.currentLocation;
        }

        private static bool IsStaleMineLocation(GameLocation location)
        {
            if (location is not MineShaft)
                return false;

            return !ReferenceEquals(Game1.player?.currentLocation, location);
        }

        private void SeparateOverlappingBots()
        {
            foreach (var group in ScanWorldForBotObjects()
                .GroupBy(entry => $"{entry.Location.NameOrUniqueName}:{entry.Tile.X},{entry.Tile.Y}")
                .Where(group => group.Select(entry => entry.Bot).Distinct<BotObject>(ReferenceEqualityComparer.Instance).Count() > 1))
            {
                var entries = group
                    .GroupBy(entry => entry.Bot, ReferenceEqualityComparer.Instance)
                    .Select(botGroup => botGroup.First())
                    .ToList();

                var keeper = entries.First();
                LogOncePerInterval(
                    $"overlap:{keeper.Location.NameOrUniqueName}:{keeper.Tile.X},{keeper.Tile.Y}",
                    $"Bot persistence WARN: overlapping bots at {keeper.Location.NameOrUniqueName} {keeper.Tile.X},{keeper.Tile.Y}: {string.Join(", ", entries.Select(entry => entry.Bot.name))}.",
                    LogLevel.Warn,
                    TimeSpan.FromSeconds(10));

                foreach (var entry in entries.Skip(1))
                {
                    ClearBotScriptQueue(entry.Bot);
                    CancelSupervisorPlan(entry.Bot);
                    EnsureBotPlacedSafely(entry.Bot, entry.Location, GetPreferredRescueTile(entry.Bot));
                }
            }
        }

        private void CancelSupervisorPlan(BotObject bot)
        {
            var state = botStates.Values.FirstOrDefault(state => ReferenceEquals(state.Bot, bot));
            if (state == null)
                return;

            ClearCurrentPlan(state);
            state.Mode = BotMode.Cooldown;
            state.NextAllowedPlanAt = Game1.currentGameTime.TotalGameTime + TimeSpan.FromSeconds(2);
            state.LastNoPlanReason = "Bot persistence safety intervention.";
        }

        private bool EnsureBotPlacedSafely(BotObject bot, GameLocation location, Vector2 preferredTile)
        {
            if (bot == null || location == null)
                return false;

            if (!TryFindSafeBotTile(bot, location, preferredTile, out var safeTile))
            {
                LogOncePerInterval(
                    $"no-safe-tile:{bot.BotGuid}:{location.NameOrUniqueName}",
                    $"Bot persistence WARN: no safe tile found for {bot.name} near {location.NameOrUniqueName} {preferredTile.X},{preferredTile.Y}; bot remains tracked and disabled.",
                    LogLevel.Warn,
                    TimeSpan.FromSeconds(30));
                QuarantineBot(bot, "No safe tile found for placement.");
                return false;
            }

            RemoveBotFromWorld(bot);
            bot.currentLocation = location;
            bot.TileLocation = safeTile;
            bot.Position = safeTile.GetAbsolutePosition();
            ClearBotQuarantine(bot, "safe placement");
            bot.data.Update();
            location.objects[safeTile] = bot;

            ModEntry.instance.Monitor.Log(
                $"Bot persistence WARN: placed/reinserted {bot.name} at {location.NameOrUniqueName} {safeTile.X},{safeTile.Y}.",
                LogLevel.Warn);
            ReportBotInventory(bot);
            return true;
        }

        private bool TryFindSafeBotTile(BotObject bot, GameLocation location, Vector2 preferredTile, out Vector2 safeTile)
        {
            if (IsTileSafeForBot(bot, location, preferredTile))
            {
                safeTile = preferredTile;
                return true;
            }

            for (int radius = 1; radius <= 12; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                            continue;

                        var candidate = preferredTile + new Vector2(dx, dy);
                        if (IsTileSafeForBot(bot, location, candidate))
                        {
                            safeTile = candidate;
                            return true;
                        }
                    }
                }
            }

            safeTile = Vector2.Zero;
            return false;
        }

        private bool IsTileOccupiedByAnotherBot(BotObject bot, GameLocation location, Vector2 tile)
        {
            return location != null
                && location.objects.TryGetValue(tile, out StardewValley.Object obj)
                && obj is BotObject
                && !ReferenceEquals(obj, bot);
        }

        private bool IsTileSafeForBot(BotObject bot, GameLocation location, Vector2 tile)
        {
            if (location == null || !IsWithinMap(location, tile))
                return false;

            if (IsTileReservedByOther(bot, location, tile))
                return false;

            if (location.objects.TryGetValue(tile, out StardewValley.Object obj) && !ReferenceEquals(obj, bot))
                return false;

            if (location.objects.TryGetValue(tile, out obj) && ReferenceEquals(obj, bot))
                return true;

            if (IsTileOccupiedByAnotherBot(bot, location, tile))
                return false;

            return IsSupervisorPassable(location, tile);
        }

        private void QuarantineBot(BotObject bot, string reason)
        {
            if (bot == null)
                return;

            bool wasAlreadyQuarantined = bot.IsQuarantined;
            ClearBotScriptQueue(bot);
            CancelSupervisorPlan(bot);
            bot.IsQuarantined = true;
            bot.modData[ModEntry.GetModDataKey("quarantined")] = reason;
            var physical = ScanWorldForBotObjects().FirstOrDefault(entry => ReferenceEquals(entry.Bot, bot));
            if (physical != null && TryFindSafeBotTile(bot, physical.Location, physical.Tile + new Vector2(0, 1), out var quarantineTile))
            {
                RemoveBotFromWorld(bot);
                bot.currentLocation = physical.Location;
                bot.TileLocation = quarantineTile;
                bot.Position = quarantineTile.GetAbsolutePosition();
                bot.data.Update();
                physical.Location.objects[quarantineTile] = bot;
            }
            string message = $"Bot persistence WARN: quarantined {bot.name}; guid={bot.BotGuid}; reason={reason}; loc={bot.currentLocation?.NameOrUniqueName ?? "(null)"} tile={bot.TileLocation.X},{bot.TileLocation.Y}.";
            if (!wasAlreadyQuarantined)
            {
                ModEntry.instance.Monitor.Log(message, LogLevel.Warn);
                ReportBotInventory(bot);
            }
            else
            {
                LogOncePerInterval($"quarantined:{bot.BotGuid}:{reason}", message, LogLevel.Warn, TimeSpan.FromSeconds(30));
            }
        }

        private void ClearBotQuarantine(BotObject bot, string reason)
        {
            if (bot == null || !bot.IsQuarantined)
                return;

            bot.IsQuarantined = false;
            bot.modData.Remove(ModEntry.GetModDataKey("quarantined"));
            ModEntry.instance.Monitor.Log(
                $"Bot persistence: cleared quarantine for {bot.name}; guid={bot.BotGuid}; reason={reason}.",
                LogLevel.Warn);
        }

        public void SafeCleanupDuplicateBots(bool dryRun, bool automatic = false)
        {
            if (automatic)
                dryRun = true;

            var snapshots = BuildBotSnapshots(validateFirst: false);
            var summary = new DedupeSummary
            {
                RemainingUnknownBots = snapshots.Count(snapshot => snapshot.Classification == "UnknownBot")
            };

            foreach (var group in snapshots
                .Where(snapshot => snapshot.Bot != null)
                .GroupBy(snapshot => snapshot.Bot.name ?? snapshot.Bot.Name ?? "(unnamed)", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                CleanupDuplicateGroup(group.ToList(), "name", dryRun, automatic, summary);
            }

            foreach (var group in snapshots
                .Where(snapshot => snapshot.Bot != null && !string.IsNullOrWhiteSpace(snapshot.Bot.BotGuid))
                .GroupBy(snapshot => snapshot.Bot.BotGuid, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                string message = $"Bot dedupe {(dryRun ? "DRYRUN " : "")}guid duplicate report only: guid={group.Key} count={group.Count()} canonical={DescribeBotSnapshot(ChooseCanonicalBot(group.ToList()))}";
                if (automatic)
                    LogOncePerInterval($"dedupe-guid:{group.Key}", message, LogLevel.Warn, TimeSpan.FromSeconds(30));
                else
                    ModEntry.instance.Monitor.Log(message, LogLevel.Warn);
            }

            if (!automatic)
            {
                if (!dryRun)
                    summary.RemainingUnknownBots = BuildBotSnapshots(validateFirst: false)
                        .Count(snapshot => snapshot.Classification == "UnknownBot");

                ModEntry.instance.Monitor.Log(
                    $"Bot dedupe summary: {(dryRun ? "would delete" : "deleted")} BrickedDuplicate count={summary.DeletedBrickedDuplicates}; " +
                    $"quarantined duplicate count={summary.QuarantinedDuplicates}; " +
                    $"skipped valuable duplicate count={summary.SkippedValuableDuplicates}; " +
                    $"remaining UnknownBot count={summary.RemainingUnknownBots}.",
                    LogLevel.Warn);
            }
        }

        private void CleanupDuplicateGroup(List<BotSnapshot> group, string duplicateKind, bool dryRun, bool automatic, DedupeSummary summary)
        {
            var canonical = ChooseCanonicalBot(group);
            if (canonical == null)
                return;

            string canonicalMessage = $"Bot dedupe {(dryRun ? "DRYRUN " : "")}{duplicateKind}: canonical {DescribeBotSnapshot(canonical)}";
            if (automatic)
                LogOncePerInterval($"dedupe-canonical:{duplicateKind}:{canonical.Bot.name}", canonicalMessage, LogLevel.Warn, TimeSpan.FromSeconds(30));
            else
                ModEntry.instance.Monitor.Log(canonicalMessage, LogLevel.Warn);

            foreach (var duplicate in group.Where(snapshot => !ReferenceEquals(snapshot.Bot, canonical.Bot)))
            {
                bool safeDelete = IsSafeDuplicateDelete(duplicate, canonical, group);
                if (safeDelete)
                {
                    string deleteMessage = $"Bot dedupe {(dryRun ? "DRYRUN would remove" : "removing")} BrickedDuplicate: {DescribeBotSnapshot(duplicate)}";
                    if (automatic)
                        LogOncePerInterval($"dedupe-bricked:{duplicate.Bot.BotGuid}", deleteMessage, LogLevel.Warn, TimeSpan.FromSeconds(30));
                    else
                        ModEntry.instance.Monitor.Log(deleteMessage, LogLevel.Warn);
                    summary.DeletedBrickedDuplicates++;
                    if (!dryRun)
                        DeleteBrickedDuplicate(duplicate.Bot);
                    continue;
                }

                string quarantineReason = GetDuplicateQuarantineReason(duplicate, canonical, group);
                if (quarantineReason == "valuable inventory")
                    summary.SkippedValuableDuplicates++;
                summary.QuarantinedDuplicates++;

                string quarantineMessage = $"Bot dedupe {(dryRun ? "DRYRUN would quarantine" : "quarantining")} duplicate reason={quarantineReason}: {DescribeBotSnapshot(duplicate)}";
                if (automatic)
                    LogOncePerInterval($"dedupe-ambiguous:{duplicate.Bot.BotGuid}", quarantineMessage, LogLevel.Warn, TimeSpan.FromSeconds(30));
                else
                    ModEntry.instance.Monitor.Log(quarantineMessage, LogLevel.Warn);
                if (!automatic)
                    ReportBotInventory(duplicate.Bot);
                if (!dryRun && !automatic)
                    QuarantineBot(duplicate.Bot, $"Duplicate {duplicateKind} not deleted: {quarantineReason}; canonical guid={canonical.Bot.BotGuid}.");
            }
        }

        private BotSnapshot ChooseCanonicalBot(List<BotSnapshot> candidates)
        {
            return candidates
                .OrderByDescending(snapshot => HasValuableInventory(snapshot.Bot) ? 100000 : 0)
                .ThenByDescending(snapshot => snapshot.IsPlaced ? 10000 : 0)
                .ThenByDescending(snapshot => snapshot.InSupervisorState ? 5000 : 0)
                .ThenByDescending(snapshot => snapshot.IsTracked ? 2500 : 0)
                .ThenByDescending(snapshot => snapshot.Bot.shell != null ? 1500 : 0)
                .ThenBy(snapshot => snapshot.Bot.IsQuarantined ? 1 : 0)
                .ThenByDescending(snapshot => snapshot.Bot.currentLocation != null ? 1000 : 0)
                .ThenByDescending(snapshot =>
                    knownCanonicalByGuid.TryGetValue(snapshot.Bot.BotGuid, out var known)
                    && ReferenceEquals(known, snapshot.Bot) ? 500 : 0)
                .ThenBy(snapshot => snapshot.Bot.CreatedTick)
                .FirstOrDefault();
        }

        private bool IsSafeDuplicateDelete(BotSnapshot duplicate, BotSnapshot canonical, List<BotSnapshot> group)
        {
            if (duplicate == null || canonical == null || ReferenceEquals(duplicate.Bot, canonical.Bot))
                return false;
            if (duplicate.Classification != "BrickedDuplicate")
                return false;
            if (HasValuableInventory(duplicate.Bot))
                return false;
            if (duplicate.InSupervisorState)
                return false;
            if (duplicate.IsStored)
                return false;
            if (!duplicate.IsPlaced)
                return false;
            if (group.Count(snapshot => snapshot.IsPlaced) <= 1 && duplicate.IsPlaced)
                return false;
            if (group.Count <= 1)
                return false;

            bool sameName = string.Equals(duplicate.Bot.name, canonical.Bot.name, StringComparison.OrdinalIgnoreCase);
            return sameName;
        }

        private bool IsBrickedDuplicate(BotSnapshot duplicate, BotSnapshot canonical, List<BotSnapshot> group)
        {
            if (duplicate == null || canonical == null || ReferenceEquals(duplicate.Bot, canonical.Bot))
                return false;

            if (!duplicate.IsPlaced || duplicate.IsStored || duplicate.InSupervisorState)
                return false;

            if (HasValuableInventory(duplicate.Bot))
                return false;

            bool sameName = string.Equals(duplicate.Bot.name, canonical.Bot.name, StringComparison.OrdinalIgnoreCase);
            if (!sameName)
                return false;

            if (!IsFunctionalWorldBotSnapshot(canonical, isCanonical: true))
                return false;

            if (string.Equals(duplicate.Bot.BotGuid, canonical.Bot.BotGuid, StringComparison.OrdinalIgnoreCase))
                return false;

            if (group.Count(snapshot => string.Equals(snapshot.Bot.name, duplicate.Bot.name, StringComparison.OrdinalIgnoreCase)) <= 1)
                return false;

            return true;
        }

        private void DeleteBrickedDuplicate(BotObject bot)
        {
            if (bot == null)
                return;

            var beforeSnapshots = BuildBotSnapshots(validateFirst: false);
            ModEntry.instance.Monitor.Log(
                $"Bot dedupe before remove: total={beforeSnapshots.Count} world={beforeSnapshots.Count(snapshot => snapshot.IsPlaced)} tracked={beforeSnapshots.Count(snapshot => snapshot.IsTracked)} target={bot.name} guid={bot.BotGuid}.",
                LogLevel.Warn);
            ClearBotScriptQueue(bot);
            CancelSupervisorPlan(bot);
            RemoveBotFromWorld(bot);
            RemoveBotFromRegistries(bot);
            bot.IsQuarantined = true;
            bot.modData[ModEntry.GetModDataKey("duplicateCleanup")] = "removed-bricked-duplicate";
            var afterSnapshots = BuildBotSnapshots(validateFirst: false);
            ModEntry.instance.Monitor.Log(
                $"Bot dedupe after remove: total={afterSnapshots.Count} world={afterSnapshots.Count(snapshot => snapshot.IsPlaced)} tracked={afterSnapshots.Count(snapshot => snapshot.IsTracked)} target={bot.name} guid={bot.BotGuid}.",
                LogLevel.Warn);
        }

        private void RemoveBotFromRegistries(BotObject bot)
        {
            if (bot == null)
                return;

            BotManager.instances.RemoveAll(existing => ReferenceEquals(existing, bot));
            foreach (var remoteBots in BotManager.remoteInstances.Values)
                remoteBots.RemoveAll(existing => ReferenceEquals(existing, bot));
        }

        private bool HasValuableInventory(BotObject bot)
        {
            if (bot?.inventory == null)
                return false;

            foreach (var item in bot.inventory)
            {
                if (item == null)
                    continue;

                if (item is Tool tool)
                {
                    if (tool.UpgradeLevel > 0)
                        return true;
                    if (!IsDefaultTool(tool))
                        return true;
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool IsDefaultTool(Tool tool)
        {
            return tool is Axe
                or Hoe
                or Pickaxe
                or WateringCan
                || (tool is MeleeWeapon weapon && weapon.isScythe());
        }

        private string DescribeBotSnapshot(BotSnapshot snapshot)
        {
            if (snapshot == null)
                return "(null)";

            string loc = snapshot.Location?.NameOrUniqueName
                ?? snapshot.Bot.currentLocation?.NameOrUniqueName
                ?? "(null)";
            string tile = $"{snapshot.Tile.X},{snapshot.Tile.Y}";
            string storage = snapshot.IsStored ? $" stored={snapshot.Container}" : "";
            return $"{snapshot.Bot.name} guid={snapshot.Bot.BotGuid} class={snapshot.Classification} reason={GetBotClassificationReason(snapshot)} loc={loc} tile={tile} tracked={snapshot.IsTracked} placed={snapshot.IsPlaced}{storage} valuable={HasValuableInventory(snapshot.Bot)}";
        }

        private string GetBotClassificationReason(BotSnapshot snapshot)
        {
            if (snapshot == null)
                return "none";

            if (snapshot.Classification == "BrickedDuplicate")
                return "duplicate-name, non-canonical, empty, placed-world-object, not supervisor-active";

            if (snapshot.Classification == "StoredBot")
                return "stored";

            if (snapshot.Classification == "FunctionalWorldBot")
                return "canonical, placed, registered, supervisor-eligible";

            if (snapshot.Classification == "PlacedButNotRegistered")
                return "placed world object missing registry";

            if (snapshot.Classification == "RegisteredGhost")
                return "registered but not placed";

            if (snapshot.Classification == "QuarantinedDuplicate")
                return "quarantined duplicate";

            return "ambiguous";
        }

        private string GetDuplicateQuarantineReason(BotSnapshot duplicate, BotSnapshot canonical, List<BotSnapshot> group)
        {
            if (duplicate == null)
                return "ambiguous";
            if (HasValuableInventory(duplicate.Bot))
                return "valuable inventory";
            if (duplicate.InSupervisorState)
                return "supervisor-active";
            if (duplicate.IsStored)
                return "stored";
            if (group.Count <= 1)
                return "only copy of that name";
            if (canonical != null && string.Equals(duplicate.Bot.BotGuid, canonical.Bot.BotGuid, StringComparison.OrdinalIgnoreCase))
                return "duplicate GUID uncertainty";
            if (!duplicate.IsPlaced)
                return "not placed world object";
            return "ambiguous";
        }

        public void RecallBotsHome()
        {
            AssignMissingBotIdentities();
            var farm = Game1.getLocationFromName("Farm");
            if (farm == null)
            {
                ModEntry.instance.Monitor.Log("RecallBotsHome refused: Farm location unavailable.", LogLevel.Warn);
                return;
            }

            foreach (var snapshot in BuildBotSnapshots(validateFirst: false)
                .Where(snapshot => snapshot.Classification == "FunctionalWorldBot")
                .Where(snapshot => namedHomeTiles.ContainsKey(snapshot.Bot.name ?? ""))
                .ToList())
            {
                var bot = snapshot.Bot;
                var homeTile = namedHomeTiles[bot.name];
                if (!TryFindSafeRelocationTile(bot, farm, homeTile, out var targetTile))
                {
                    ModEntry.instance.Monitor.Log(
                        $"RecallBotsHome refused for {bot.name} guid={bot.BotGuid}: no safe tile near home {homeTile.X},{homeTile.Y}.",
                        LogLevel.Warn);
                    continue;
                }
                ModEntry.instance.Monitor.Log(
                    $"RecallBotsHome moving functional world bot {bot.name} guid={bot.BotGuid} from {snapshot.Location?.NameOrUniqueName ?? "(null)"} {snapshot.Tile.X},{snapshot.Tile.Y}.",
                    LogLevel.Warn);
                RelocateExistingBotInstance(bot, farm, targetTile, "RecallBotsHome");
            }
        }

        public void ReportAllBots()
        {
            var player = Game1.player;
            var playerLoc = player.currentLocation;
            var playerPos = player.position;

            foreach (var bot in BotManager.GetAllBots())
            {
                if (bot == null)
                {
                    ModEntry.instance.Monitor.Log("Bot report: null bot");
                    continue;
                }
                if (bot.farmer == null)
                {
                    ModEntry.instance.Monitor.Log("Bot report: null bot farmer");
                    continue;
                }
                EnsureInventorySlots(bot);
                string locName = bot.currentLocation?.NameOrUniqueName ?? "(null)";
                var tile = bot.TileLocation;

                string relative = "";
                if (bot.currentLocation == playerLoc)
                {
                    int dx = (int)(tile.X - playerPos.X / 64);
                    int dy = (int)(tile.Y - playerPos.Y / 64);
                    relative = $" relative dx={dx}, dy={dy}";
                }
                ReportBotInventory(bot);
                string msg = $"{bot.name}: {locName} tile {(int)tile.X},{(int)tile.Y} facing {bot.facingDirection}{relative}";
                ModEntry.instance.Monitor.Log(msg);
                Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.newQuest_type));
            }
        }

        public void ReportAllBotPersistenceState()
        {
            var snapshots = BuildBotSnapshots(validateFirst: true);

            ModEntry.instance.Monitor.Log("=== Farmtronics Bot Persistence Report ===", LogLevel.Warn);
            ModEntry.instance.Monitor.Log(
                $"Tracked bots: {snapshots.Count(snapshot => snapshot.IsTracked)}; " +
                $"physical world bots: {snapshots.Count(snapshot => snapshot.IsPlaced)}; " +
                $"stored bots: {snapshots.Count(snapshot => snapshot.IsStored)}.",
                LogLevel.Warn);

            ModEntry.instance.Monitor.Log("-- All classified bots --", LogLevel.Warn);
            foreach (var snapshot in snapshots.OrderBy(snapshot => snapshot.Bot.name).ThenBy(snapshot => snapshot.Bot.BotGuid))
            {
                ModEntry.instance.Monitor.Log(DescribeBotSnapshot(snapshot), LogLevel.Warn);
                ReportBotInventory(snapshot.Bot);
            }

            LogDuplicateSummary(snapshots);
            CleanupStaleReservations(Game1.currentGameTime.TotalGameTime);
            LogReservationReport(Game1.currentGameTime.TotalGameTime);
            LogOrdersReport();

            ModEntry.instance.Monitor.Log("=== End Farmtronics Bot Persistence Report ===", LogLevel.Warn);
        }

        private void LogOrdersReport()
        {
            ModEntry.instance.Monitor.Log("-- Bot orders --", LogLevel.Warn);
            foreach (var bot in BotManager.GetAllBots().Where(bot => bot != null).OrderBy(bot => bot.name))
            {
                var orders = GetOrdersForBot(bot);
                ModEntry.instance.Monitor.Log(
                    $"  {bot.name}: mode={orders.Mode} capabilities={FormatCapabilities(orders.Capabilities)} zones={FormatZones(orders)} idleReason={botStates.Values.FirstOrDefault(state => ReferenceEquals(state.Bot, bot))?.LastNoPlanReason ?? "(none)"}",
                    LogLevel.Warn);
            }

            ModEntry.instance.Monitor.Log("-- Bot zones --", LogLevel.Warn);
            if (botZones.Count == 0)
            {
                ModEntry.instance.Monitor.Log("  none", LogLevel.Warn);
                return;
            }

            foreach (var zone in botZones.Values.OrderBy(zone => zone.Name))
            {
                ModEntry.instance.Monitor.Log(
                    $"  {zone.Name}: loc={zone.LocationName} rect={zone.Bounds.Left},{zone.Bounds.Top}..{zone.Bounds.Right - 1},{zone.Bounds.Bottom - 1}",
                    LogLevel.Warn);
            }
        }

        private void LogDuplicateSummary(List<BotSnapshot> snapshots)
        {
            foreach (var group in snapshots
                .GroupBy(snapshot => snapshot.Bot.name ?? snapshot.Bot.Name ?? "(unnamed)", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                ModEntry.instance.Monitor.Log(
                    $"duplicate name: {group.Key} count={group.Count()} canonical={DescribeBotSnapshot(ChooseCanonicalBot(group.ToList()))}",
                    LogLevel.Warn);
            }

            foreach (var group in snapshots
                .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Bot.BotGuid))
                .GroupBy(snapshot => snapshot.Bot.BotGuid, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                ModEntry.instance.Monitor.Log(
                    $"duplicate guid: {group.Key} count={group.Count()} canonical={DescribeBotSnapshot(ChooseCanonicalBot(group.ToList()))}",
                    LogLevel.Warn);
            }

            foreach (var group in snapshots
                .Where(snapshot => snapshot.IsPlaced && snapshot.Location != null)
                .GroupBy(snapshot => $"{snapshot.Location.NameOrUniqueName}:{snapshot.Tile.X},{snapshot.Tile.Y}")
                .Where(group => group.Count() > 1))
            {
                ModEntry.instance.Monitor.Log(
                    $"duplicate physical tile: {group.Key} bots={string.Join(", ", group.Select(snapshot => snapshot.Bot.name + "/" + snapshot.Bot.BotGuid))}",
                LogLevel.Warn);
            }
        }

        public void MoveBotHere(string botName)
        {
            botName = NormalizeBotName(botName);
            var player = Game1.player;
            var location = player?.currentLocation;
            if (location == null)
            {
                ModEntry.instance.Monitor.Log("ft_bot_move_here refused: player location unavailable.", LogLevel.Warn);
                return;
            }

            var preferredTile = GetTileAhead(player.Tile, player.FacingDirection);
            RelocateNamedBot(botName, location, preferredTile, "ft_bot_move_here");
        }

        public void HandlePlayerWarped(WarpedEventArgs args)
        {
            if (args == null || !args.IsLocalPlayer)
                return;

            if (args.OldLocation is not MineShaft)
                return;

            var player = args.Player ?? Game1.player;
            var destination = args.NewLocation ?? player?.currentLocation ?? Game1.player?.currentLocation ?? Game1.getLocationFromName("Farm");
            if (destination == null)
                return;

            var preferredTile = player?.Tile ?? Game1.player?.Tile ?? Vector2.Zero;
            int moved = 0;

            foreach (var bot in BotManager.GetAllBots().Where(bot => ShouldFollowPlayerToMineLevel(bot, args.OldLocation)).ToList())
            {
                if (!TryFindSafeRelocationTile(bot, destination, preferredTile, out var targetTile))
                {
                    ModEntry.instance.Monitor.Log(
                        $"Mine follow skipped for {bot.name}: no safe tile near {destination.NameOrUniqueName} {preferredTile.X},{preferredTile.Y}.",
                        LogLevel.Warn);
                    continue;
                }

                RelocateExistingBotInstance(bot, destination, targetTile, "MineLevelFollow");
                moved++;
            }

            if (moved > 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"MineLevelFollow moved {moved} bot(s) from {args.OldLocation.NameOrUniqueName} to {destination.NameOrUniqueName}.",
                    LogLevel.Trace);
            }
        }

        private bool ShouldFollowPlayerToMineLevel(BotObject bot, GameLocation oldLocation)
        {
            if (bot == null || bot.IsQuarantined || oldLocation == null)
                return false;

            if (!ReferenceEquals(bot.currentLocation, oldLocation))
                return false;

            var orders = GetOrdersForBot(bot);
            if (orders.Mode == BotOrderMode.Follow)
                return true;

            return orders.Mode == BotOrderMode.Work
                && (orders.Capabilities & BotCapability.Mine) != 0;
        }

        public void SendBotHome(string botName)
        {
            botName = NormalizeBotName(botName);
            if (string.IsNullOrWhiteSpace(botName))
            {
                ModEntry.instance.Monitor.Log("ft_bot_send_home refused: missing bot name.", LogLevel.Warn);
                return;
            }

            if (!namedHomeTiles.TryGetValue(botName, out var homeTile))
            {
                ModEntry.instance.Monitor.Log(
                    $"ft_bot_send_home refused for {botName}: no configured home tile.",
                    LogLevel.Warn);
                return;
            }

            var farm = Game1.getLocationFromName("Farm");
            if (farm == null)
            {
                ModEntry.instance.Monitor.Log(
                    $"ft_bot_send_home refused for {botName}: Farm location unavailable.",
                    LogLevel.Warn);
                return;
            }

            RelocateNamedBot(botName, farm, homeTile, "ft_bot_send_home");
        }

        private void RelocateNamedBot(string botName, GameLocation destination, Vector2 preferredTile, string commandName)
        {
            botName = NormalizeBotName(botName);
            if (string.IsNullOrWhiteSpace(botName))
            {
                ModEntry.instance.Monitor.Log($"{commandName} refused: missing bot name.", LogLevel.Warn);
                return;
            }

            AssignMissingBotIdentities();
            var snapshots = BuildBotSnapshots(validateFirst: false);
            var matching = snapshots
                .Where(snapshot => string.Equals(NormalizeBotName(snapshot.Bot.name), botName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var functional = matching
                .Where(snapshot => snapshot.Classification == "FunctionalWorldBot")
                .ToList();
            var selected = functional.Count == 1 ? functional[0] : null;
            if (selected == null
                && matching.Count == 1
                && matching[0].Bot.IsQuarantined
                && matching[0].IsPlaced
                && !matching[0].IsStored)
            {
                selected = matching[0];
                ModEntry.instance.Monitor.Log(
                    $"{commandName}: recovering explicitly named quarantined bot {selected.Bot.name} guid={selected.Bot.BotGuid}.",
                    LogLevel.Warn);
            }
            var ambiguousSameName = matching
                .Where(snapshot => !ReferenceEquals(snapshot, selected)
                    && snapshot.Classification != "FunctionalWorldBot"
                    && snapshot.Classification != "BrickedDuplicate")
                .ToList();

            if (selected == null || ambiguousSameName.Count > 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"{commandName} refused for {botName}: found {functional.Count} canonical functional world candidate(s) and {ambiguousSameName.Count} ambiguous same-name candidate(s) among {matching.Count} matching bot snapshot(s). Run ft_bot_report / ft_bot_dedupe first.",
                    LogLevel.Warn);
                foreach (var snapshot in matching)
                    ModEntry.instance.Monitor.Log($"  candidate: {DescribeBotSnapshot(snapshot)}", LogLevel.Warn);
                return;
            }

            if (selected.IsStored)
            {
                ModEntry.instance.Monitor.Log(
                    $"{commandName} refused for {botName}: selected bot is stored in {selected.Container}.",
                    LogLevel.Warn);
                return;
            }

            if (!TryFindSafeRelocationTile(selected.Bot, destination, preferredTile, out var targetTile))
            {
                ModEntry.instance.Monitor.Log(
                    $"{commandName} refused for {botName}: no safe destination tile near {destination.NameOrUniqueName} {preferredTile.X},{preferredTile.Y}.",
                    LogLevel.Warn);
                return;
            }

            RelocateExistingBotInstance(selected.Bot, destination, targetTile, commandName);
        }

        private void RelocateExistingBotInstance(BotObject bot, GameLocation destination, Vector2 targetTile, string reason)
        {
            var oldEntry = ScanWorldForBotObjects().FirstOrDefault(entry => ReferenceEquals(entry.Bot, bot));
            string oldLocationName = oldEntry?.Location?.NameOrUniqueName ?? bot.currentLocation?.NameOrUniqueName ?? "(null)";
            Vector2 oldTile = oldEntry?.Tile ?? bot.TileLocation;

            _ = bot.BotGuid;
            ClearBotScriptQueue(bot);
            CancelSupervisorPlan(bot);

            if (oldEntry != null
                && oldEntry.Location.objects.TryGetValue(oldEntry.Tile, out StardewValley.Object oldObject)
                && ReferenceEquals(oldObject, bot))
            {
                oldEntry.Location.objects.Remove(oldEntry.Tile);
            }
            else
            {
                RemoveBotFromWorld(bot);
            }

            bot.currentLocation = destination;
            bot.TileLocation = targetTile;
            bot.Position = targetTile.GetAbsolutePosition();
            ClearBotQuarantine(bot, reason);
            bot.data.Update();
            destination.objects[targetTile] = bot;
            BotManager.RegisterLocalBot(bot, reason);
            UpdateSupervisorStateAfterRelocation(bot, targetTile);

            ModEntry.instance.Monitor.Log(
                $"{reason}: moved {bot.name} guid={bot.BotGuid} from {oldLocationName} {oldTile.X},{oldTile.Y} to {destination.NameOrUniqueName} {targetTile.X},{targetTile.Y}.",
                LogLevel.Warn);
            ReportBotInventory(bot);
        }

        private void UpdateSupervisorStateAfterRelocation(BotObject bot, Vector2 targetTile)
        {
            var now = Game1.currentGameTime.TotalGameTime;
            var state = botStates.Values.FirstOrDefault(state => ReferenceEquals(state.Bot, bot));
            if (state == null)
                return;

            state.Bot = bot;
            ClearCurrentPlan(state);
            state.Mode = BotMode.Planning;
            state.LastObservedTile = targetTile;
            state.LastMovementAt = now;
            state.NextAllowedPlanAt = now + TimeSpan.FromSeconds(1);
            state.LastNoPlanReason = "Bot relocated safely.";
        }

        private bool TryFindSafeRelocationTile(BotObject bot, GameLocation location, Vector2 preferredTile, out Vector2 safeTile)
        {
            if (IsRelocationTileSafeForBot(bot, location, preferredTile))
            {
                safeTile = preferredTile;
                return true;
            }

            for (int radius = 1; radius <= 12; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                            continue;

                        var candidate = preferredTile + new Vector2(dx, dy);
                        if (IsRelocationTileSafeForBot(bot, location, candidate))
                        {
                            safeTile = candidate;
                            return true;
                        }
                    }
                }
            }

            safeTile = Vector2.Zero;
            return false;
        }

        private bool IsRelocationTileSafeForBot(BotObject bot, GameLocation location, Vector2 tile)
        {
            if (!IsTileSafeForBot(bot, location, tile))
                return false;

            if (location.terrainFeatures.ContainsKey(tile))
                return false;

            if (location.largeTerrainFeatures.Count > 0)
            {
                var tileRect = new Rectangle((int)tile.X * Game1.tileSize, (int)tile.Y * Game1.tileSize, Game1.tileSize, Game1.tileSize);
                if (location.largeTerrainFeatures.Any(feature => feature.getBoundingBox().Intersects(tileRect)))
                    return false;
            }

            bool occupiedBySelf = location.objects.TryGetValue(tile, out StardewValley.Object objAtTile)
                && ReferenceEquals(objAtTile, bot);
            if (!occupiedBySelf && location.IsTileOccupiedBy(tile))
                return false;

            return true;
        }

        public void PurgeExtraBotObjects()
        {
            ModEntry.instance.Monitor.Log(
                "PurgeExtraBotObjects is now a non-destructive bot safety scan/quarantine.",
                LogLevel.Warn);
            ValidateBotPersistence();
            RecallBotsHome();
        }

        private void PlaceDesiredBotsNearPlayer(Dictionary<string, BotObject> botsByName)
        {
            var player = Game1.player;
            var location = player.currentLocation;
            if (location == null) return;

            var playerTile = player.Tile;
            int offset = 1;

            foreach (var name in resetBotNames)
            {
                if (!botsByName.TryGetValue(name, out var bot))
                    continue;

                var targetTile = FindNearbyOpenTile(location, playerTile, offset);
                offset++;
                EnsureBotPlacedSafely(bot, location, targetTile);

                ModEntry.instance.Monitor.Log(
                    $"Requested reset bot placement for {bot.name} near {location.NameOrUniqueName} {targetTile.X},{targetTile.Y}.",
                    LogLevel.Warn);
            }
        }

        private static Vector2 FindNearbyOpenTile(GameLocation location, Vector2 playerTile, int preferredOffset)
        {
            var preferred = new Vector2(playerTile.X + preferredOffset, playerTile.Y);
            if (CanPlaceResetBot(location, preferred))
                return preferred;

            for (int radius = 1; radius <= 4; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                            continue;

                        var candidate = new Vector2(playerTile.X + dx, playerTile.Y + dy);
                        if (CanPlaceResetBot(location, candidate))
                            return candidate;
                    }
                }
            }

            return preferred;
        }

        private static bool CanPlaceResetBot(GameLocation location, Vector2 tile)
        {
            return location.isTileOnMap(tile)
                && !location.objects.ContainsKey(tile)
                && !location.IsTileOccupiedBy(tile);
        }

        private static void RemoveBotFromWorld(BotObject bot)
        {
            foreach (var location in Game1.locations
                .Concat(BotManager.GetAllBots().Where(existing => existing != null).Select(existing => existing.currentLocation))
                .Append(Game1.player?.currentLocation)
                .Where(location => location != null)
                .Distinct<GameLocation>(ReferenceEqualityComparer.Instance))
            {
                Vector2? oldTile = null;
                foreach (var pair in location.objects.Pairs)
                {
                    if (ReferenceEquals(pair.Value, bot))
                    {
                        oldTile = pair.Key;
                        break;
                    }
                }

                if (oldTile.HasValue)
                    location.objects.Remove(oldTile.Value);
            }
        }

        private void ClearBotScriptQueue(BotObject bot)
        {
            if (bot == null)
                return;

            var state = botStates.Values.FirstOrDefault(state => ReferenceEquals(state.Bot, bot));
            if (state != null)
                ReleaseReservationsForBot(state);

            bot.CancelCurrentAction();

            if (bot.shell == null)
                return;

            bot.shell.CancelCurrentCommand();

            LogOncePerInterval(
                $"clear-queue:{bot.BotGuid}",
                $"Cleared queued commands and stopped script for {bot.name}.",
                LogLevel.Trace,
                TimeSpan.FromSeconds(1));
        }

        public void WarpSameLocationBotsToPlayer()
        {
            ModEntry.instance.Monitor.Log(
                "WarpSameLocationBotsToPlayer disabled; recalling bots to separated home/safe tiles instead.",
                LogLevel.Warn);
            RecallBotsHome();
        }        
        public void WarpPlayerHome()
        {
            if (!Context.IsWorldReady)
                return;

            Game1.warpFarmer("FarmHouse", 64, 15, false);

            ModEntry.instance.Monitor.Log("Warped player home.");
            Game1.addHUDMessage(new HUDMessage("Warped home", HUDMessage.newQuest_type));
        }
        public void WarpPlayerToMines()
        {
            if (!Context.IsWorldReady)
                return;

            Game1.warpFarmer("Mine", 16, 10, false);

            ModEntry.instance.Monitor.Log("Warped player to mines.");
            Game1.addHUDMessage(new HUDMessage("Warped to mines", HUDMessage.newQuest_type));
        }
		private bool IsTargetIgnored(GameLocation location, Vector2 tile) {
			if (location == null) return false;
			string key = GetBlockedTargetKey(location, tile);
			return targetFailureCounts.TryGetValue(key, out int count) && count >= maxTargetFailures;
		}

		private bool IsTargetBlocked(GameLocation location, Vector2 tile, TimeSpan now) {
			string key = GetBlockedTargetKey(location, tile);
			if (!blockedTargets.TryGetValue(key, out TimeSpan blockedUntil)) return false;
			if (now >= blockedUntil) {
				blockedTargets.Remove(key);
				return false;
			}
			return true;
		}

		private void BlockTarget(GameLocation location, Vector2 tile, TimeSpan now) {
			if (location == null) return;
			blockedTargets[GetBlockedTargetKey(location, tile)] = now + blockedCooldown;
		}

		private void CleanupBlockedTargets(TimeSpan now) {
			if (blockedTargets.Count == 0) return;
			var expired = blockedTargets.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList();
			foreach (var key in expired) blockedTargets.Remove(key);
		}

		private static string GetBlockedTargetKey(GameLocation location, Vector2 tile) {
			string locationName = location?.NameOrUniqueName ?? "";
			return locationName + ":" + tile.X + "," + tile.Y;
		}

        private static string GetTileReservationKey(string locationName, Vector2 tile)
        {
            return $"{locationName ?? ""}:{(int)tile.X},{(int)tile.Y}";
        }

        private static string GetTileReservationKey(GameLocation location, Vector2 tile)
        {
            return GetTileReservationKey(location?.NameOrUniqueName ?? "", tile);
        }

        private static string GetBotReservationOwnerKey(BotObject bot)
        {
            if (!string.IsNullOrWhiteSpace(bot?.BotGuid))
                return bot.BotGuid;

            return bot?.name ?? "";
        }

        private static string GetReservationOwnerKey(BotReservation reservation)
        {
            if (!string.IsNullOrWhiteSpace(reservation?.BotGuid))
                return reservation.BotGuid;

            return reservation?.BotName ?? "";
        }

        private bool IsReservationOwnedBy(BotReservation reservation, BotObject bot)
        {
            if (reservation == null || bot == null)
                return false;

            return string.Equals(GetReservationOwnerKey(reservation), GetBotReservationOwnerKey(bot), StringComparison.OrdinalIgnoreCase);
        }

        private BotReservation CreateReservation(BotSupervisorState state, PendingPlan plan, Vector2 reservedTile, TimeSpan now)
        {
            return new BotReservation
            {
                BotName = state.Bot?.name ?? state.BotName,
                BotGuid = state.Bot?.BotGuid,
                JobType = plan.JobType.ToString(),
                LocationName = plan.LocationName,
                TargetTile = plan.TargetTile,
                StandTile = plan.StandTile,
                ReservedTile = reservedTile,
                CreatedAt = now,
                PlanId = plan.PlanId,
            };
        }

        private bool TryGetOtherReservation(
            Dictionary<string, BotReservation> reservations,
            GameLocation location,
            Vector2 tile,
            BotObject bot,
            out BotReservation reservation)
        {
            reservation = null;
            if (location == null)
                return false;

            return reservations.TryGetValue(GetTileReservationKey(location, tile), out reservation)
                && !IsReservationOwnedBy(reservation, bot);
        }

        private string GetJobReservationRejectionReason(BotSupervisorState state, BotJob job, GameLocation location)
        {
            var bot = state?.Bot;

            if (TryGetOtherReservation(ReservedTargetTiles, location, job.TargetTile, bot, out var targetReservation))
                return $"target reserved by {targetReservation.BotName}";

            if (TryGetOtherReservation(ReservedStandTiles, location, job.AdjacentTile, bot, out var standReservation))
                return $"stand reserved by {standReservation.BotName}";

            if (TryGetOtherReservation(ReservedDestinationTiles, location, job.AdjacentTile, bot, out var destinationReservation))
                return $"destination reserved by {destinationReservation.BotName}";

            if (IsTileOccupiedByAnotherBot(bot, location, job.AdjacentTile))
                return "destination occupied by another bot";

            if (IsOtherBotHomeTile(state, location, job.AdjacentTile, out var homeOwner))
                return $"destination is {homeOwner}'s home tile";

            var conflictingPlan = botStates.Values
                .Where(other => !ReferenceEquals(other, state))
                .FirstOrDefault(other => other.CurrentPlan != null
                    && string.Equals(other.CurrentPlan.LocationName, location?.NameOrUniqueName, StringComparison.Ordinal)
                    && other.CurrentPlan.TargetTile == job.TargetTile);

            if (conflictingPlan != null)
                return $"target is already planned by {conflictingPlan.Bot?.name ?? conflictingPlan.BotName}";

            conflictingPlan = botStates.Values
                .Where(other => !ReferenceEquals(other, state))
                .FirstOrDefault(other => other.CurrentPlan != null
                    && string.Equals(other.CurrentPlan.LocationName, location?.NameOrUniqueName, StringComparison.Ordinal)
                    && other.CurrentPlan.StandTile == job.AdjacentTile);

            if (conflictingPlan != null)
                return $"stand is already planned by {conflictingPlan.Bot?.name ?? conflictingPlan.BotName}";

            return null;
        }

        private bool IsOtherBotHomeTile(BotSupervisorState state, GameLocation location, Vector2 tile, out string ownerName)
        {
            ownerName = null;
            if (location?.NameOrUniqueName != "Farm")
                return false;

            foreach (var pair in namedHomeTiles)
            {
                if (pair.Value != tile)
                    continue;

                if (string.Equals(pair.Key, state?.Bot?.name ?? state?.BotName, StringComparison.OrdinalIgnoreCase))
                    continue;

                ownerName = pair.Key;
                return true;
            }

            return false;
        }

        private bool IsTileReservedByOther(BotObject bot, GameLocation location, Vector2 tile)
        {
            return TryGetOtherReservation(ReservedTargetTiles, location, tile, bot, out _)
                || TryGetOtherReservation(ReservedStandTiles, location, tile, bot, out _)
                || TryGetOtherReservation(ReservedDestinationTiles, location, tile, bot, out _)
                || TryGetOtherReservation(ReservedHomeTiles, location, tile, bot, out _);
        }

        private bool TryReservePlanForBot(BotSupervisorState state, PendingPlan plan, TimeSpan now, out string reason)
        {
            reason = null;
            if (state?.Bot == null || plan == null)
            {
                reason = "missing bot or plan";
                return false;
            }

            var location = state.Bot.currentLocation;
            var pseudoJob = new BotJob
            {
                Type = plan.JobType,
                TargetTile = plan.TargetTile,
                AdjacentTile = plan.StandTile,
                TargetName = plan.TargetName,
            };

            reason = GetJobReservationRejectionReason(state, pseudoJob, location);
            if (reason != null)
                return false;

            var targetKey = GetTileReservationKey(plan.LocationName, plan.TargetTile);
            var standKey = GetTileReservationKey(plan.LocationName, plan.StandTile);
            var reservation = CreateReservation(state, plan, plan.TargetTile, now);
            ReservedTargetTiles[targetKey] = reservation;
            ReservedStandTiles[standKey] = CreateReservation(state, plan, plan.StandTile, now);
            ReservedDestinationTiles[standKey] = CreateReservation(state, plan, plan.StandTile, now);

            ModEntry.instance.Monitor.Log(
                $"Reserved job for {state.Bot.name}: {plan.JobType} target={(int)plan.TargetTile.X},{(int)plan.TargetTile.Y} stand={(int)plan.StandTile.X},{(int)plan.StandTile.Y}.",
                LogLevel.Trace);
            return true;
        }

        private void ReleaseReservationsForBot(BotSupervisorState state)
        {
            if (state?.Bot == null && string.IsNullOrWhiteSpace(state?.BotName))
                return;

            string ownerKey = state.Bot != null ? GetBotReservationOwnerKey(state.Bot) : state.BotName;
            var released = new List<BotReservation>();

            ReleaseReservationsForOwner(ReservedTargetTiles, ownerKey, released);
            ReleaseReservationsForOwner(ReservedStandTiles, ownerKey, released);
            ReleaseReservationsForOwner(ReservedDestinationTiles, ownerKey, released);
            ReleaseReservationsForOwner(ReservedHomeTiles, ownerKey, released);

            if (released.Count == 0)
                return;

            var first = released[0];
            ModEntry.instance.Monitor.Log(
                $"Released reservations for {first.BotName}: target={(int)first.TargetTile.X},{(int)first.TargetTile.Y} stand={(int)first.StandTile.X},{(int)first.StandTile.Y}.",
                LogLevel.Trace);
        }

        private static void ReleaseReservationsForOwner(
            Dictionary<string, BotReservation> reservations,
            string ownerKey,
            List<BotReservation> released)
        {
            var keys = reservations
                .Where(pair => string.Equals(GetReservationOwnerKey(pair.Value), ownerKey, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in keys)
            {
                released.Add(reservations[key]);
                reservations.Remove(key);
            }
        }

        private void ClearCurrentPlan(BotSupervisorState state)
        {
            ReleaseReservationsForBot(state);
            if (state != null)
                state.CurrentPlan = null;
        }

        private void CleanupStaleReservations(TimeSpan now)
        {
            CleanupStaleReservations(ReservedTargetTiles, now);
            CleanupStaleReservations(ReservedStandTiles, now);
            CleanupStaleReservations(ReservedDestinationTiles, now);
            CleanupStaleReservations(ReservedHomeTiles, now);
        }

        private void CleanupStaleReservations(Dictionary<string, BotReservation> reservations, TimeSpan now)
        {
            if (reservations.Count == 0)
                return;

            var staleKeys = reservations
                .Where(pair => IsReservationStale(pair.Value, now))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                var reservation = reservations[key];
                reservations.Remove(key);
                ModEntry.instance.Monitor.Log(
                    $"Cleaned stale reservation for {reservation.BotName}: {reservation.JobType} loc={reservation.LocationName} tile={(int)reservation.ReservedTile.X},{(int)reservation.ReservedTile.Y}.",
                    LogLevel.Trace);
            }
        }

        private bool IsReservationStale(BotReservation reservation, TimeSpan now)
        {
            if (reservation == null)
                return true;

            if (now - reservation.CreatedAt > reservationTimeout)
                return true;

            var state = botStates.Values.FirstOrDefault(state =>
                string.Equals(GetBotReservationOwnerKey(state.Bot), GetReservationOwnerKey(reservation), StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.BotName, reservation.BotName, StringComparison.OrdinalIgnoreCase));

            if (state?.Bot == null)
                return true;

            var plan = state.CurrentPlan;
            if (plan == null)
                return true;

            if (state.Mode == BotMode.Idle || state.Mode == BotMode.Cooldown || state.Mode == BotMode.Paused)
                return true;

            if (!string.Equals(state.Bot.currentLocation?.NameOrUniqueName, reservation.LocationName, StringComparison.Ordinal))
                return true;

            if (!string.Equals(plan.PlanId, reservation.PlanId, StringComparison.Ordinal))
                return true;

            return false;
        }

        private void LogReservationReport(TimeSpan now)
        {
            var allReservations = ReservedTargetTiles.Values
                .Concat(ReservedStandTiles.Values)
                .Concat(ReservedDestinationTiles.Values)
                .Concat(ReservedHomeTiles.Values)
                .GroupBy(reservation => $"{reservation.PlanId}:{reservation.LocationName}:{reservation.BotName}:{reservation.TargetTile}:{reservation.StandTile}")
                .Select(group => group.First())
                .ToList();

            ModEntry.instance.Monitor.Log("-- Active bot reservations --", LogLevel.Warn);
            if (allReservations.Count == 0)
            {
                ModEntry.instance.Monitor.Log("  none", LogLevel.Warn);
                return;
            }

            foreach (var reservation in allReservations.OrderBy(r => r.LocationName).ThenBy(r => r.BotName))
            {
                ModEntry.instance.Monitor.Log(
                    $"  {reservation.LocationName}: owner={reservation.BotName} guid={reservation.BotGuid} job={reservation.JobType} target={(int)reservation.TargetTile.X},{(int)reservation.TargetTile.Y} stand={(int)reservation.StandTile.X},{(int)reservation.StandTile.Y} age={(now - reservation.CreatedAt).TotalSeconds:0}s stale={IsReservationStale(reservation, now)}",
                    LogLevel.Warn);
            }
        }

		private static bool IsSupervisorPassable(GameLocation location, Vector2 tile) {
            if (!IsWithinMap(location, tile)) return false;

            ValMap info = TileInfo.GetInfo(location, tile);
            try
            {
                if (info == null)
                {
                    //ModEntry.instance.Monitor.Log(
                        //$"Tile info missing for {location.NameOrUniqueName} at {tile.X},{tile.Y}",
                        //LogLevel.Warn);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ModEntry.instance.Monitor.Log(
                    $"Error getting tile info for {location.NameOrUniqueName} at {tile.X},{tile.Y}: {ex}",
                    LogLevel.Error);
                return true;
            }

            string typeName = info.GetString("type");
            string objectName = info.GetString("name");

            if (typeName == null && objectName == null) return true;

            // SMAPI/Farmtronics may report Building passable,
            // but bots cannot path through structures.
            if (typeName == "Building" || objectName == "Building") return false;

            // Also probably not pathable as ordinary walk tiles.
            if (typeName == "Bush" || objectName == "Bush") return false;

            return TileInfo.IsPassable(location, tile);
        }

	}
}
