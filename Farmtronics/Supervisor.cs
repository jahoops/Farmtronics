using System;
using System.Collections.Generic;
using System.Linq;
using Farmtronics.Bot;
using Farmtronics.M1;
using Farmtronics.Utils;
using Microsoft.Xna.Framework;
using Miniscript;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace Farmtronics {
    internal sealed class Supervisor
    {
        private enum SupervisorMode
        {
            Idle,
            AllBotsRockTest,
            SingleBotRockTest
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
            public Vector2 TargetTile { get; init; }
            public string TargetName { get; init; }
            public Vector2 AdjacentTile { get; init; }
            public Vector2 StartTile { get; init; }
            public string Script { get; init; }
            public TimeSpan? QueuedAt { get; set; }
		}

        private sealed class TargetCandidate
        {
            public string TargetName { get; init; }
            public Vector2 TargetTile { get; init; }
            public Vector2 AdjacentTile { get; init; }
            public float DistanceSquared { get; init; }
        }

        private SupervisorMode mode = SupervisorMode.Idle;
        private readonly Dictionary<string, BotSupervisorState> botStates = new();
        private readonly Dictionary<string, TimeSpan> blockedTargets = new();

        private void LoadBotStatesToSupervisor(bool singleBot = false)
        {
            botStates.Clear();

            foreach (var bot in BotManager.GetAllBots()
                .Where(candidate => candidate?.currentLocation is Farm))
            {
                bot.InitShell();

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
            {
                ModEntry.instance.Monitor.Log("Supervisor found no farm bots to supervise.");
            }
        }
        
		private static readonly HashSet<string> clearTypes = new() { "Grass", "Stone", "Twig", "Weeds" };
		private static readonly TimeSpan blockedCooldown = TimeSpan.FromSeconds(6);
		private static readonly TimeSpan stuckTimeout = TimeSpan.FromSeconds(2);
        private readonly Dictionary<string, int> targetFailureCounts = new();
        private static readonly int maxTargetFailures = 2;
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

		public void StartSingleBotRockTest()
        {
            mode = SupervisorMode.SingleBotRockTest;
            blockedTargets.Clear();
            targetFailureCounts.Clear();
            LoadBotStatesToSupervisor(singleBot: true);
            ModEntry.instance.Monitor.Log("Supervisor started: single-bot rock test.");
        }

		public void StartAllBotsRockTest() {
			mode = SupervisorMode.AllBotsRockTest;
			blockedTargets.Clear();
			targetFailureCounts.Clear();
			LoadBotStatesToSupervisor(singleBot: false);
			ModEntry.instance.Monitor.Log("Supervisor started: all-bots rock test.");
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

            var location = bot.currentLocation;
            if (location == null || location.NameOrUniqueName != "Farm")
            {
                state.Mode = BotMode.Paused;
                state.LastNoPlanReason = "Bot is not on the farm.";
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
                    if (now - state.LastMovementAt >= stuckTimeout)
                    {
                        ModEntry.instance.Monitor.Log($"Supervisor plan stuck for {bot.name}: no movement at {state.LastObservedTile.X},{state.LastObservedTile.Y} targeting {state.CurrentPlan.TargetTile.X},{state.CurrentPlan.TargetTile.Y} name {state.CurrentPlan.TargetName}");
                        bot.shell.Break(true);
                        FailPlan(state, now);
                        break;
                    }
                    string planStatus = GetPlanStatus(bot);
                    if (planStatus == "success")
                    {
                        var plan = state.CurrentPlan;
                        state.CurrentPlan = null;
                        if (IsClearableTile(bot.currentLocation, plan.TargetTile))
                        {
                            ModEntry.instance.Monitor.Log($"Supervisor: target {plan.TargetTile.X},{plan.TargetTile.Y} still clearable after attempt for {bot.name}; recording failure.");
                            RecordTargetFailure(bot.currentLocation, plan.TargetTile);
                            BlockTarget(bot.currentLocation, plan.TargetTile, now);
                            state.NextAllowedPlanAt = now + blockedCooldown;
                            state.Mode = BotMode.Cooldown;
                        }
                        else
                        {
                            ModEntry.instance.Monitor.Log($"Supervisor plan completed for {bot.name}: target {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");
                            state.Mode = BotMode.Planning;
                        }
                    }
                    else if (planStatus == "failed")
                    {
                        ModEntry.instance.Monitor.Log($"Supervisor plan failed for {bot.name}: target {state.CurrentPlan.TargetTile.X},{state.CurrentPlan.TargetTile.Y} name {state.CurrentPlan.TargetName}");
                        FailPlan(state, now);
                    }
                    break;

                case BotMode.Cooldown:
                    if (now >= state.NextAllowedPlanAt)
                    {
                        state.Mode = BotMode.Planning;
                        state.LastNoPlanReason = null;
                    }
                    break;
            }
        }   
		private IEnumerable<TargetCandidate> FindTargetCandidates(Farm farm, Vector2 fromTile) {
			int width = farm.map.Layers[0].LayerWidth;
			int height = farm.map.Layers[0].LayerHeight;

			for (int y = 0; y < height; y++) {
				for (int x = 0; x < width; x++) {
					var tile = new Vector2(x, y);
					if (!IsClearableTile(farm, tile, out string targetName)) continue;

					var adjacentTiles = GetAdjacentPassableTiles(farm, tile).ToList();
					if (adjacentTiles.Count == 0) continue;

					var adjacent = adjacentTiles.OrderBy(candidate => DistanceSquared(fromTile, candidate)).First();
					yield return new TargetCandidate {
						TargetName = targetName,
						TargetTile = tile,
						AdjacentTile = adjacent,
						DistanceSquared = DistanceSquared(fromTile, tile),
					};
				}
			}
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

            var farm = bot.currentLocation as Farm;
            if (farm == null)
            {
                state.Mode = BotMode.Paused;
                state.LastNoPlanReason = "Bot is not on a farm.";
                return;
            }

            var candidates = FindTargetCandidates(farm, bot.TileLocation)
                .OrderBy(candidate => candidate.DistanceSquared)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (IsTargetIgnored(farm, candidate.TargetTile))
                    continue;
                if (IsTargetBlocked(farm, candidate.TargetTile, now))
                    continue;

                var path = FindPath(farm, bot.TileLocation, candidate.AdjacentTile);
                if (path == null)
                {
                    BlockTarget(farm, candidate.TargetTile, now);
                    continue;
                }

                var script = BuildScript(
                    bot.TileLocation,
                    bot.facingDirection,
                    path,
                    candidate.TargetTile,
                    candidate.TargetName,
                    farm);

                if (string.IsNullOrWhiteSpace(script))
                {
                    BlockTarget(farm, candidate.TargetTile, now);
                    continue;
                }

                bot.InitShell();

                state.CurrentPlan = new PendingPlan
                {
                    TargetTile = candidate.TargetTile,
                    TargetName = candidate.TargetName,
                    StartTile = bot.TileLocation,
                    Script = script,
                    QueuedAt = null,
                };

                state.Mode = BotMode.Queued;
                state.LastNoPlanReason = null;

                ModEntry.instance.Monitor.Log(
                    $"Supervisor created plan for {bot.name}: target tile {candidate.TargetTile.X},{candidate.TargetTile.Y} name {candidate.TargetName}");

                return;
            }

            state.CurrentPlan = null;
            state.Mode = BotMode.Idle;
            state.LastNoPlanReason = "No reachable clearable target found.";

            ModEntry.instance.Monitor.Log(
                $"Supervisor has no plan for {bot.name}: {state.LastNoPlanReason}");
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

		private bool IsClearableTile(GameLocation location, Vector2 tile) {
			return IsClearableTile(location, tile, out _);
		}

		private IEnumerable<Vector2> GetAdjacentPassableTiles(GameLocation location, Vector2 tile) {
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

		private string BuildScript(Vector2 startTile, int startFacing, List<Vector2> path, Vector2 targetTile, string targetName, Farm farm) {
			var lines = new List<string>();
			int facing = startFacing;
			Vector2 current = startTile;

			lines.Add("run = function");
			lines.Add("    clearTypes = [\"Grass\", \"Stone\", \"Twig\", \"Weeds\"]");
			lines.Add("    farm = me.position.area");
			lines.Add("    moveStep = function(expectedFacing, dx, dy)");
			lines.Add("        print \"Current tile: \" + me.position.x + \",\" + me.position.y");
			lines.Add("        print \"Facing: \" + me.facing");
			lines.Add("        print \"Destination tile: \" + (me.position.x + dx) + \",\" + (me.position.y + dy)");
			lines.Add("        if me.facing != expectedFacing then");
			lines.Add("            print \"Facing mismatch: expected \" + expectedFacing + \" got \" + me.facing");
			lines.Add("            return false");
			lines.Add("        end if");
			lines.Add("        startX = me.position.x");
			lines.Add("        startY = me.position.y");
			lines.Add("        me.forward");
			lines.Add("        if me.position.x != startX + dx or me.position.y != startY + dy then");
			lines.Add("            print \"Movement mismatch: expected \" + (startX + dx) + \",\" + (startY + dy) + \" got \" + me.position.x + \",\" + me.position.y");
			lines.Add("            return false");
			lines.Add("        end if");
			lines.Add("        return true");
			lines.Add("    end function");
			lines.Add($"    print \"Target tile: {targetTile.X},{targetTile.Y} name: {targetName}\"");

			foreach (var step in path) {
				int targetFacing = FacingForStep(step - current);
				if (targetFacing < 0) return null;
				AppendTurns(lines, ref facing, targetFacing);
				Vector2 delta = step - current;
				lines.Add("    if not moveStep(" + targetFacing + ", " + (int)delta.X + ", " + (int)delta.Y + ") then return false");
				current = step;
			}

			int finalFacing = FacingTowardTarget(current, targetTile);
			if (finalFacing < 0) return null;
			AppendTurns(lines, ref facing, finalFacing);
			lines.Add("    expectedX = " + current.X);
			lines.Add("    expectedY = " + current.Y);
			lines.Add("    if me.position.x != expectedX or me.position.y != expectedY then");
			lines.Add("        print \"Target mismatch: expected position \" + expectedX + \",\" + expectedY + \" got \" + me.position.x + \",\" + me.position.y");
			lines.Add("        return false");
			lines.Add("    end if");
			lines.Add("    inTile = me.ahead");
			lines.Add("    what = \"nothing\"");
			lines.Add("    if inTile then");
			lines.Add("        if inTile.hasIndex(\"type\") then what = inTile.type");
			lines.Add("        if inTile.hasIndex(\"name\") then what = inTile.name");
			lines.Add("    end if");
			lines.Add("    print \"Ahead tile: \" + what");
			lines.Add("    if inTile then");
			lines.Add("        cleared = false");
			lines.Add("        if clearTypes.indexOf(inTile.type) > -1 then");
			lines.Add("            cleared = me.clearAhead");
			lines.Add("        else if inTile.hasIndex(\"name\") and clearTypes.indexOf(inTile.name) > -1 then");
			lines.Add("            cleared = me.clearAhead");
			lines.Add("        else");
			lines.Add("            cleared = true  // nothing we recognize; treat as success");
			lines.Add("        end if");
			lines.Add("        if not cleared then");
			lines.Add("            return false");
			lines.Add("        end if");
			lines.Add("    end if");
			lines.Add("    return true");
			lines.Add("end function");
			lines.Add("run");
			return string.Join("\n", lines);
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

		private static float DistanceSquared(Vector2 a, Vector2 b) {
			float dx = a.X - b.X;
			float dy = a.Y - b.Y;
			return dx * dx + dy * dy;
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

		private void FailPlan(BotSupervisorState state, TimeSpan now) {
			if (state?.CurrentPlan == null) return;
			BlockTarget(state.Bot?.currentLocation, state.CurrentPlan.TargetTile, now);
			state.CurrentPlan = null;
			state.NextAllowedPlanAt = now + blockedCooldown;
			state.Mode = BotMode.Cooldown;
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

		private string GetPlanStatus(BotObject bot) {
			if (bot?.shell?.interpreter?.vm?.globalContext?.variables == null) return null;
			if (!bot.shell.interpreter.vm.globalContext.variables.map.TryGetValue(planStatusKey, out Value statusValue)) return null;
			return statusValue?.ToString();
		}
        private bool IsSupervisorPassable(GameLocation location, Vector2 tile) {
            if (!IsWithinMap(location, tile)) return false;

            ValMap info = TileInfo.GetInfo(location, tile);
            if (info == null) return true;

            string typeName = info.GetString("type");
            string objectName = info.GetString("name");

            // SMAPI/Farmtronics may report Building passable,
            // but bots cannot path through structures.
            if (typeName == "Building" || objectName == "Building") return false;

            // Also probably not pathable as ordinary walk tiles.
            if (typeName == "Bush" || objectName == "Bush") return false;

            return TileInfo.IsPassable(location, tile);
        }

	}
}
