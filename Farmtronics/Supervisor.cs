using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Farmtronics.Bot;
using Farmtronics.M1;
using Farmtronics.Utils;
using Microsoft.Xna.Framework;
using Miniscript;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Network;
using StardewValley.Tools;

namespace Farmtronics {
    internal sealed class Supervisor
    {

        private enum JobType
        {
            HarvestCrop,
            WaterCrop,
            PlantCrop,
            ClearDebris,
            ServiceMachine,
            TillSoil,
            MineBreakStone,
            MineCutWeeds,
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

            public Vector2 TargetTile { get; init; }
            public Vector2 StandTile { get; init; }
            public string TargetName { get; init; }
            public Vector2 AdjacentTile { get; init; }
            public Vector2 StartTile { get; init; }

            public string Script { get; init; }
            public TimeSpan? QueuedAt { get; set; }
            public int RepathAttempts { get; set; }
        }

        private sealed class JobMemory
        {
            public int FailureCount { get; set; }
            public TimeSpan SuppressedUntil { get; set; }
            public bool IgnoreForRun { get; set; }
            public string LastReason { get; set; }
        }

        private readonly Dictionary<string, JobMemory> jobMemory = new();
        private static readonly TimeSpan shortJobCooldown = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan mediumJobCooldown = TimeSpan.FromSeconds(60);
        private const int maxJobFailuresBeforeIgnore = 3;

        private SupervisorMode mode = SupervisorMode.Idle;
        private readonly Dictionary<string, BotSupervisorState> botStates = new();
        private readonly Dictionary<string, TimeSpan> blockedTargets = new();

        private sealed record MachineServiceMemory(
            MachineFingerprint Fingerprint,
            TimeSpan SuppressedUntil
        );

        private readonly Dictionary<Vector2, MachineServiceMemory> _machineServiceMemory = new();

        private bool IsControllableBot(BotObject bot)
        {
            if (bot == null)
                return false;

            bool hasAnyTool =
            bot.inventory.Any(item =>
                item is Tool);
            if (!hasAnyTool)
            {
                ModEntry.instance.Monitor.Log("Bot report: bot has no tools, giving tools");
                bot.farmer.Items.AddRange(Farmer.initialTools());

                // Inventory indices have to exist, since InventoryMenu exclusively uses them and can't assign items otherwise.
                for (int i = bot.farmer.Items.Count; i < bot.GetActualCapacity(); i++) {
                    bot.farmer.Items.Add(null);
                }
            }
            hasAnyTool =
            bot.inventory.Any(item =>
                item is Tool);
            if (!hasAnyTool)
            {
                ModEntry.instance.Monitor.Log("Bot report: bot has no tools, giving tools didn't work");
                return false;
            }
            return true;
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

		private static readonly HashSet<string> clearTypes = new() { "Tree", "Stone", "Twig", "Weeds", "Grass" };
		private static readonly TimeSpan blockedCooldown = TimeSpan.FromSeconds(6);
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
            ModEntry.instance.Monitor.Log("Supervisor reset.");
        }

        public void Stop()
        {
            mode = SupervisorMode.Idle;
            botStates.Clear();
            blockedTargets.Clear();
            targetFailureCounts.Clear();
            ModEntry.instance.Monitor.Log("Supervisor stopped.");
        }

		public void StartAllBots() {
			mode = SupervisorMode.AllBots;
			blockedTargets.Clear();
			targetFailureCounts.Clear();
			LoadBotStatesToSupervisor(singleBot: false);
			ModEntry.instance.Monitor.Log("Supervisor started: all-bots.");
		}
        public void Update(GameTime gameTime)
        {
            if (!Context.IsMainPlayer) return;
            if (!Context.IsWorldReady) return;
            if (mode == SupervisorMode.Idle) return;

            TimeSpan now = gameTime.TotalGameTime;
            CleanupBlockedTargets(now);

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
                    state.CurrentPlan = null;
                    state.NextAllowedPlanAt = now + TimeSpan.FromSeconds(30);
                    state.Mode = BotMode.Cooldown;
                    break;
            }
        }   
        private static readonly TimeSpan machineFailedAttemptCooldown = TimeSpan.FromSeconds(20);

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
                state.CurrentPlan = null;
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
                state.CurrentPlan = null;
                state.Mode = BotMode.Planning;
                return;
            }

            if (plan.JobType == JobType.TillSoil && GetBotTile(bot) != plan.StandTile)
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

                state.CurrentPlan = null;
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

                state.CurrentPlan = null;
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

                state.CurrentPlan = null;
                state.NextAllowedPlanAt = now + blockedCooldown;
                state.Mode = BotMode.Cooldown;
                return;
            }

            ModEntry.instance.Monitor.Log(
                $"Supervisor plan completed for {bot.name}: {plan.JobType} target {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");

            state.CurrentPlan = null;
            state.Mode = BotMode.Planning;
        }

        private List<BotJob> BuildJobsForLocation(GameLocation location, BotSupervisorState state)
        {
            return location switch
            {
                Farm farm => BuildFarmJobs(farm, state),
                MineShaft mine => BuildMineJobs(mine, state),
                Woods woods => BuildClearAllJobs(location, state),
                Mountain mountain => BuildClearAllJobs(location, state),
                _ => new List<BotJob>(),
            };
        }
        private List<BotJob> BuildClearAllJobs(GameLocation location, BotSupervisorState state)
        {
            var jobs = new List<BotJob>();

            int width = location.map.Layers[0].LayerWidth;
            int height = location.map.Layers[0].LayerHeight;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
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
                    }
                }
            }

            return jobs;
        }
        private List<BotJob> BuildFarmJobs(Farm farm, BotSupervisorState state)
        {
            var jobs = new List<BotJob>();

            int width = farm.map.Layers[0].LayerWidth;
            int height = farm.map.Layers[0].LayerHeight;
            // Machine Area
            for (int y = 10; y < 22; y++)
            {
                for (int x = 46; x < width; x++)
                {
                    var tile = new Vector2(x, y);
                    TimeSpan timestamp = new TimeSpan(DateTime.Now.Ticks);
                    if (TryBuildMachineJob(farm, tile, timestamp,  out BotJob machineJob)) {
                        jobs.Add(machineJob);
                        continue;
                    }  
                }
            }
            // Crop Area
            for (int y = 22; y < height; y++)
            {
                for (int x = 26; x < width; x++)
                {
                    var tile = new Vector2(x, y);

                    //ModEntry.instance.Monitor.Log($"Building job for tile {tile.X},{tile.Y}", LogLevel.Trace);


                    var adjacentTiles = GetAdjacentPassableTiles(farm, tile).ToList();
                    if (adjacentTiles.Count == 0){
                        //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} NO ADJACENT TILES", LogLevel.Trace);
                        continue;                     
                    }
                    if(IsHarvestableCropTile(farm, tile, out string targetName, out bool requiresScythe))  {
                        jobs.Add(new BotJob
                        {
                            Type = JobType.HarvestCrop,
                            JobKey = GetJobKey(farm, JobType.HarvestCrop, tile),
                            TargetName = targetName,
                            TargetTile = tile,
                            AdjacentTile = adjacentTiles.First(), // improve later
                            BasePriority = requiresScythe ? 120 : 110,
                        });   
                        //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} added: {targetName} \"HarvestCrop\"", LogLevel.Trace);
                        continue;                     
                    }
                    if(IsPlantCropTile(farm, tile, out string targetPlanting, out bool clearAhead, out bool canFertilize))  {
                        jobs.Add(new BotJob
                        {
                            Type = JobType.PlantCrop,
                            JobKey = GetJobKey(farm, JobType.PlantCrop, tile),
                            TargetName = targetPlanting,
                            TargetTile = tile,
                            AdjacentTile = adjacentTiles.First(), // improve later
                            BasePriority = 100 + (clearAhead ? 50 : 0) + (canFertilize ? 25 : 0),
                        });   
                        //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} added: {targetPlanting} \"PlantCrop\"", LogLevel.Trace);
                        continue;                                        
                    }
                    if (IsDryCropTile(farm, tile))  {
                        jobs.Add(new BotJob
                        {
                            Type = JobType.WaterCrop,
                            JobKey = GetJobKey(farm, JobType.WaterCrop, tile),
                            TargetName = "Dry Crop",
                            TargetTile = tile,
                            AdjacentTile = adjacentTiles.First(), // improve later
                            BasePriority = 100,
                        });   
                        //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} added: {targetName} \"WaterCrop\"", LogLevel.Trace);
                        continue;                                         
                    }      
                    if (IsClearableTile(farm, tile, out string harvestName)) {
                        jobs.Add(new BotJob
                        {
                            Type = JobType.ClearDebris,
                            JobKey = GetJobKey(farm, JobType.ClearDebris, tile),
                            TargetName = harvestName,
                            TargetTile = tile,
                            AdjacentTile = adjacentTiles.First(), // improve later
                            BasePriority = 100,
                        });
                        //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} added: {harvestName} \"ClearDebris\"", LogLevel.Trace);
                        continue;                     
                    }
                    //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} checking Tillable", LogLevel.Trace);                   
                    if (TryBuildTillSoilJob(farm, tile, out BotJob tillSoilJob))
                    {
                        jobs.Add(tillSoilJob);
                        //ModEntry.instance.Monitor.Log($"Job {tile.X},{tile.Y} added: {tillSoilJob.TargetName} \"TillSoil\"", LogLevel.Trace);
                        continue;                     
                    };      

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
            if (plan.JobType == JobType.TillSoil)
                return IsStillTillable(location, plan.TargetTile);

            return true;
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

            var allJobs = BuildJobsForLocation(bot.currentLocation, state).ToList();

            var jobs = new List<BotJob>();

            foreach (var job in allJobs)
            {
                TryGetAllowedMachinesForBot(bot.name, out var allowedMachines);
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

                if (!BotCanDoJob(state, job))
                {
                    LogRejectedJob(job, "bot cannot do job", score);
                    continue;
                }

                /*ModEntry.instance.Monitor.Log(
                    $"Accepted job: {job.GetType().Name} target={job.TargetTile.X},{job.TargetTile.Y} " +
                    $"name={job.TargetName} score={score}",
                    LogLevel.Trace);*/

                jobs.Add(job);
            }

            jobs = jobs
                .OrderByDescending(job => ScoreJobForBot(state, job))
                .ToList();


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
                    TargetTile = job.TargetTile,
                    StandTile = job.AdjacentTile,
                    TargetName = job.TargetName,
                    StartTile = bot.TileLocation,
                    Script = script,
                    QueuedAt = null,
                };

                state.Mode = BotMode.Queued;
                state.LastNoPlanReason = null;

                ModEntry.instance.Monitor.Log(
                    $"Supervisor created plan for {bot.name}: target tile {job.TargetTile.X},{job.TargetTile.Y} name {job.TargetName}");

                return;
            }

            state.CurrentPlan = null;
            state.Mode = BotMode.Idle;
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
        private bool BotHasInventoryCategory(BotObject bot, int category)
        {
            if (bot == null) return false;

            foreach (var item in bot.inventory) 
            {
                if (item == null) continue;
                if (item.Category == category)
                    return true;
            }

            return false;
        }
        private bool BotCanDoJob(BotSupervisorState state, BotJob job)
        {
            return job.Type switch
            {
                JobType.TillSoil =>
                    BotHasToolType(state.Bot, "Hoe"),

                JobType.PlantCrop =>
                    BotHasInventoryCategory(state.Bot, "Seed"),

                JobType.WaterCrop =>
                    BotHasToolType(state.Bot, "WateringCan"),

                JobType.ServiceMachine =>
                    BotCanServiceMachine(state, job),

                JobType.MineBreakStone =>   
                    BotHasToolType(state.Bot, "Pickaxe"),

                _ => true,
            };
        }
        private bool BotCanServiceMachine(BotSupervisorState state, BotJob job)
        {
            var botName = state.Bot?.name ?? "";

            // No machine role? Then this is a farm/general bot.
            // It should not do machine jobs.
            if (!TryGetAllowedMachinesForBot(botName, out var allowedMachines))
                return false;

            // Has a machine role, but not for this machine.
            if (!allowedMachines.Contains(job.TargetName))
                return false;

            // Has the role and the needed input.
            return BotHasMachineInput(state.Bot, job);
        }
        private bool BotHasMachineInput(BotObject bot, BotJob job)
        {
            var botName = bot.name ?? "";

            if (TryGetAllowedMachinesForBot(botName, out var allowedMachines))
            {
                // Machine-limited bot can only service allowed machine names.
                if (!allowedMachines.Contains(job.TargetName))
                    return false;
            }
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
        private static readonly Dictionary<string, string> BotMachinePrefixes = new()
        {
            ["keg"] = "Keg",
            ["jar"] = "Preserves Jar",
            ["preserves"] = "Preserves Jar",
            ["seed"] = "Seed Maker",
            ["mayo"] = "Mayonnaise Machine",
            ["mayonnaise"] = "Mayonnaise Machine",
            ["cheese"] = "Cheese Press",
            ["furnace"] = "Furnace",
            ["kiln"] = "Charcoal Kiln"
        };

        private static readonly List<MachineRule> machineRules = new()
        {
            new()
            {
                MachineName = "Cheese Press",
                BasePriority = 750,
                InputRules =
                {
                    new MachineInputRule { Category = "Milk" },
                    new MachineInputRule { Name = "Milk" },
                    new MachineInputRule { Name = "Goat Milk" },
                }
            },
            new()
            {
                MachineName = "Keg",
                BasePriority = 750,
                InputRules =
                {
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

            state.CurrentPlan = null;
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
        private bool TryBuildTillSoilJob(Farm farm, Vector2 tile, out BotJob job)
        {
            job = null;

            if (farm.objects.TryGetValue(tile, out StardewValley.Object obj))
                return false;

            // Already has terrain feature.
            if (farm.terrainFeatures.TryGetValue(tile, out var feature))
            {
                // HoeDirt means already tilled.
                if (feature is StardewValley.TerrainFeatures.HoeDirt)
                    return false;

                // Anything else means blocked for now: tree, grass, etc.
                return false;
            }

            var diggable = farm.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back");

            if (diggable == null)
                return false;

            var adjacentTiles = GetAdjacentPassableTiles(farm, tile).ToList();
            if (adjacentTiles.Count == 0)
            {
                ModEntry.instance.Monitor.Log(
                    $"Tilling job rejected: no adjacent passable tile from {(int)tile.X},{(int)tile.Y}");
                return false;
            }

            job = new BotJob
            {
                Type = JobType.TillSoil,
                JobKey = GetJobKey(farm, JobType.TillSoil, tile),
                TargetName = "Empty for Tilling",
                TargetTile = tile,
                AdjacentTile = adjacentTiles.First(),
                BasePriority = 1,
            };

            return true;
        }
        private bool TryBuildMachineJob(Farm farm, Vector2 tile, TimeSpan now, out BotJob job)
        {
            job = null;

            if (!farm.objects.TryGetValue(tile, out StardewValley.Object obj))
                return false;

            // 1. Always allow ready machines through.
            // They have output waiting and should be harvested.
            if (IsReadyMachine(obj))
            {
                return TryCreateMachineJobFromRule(farm, tile, obj, out job);
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

            return TryCreateMachineJobFromRule(farm, tile, obj, out job);
        }
        private bool TryCreateMachineJobFromRule(Farm farm, Vector2 tile, StardewValley.Object obj, out BotJob job)
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

            ValMap info = TileInfo.GetInfo(farm, tile);

            if (info == null)
                return false;

            string type = info.GetString("type");
            string name = info.GetString("name");

            if (string.IsNullOrWhiteSpace(name))
                return false;

            var rule = machineRules.FirstOrDefault(r => r.MachineName == name);
            if (rule == null)
            {
                // Temporary: log named objects that were not matched.
                if (type == "Object" || type == "BigCraftable")
                {
                    ModEntry.instance.Monitor.Log(
                        $"Machine scan no rule for tile {(int)tile.X},{(int)tile.Y}: type='{type}' name='{name}'");
                }
                return false;
            }

            var adjacentTiles = GetAdjacentPassableTiles(farm, tile).ToList();
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
                JobKey = GetJobKey(farm, JobType.ServiceMachine, tile),
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
        private bool IsHarvestableCropTile(GameLocation location, Vector2 tile, out string harvestName, out bool requiresScythe)
        {
            harvestName = null;
            requiresScythe = false;

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

            bool canUseHarvest = crop.GetBool("harvestMethod");
            requiresScythe = !canUseHarvest
            || (crop.GetInt("indexOfHarvest", 0) == 771)    // grass is weird and doesn't set harvestMethod but does require scythe
            || (crop.GetInt("indexOfHarvest", 0) == 1279);  // mushroom grass also requires scythe without setting harvestMethod
            harvestName = requiresScythe ? "Harvestable Crop (Scythe)" : "Harvestable Crop";
            return true;
        }

        private bool IsPlantCropTile(GameLocation location, Vector2 tile, out string plantName, out bool clearAhead, out bool canFertilize)
        {
            plantName = null;
            clearAhead = false;
            canFertilize = false;

			ValMap info = TileInfo.GetInfo(location, tile);
			if (info == null) return false;
            info.map.TryGetValue(new ValString("type"), out Value typeValue);
            if (typeValue == null || typeValue is ValNull || typeValue.ToString() != "HoeDirt") return false;

            info.map.TryGetValue(new ValString("crop"), out Value cropValue);

            if (cropValue is ValMap crop) {
                if (crop.GetBool("dead")) {
                    clearAhead = true;
                } else {
                    return false;
                }
            }

            bool hasFertilizer = info.GetBool("hasFertilizer");
            bool isTilled = info.GetBool("tilled");

            canFertilize = !hasFertilizer && isTilled;

            plantName = clearAhead ? "Plantable Crop (Clear Ahead)" : "Plantable Crop";

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
                    state.CurrentPlan = null;
                    state.Mode = BotMode.Planning;
                    return false;
                }
                
            return plan.JobType switch
            {
                JobType.TillSoil => IsStillTillable(bot.currentLocation, plan.TargetTile),
                JobType.ClearDebris => IsClearableTile(bot.currentLocation, plan.TargetTile, out _),
                JobType.WaterCrop => IsDryCropTile(bot.currentLocation, plan.TargetTile),
                JobType.HarvestCrop => IsHarvestableCropTile(bot.currentLocation, plan.TargetTile, out _, out _),
                JobType.PlantCrop => IsPlantCropTile(bot.currentLocation, plan.TargetTile, out _, out _, out  _),
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
        "    wait 0",
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
            "    wait 0",
            "    return true"
        );
    }
    else
    {
        AddLines(lines,
            "    print \"Harvesting crop normally\"",
            "    me.harvest",
            "    wait 0",
            "    return true"
        );
    }
}
private static void AddPlantCropAction(List<string> lines, BotJob job)
{
    AddLines(lines,
        "    print \"Planting crop\"",
        "    if selectInventoryByCategory(\"Fertilizer\") then me.placeItem",
        "    seedName = selectInventoryByCategory(\"Seed\")",
        "    trellised = [\"Hops Starter\",\"Grape Starter\",\"Bean Starter\"]",
        "    if not seedName then",
        "        print \"No seeds\"",
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
        "    wait 0",
        "    return true"
    );
}

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
                bool hasAnyTool =
                bot.inventory.Any(item =>
                    item is Tool);
                if (!hasAnyTool)
                {
                    ModEntry.instance.Monitor.Log("Bot report: bot has no tools, giving tools");
                    bot.farmer.Items.AddRange(Farmer.initialTools());

                    // Inventory indices have to exist, since InventoryMenu exclusively uses them and can't assign items otherwise.
                    for (int i = bot.farmer.Items.Count; i < bot.GetActualCapacity(); i++) {
                        bot.farmer.Items.Add(null);
                    }
                }
                hasAnyTool =
                bot.inventory.Any(item =>
                    item is Tool);
                if (!hasAnyTool)
                {
                    ModEntry.instance.Monitor.Log("Bot report: bot has no tools, giving tools didn't work");
                    continue;
                }
                string locName = bot.currentLocation?.NameOrUniqueName ?? "(null)";
                var tile = bot.TileLocation;

                string relative = "";
                if (bot.currentLocation == playerLoc)
                {
                    int dx = (int)(tile.X - playerPos.X / 64);
                    int dy = (int)(tile.Y - playerPos.Y / 64);
                    relative = $" relative dx={dx}, dy={dy}";
                }

                string msg = $"{bot.name}: {locName} tile {(int)tile.X},{(int)tile.Y} facing {bot.facingDirection}{relative}";
                ModEntry.instance.Monitor.Log(msg);
                Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.newQuest_type));
            }
        }

        public void WarpSameLocationBotsToPlayer()
        {
            var player = Game1.player;
            var loc = player.currentLocation;
            var pos = player.Position;

            int offset = 1;

            foreach (var bot in BotManager.GetAllBots())
            {
                if (bot == null) continue;
                if (bot.currentLocation != loc) continue;
                bool inBotStates = false;
                foreach(var bss in botStates.Values) {
                  if (bss.Bot == bot) {
                    inBotStates = true;
                    break;
                  }
                }
                var targetPos = pos + new Vector2(offset * 64, 0);
                offset++;
                Vector2 targetTile;
                targetTile.X = (int)targetPos.X/64;
                targetTile.Y = (int)targetPos.Y/64;
                if (inBotStates) {
                    botStates[bot.name].Mode = BotMode.Planning;
                    botStates[bot.name].CurrentPlan = null;                   
                }
                bot.currentLocation = loc;
                bot.Position = targetPos; 
                bot.TileLocation = targetTile;
                ModEntry.instance.Monitor.Log(
                    $"Warped {bot.name} to {loc.NameOrUniqueName} tile {targetTile.X},{targetTile.Y}");
            }
            StartAllBots();
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
