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
	internal sealed class SupervisorSnapshot {
		public List<SupervisorLocationSnapshot> Locations { get; } = new();
		public List<SupervisorBotSnapshot> Bots { get; } = new();
	}

	internal sealed class SupervisorLocationSnapshot {
		public string Name { get; init; }
		public int Width { get; init; }
		public int Height { get; init; }
	}

	internal sealed class SupervisorBotSnapshot {
		public string Name { get; init; }
		public string LocationName { get; init; }
		public Vector2 TileLocation { get; init; }
		public int Facing { get; init; }
	}

	internal sealed class Supervisor {
		private sealed class PendingPlan {
			public BotObject Bot { get; init; }
			public Vector2 TargetTile { get; init; }
			public string TargetName { get; init; }
			public Vector2 StartTile { get; init; }
			public string Script { get; init; }
			public TimeSpan? QueuedAt { get; set; }
		}

		private sealed class TargetCandidate {
			public string TargetName { get; init; }
			public Vector2 TargetTile { get; init; }
			public Vector2 AdjacentTile { get; init; }
			public float DistanceSquared { get; init; }
		}

		private enum Mode {
			Idle,
			SingleBotRockTest,
			AllBotsRockTest,
		}

		private static readonly TimeSpan planTimeout = TimeSpan.FromSeconds(15);
		private static readonly HashSet<string> clearTypes = new() { "Grass", "Stone", "Twig", "Weeds", "Bush" };
		private static readonly ValString planStatusKey = new("supervisorPlanStatus");
		private readonly List<PendingPlan> pendingPlans = new();
		private readonly Dictionary<string, PendingPlan> activePlansByBot = new();
		private Mode mode = Mode.Idle;

		public SupervisorSnapshot Snapshot() {
			var snapshot = new SupervisorSnapshot();

			foreach (var location in Game1.locations) {
				snapshot.Locations.Add(new SupervisorLocationSnapshot {
					Name = location.NameOrUniqueName,
					Width = location.map.Layers[0].LayerWidth,
					Height = location.map.Layers[0].LayerHeight,
				});
			}

			foreach (var bot in BotManager.GetAllBots()) {
				if (bot == null) continue;
				snapshot.Bots.Add(new SupervisorBotSnapshot {
					Name = bot.name,
					LocationName = bot.currentLocation?.NameOrUniqueName,
					TileLocation = bot.TileLocation,
					Facing = bot.facingDirection,
				});
			}

			return snapshot;
		}

		public void Reset() {
			mode = Mode.Idle;
			pendingPlans.Clear();
			activePlansByBot.Clear();
			ModEntry.instance.Monitor.Log("Supervisor reset.");
		}

		public void Stop() {
			mode = Mode.Idle;
			pendingPlans.Clear();
			activePlansByBot.Clear();
			ModEntry.instance.Monitor.Log("Supervisor stopped.");
		}

		public void StartSingleBotRockTest() {
			mode = Mode.SingleBotRockTest;
			pendingPlans.Clear();
			activePlansByBot.Clear();
			QueuePlansForFirstLoadedBot();
			ModEntry.instance.Monitor.Log("Supervisor started: single-bot rock test.");
		}

		public void StartAllBotsRockTest() {
			mode = Mode.AllBotsRockTest;
			pendingPlans.Clear();
			activePlansByBot.Clear();
			QueuePlansForAllLoadedBots();
			ModEntry.instance.Monitor.Log("Supervisor started: all-bots rock test.");
		}

		public void Update(GameTime gameTime) {
			if (!Context.IsMainPlayer) return;
			if (!Context.IsWorldReady) return;
			if (mode == Mode.Idle) return;
			if (pendingPlans.Count == 0) return;

			TimeSpan now = gameTime.TotalGameTime;
			for (int i = pendingPlans.Count - 1; i >= 0; i--) {
				var plan = pendingPlans[i];
				if (plan.Bot == null || plan.Bot.shell == null) {
					RemovePlan(plan);
					continue;
				}

				string planStatus = GetPlanStatus(plan.Bot);
				if (planStatus == "success") {
					ModEntry.instance.Monitor.Log($"Supervisor plan completed for {plan.Bot.name}: target tile {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");
					RemovePlan(plan);
					QueueNextPlanForBot(plan.Bot);
					continue;
				}

				if (planStatus == "failed") {
					ModEntry.instance.Monitor.Log($"Supervisor plan failed for {plan.Bot.name}: target tile {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");
					RemovePlan(plan);
					QueueNextPlanForBot(plan.Bot);
					continue;
				}

				if (plan.Bot.currentLocation != Game1.currentLocation) continue;

				bool targetStillExists = IsClearableTile(plan.Bot.currentLocation, plan.TargetTile);
				if (plan.QueuedAt.HasValue) {
					if (now - plan.QueuedAt.Value >= planTimeout) {
						ModEntry.instance.Monitor.Log($"Supervisor plan failed/timed out for {plan.Bot.name}: target tile {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");
						plan.Bot.shell.Break(true);
						RemovePlan(plan);
						QueueNextPlanForBot(plan.Bot);
						continue;
					}
					continue;
				}

				if (!plan.Bot.shell.IsReadyForCommand()) continue;
				if (plan.Bot.shell.HasQueuedCommands()) continue;

				ModEntry.instance.Monitor.Log($"Supervisor queued plan for {plan.Bot.name}: target tile {plan.TargetTile.X},{plan.TargetTile.Y} name {plan.TargetName}");
				plan.Bot.shell.QueueCommand(plan.Script);
				plan.QueuedAt = now;
			}

			if (pendingPlans.Count == 0) {
				mode = Mode.Idle;
			}
		}

		private void QueuePlansForFirstLoadedBot() {
			var bot = BotManager.GetAllBots().FirstOrDefault(candidate => candidate?.currentLocation is Farm);
			if (bot == null) {
				ModEntry.instance.Monitor.Log("Supervisor could not find a loaded bot on a farm.");
				return;
			}

			QueueNextPlanForBot(bot);
		}

		private void QueuePlansForAllLoadedBots() {
			foreach (var bot in BotManager.GetAllBots().Where(candidate => candidate?.currentLocation is Farm)) {
				QueueNextPlanForBot(bot);
			}

			if (pendingPlans.Count == 0) {
				ModEntry.instance.Monitor.Log("Supervisor could not build any bot plans.");
			}
		}

		private void QueueNextPlanForBot(BotObject bot) {
			if (bot == null) return;
			if (activePlansByBot.TryGetValue(bot.name, out var activePlan)) {
				ModEntry.instance.Monitor.Log($"Supervisor skipped duplicate plan for {bot.name}: target tile {activePlan.TargetTile.X},{activePlan.TargetTile.Y} name {activePlan.TargetName}");
				return;
			}

			bot.InitShell();

			var farm = bot.currentLocation as Farm;
			if (farm == null) return;

			var candidates = FindTargetCandidates(farm, bot.TileLocation)
				.OrderBy(candidate => candidate.DistanceSquared)
				.ToList();

			var candidate = candidates.FirstOrDefault();
			if (candidate == null) return;

			var path = FindPath(farm, bot.TileLocation, candidate.AdjacentTile);
			if (path == null) return;

			var script = BuildScript(bot.TileLocation, bot.facingDirection, path, candidate.TargetTile, candidate.TargetName, farm);
			if (string.IsNullOrWhiteSpace(script)) return;

			pendingPlans.Add(new PendingPlan {
				Bot = bot,
				TargetTile = candidate.TargetTile,
				TargetName = candidate.TargetName,
				StartTile = bot.TileLocation,
				Script = script,
			});
			activePlansByBot[bot.name] = pendingPlans[^1];
			ModEntry.instance.Monitor.Log($"Supervisor queued plan for {bot.name}: target tile {candidate.TargetTile.X},{candidate.TargetTile.Y} name {candidate.TargetName}");
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
				if (TileInfo.IsPassable(location, candidate)) yield return candidate;
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
				if (TileInfo.IsPassable(location, candidate)) yield return candidate;
			}
		}

		private string BuildScript(Vector2 startTile, int startFacing, List<Vector2> path, Vector2 targetTile, string targetName, Farm farm) {
			var lines = new List<string>();
			int facing = startFacing;
			Vector2 current = startTile;

			lines.Add("run = function");
			lines.Add("    clearTypes = [\"Grass\", \"Stone\", \"Twig\", \"Weeds\", \"Bush\"]");
			lines.Add("    supervisorPlanStatus = \"running\"");
			lines.Add("    farm = me.position.area");
			lines.Add("    moveStep = function(expectedFacing, dx, dy)");
			lines.Add("        print \"Current tile: \" + me.position.x + \",\" + me.position.y");
			lines.Add("        print \"Facing: \" + me.facing");
			lines.Add("        print \"Destination tile: \" + (me.position.x + dx) + \",\" + (me.position.y + dy)");
			lines.Add("        if me.facing != expectedFacing then");
			lines.Add("            print \"Facing mismatch: expected \" + expectedFacing + \" got \" + me.facing");
			lines.Add("            supervisorPlanStatus = \"failed\"");
			lines.Add("            return false");
			lines.Add("        end if");
			lines.Add("        startX = me.position.x");
			lines.Add("        startY = me.position.y");
			lines.Add("        me.forward");
			lines.Add("        if me.position.x != startX + dx or me.position.y != startY + dy then");
			lines.Add("            print \"Movement mismatch: expected \" + (startX + dx) + \",\" + (startY + dy) + \" got \" + me.position.x + \",\" + me.position.y");
			lines.Add("            supervisorPlanStatus = \"failed\"");
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
			lines.Add("        supervisorPlanStatus = \"failed\"");
			lines.Add("        return false");
			lines.Add("    end if");
			lines.Add("    inTile = me.ahead");
			lines.Add("    what = \"nothing\"");
			lines.Add("    if inTile then");
			lines.Add("        what = inTile.type");
			lines.Add("        if inTile.hasIndex(\"name\") then what = inTile.name");
			lines.Add("    end if");
			lines.Add("    print \"Ahead tile: \" + what");
			lines.Add("    if inTile then");
			lines.Add("        if clearTypes.indexOf(inTile.type) > -1 then");
			lines.Add("            me.clearAhead");
			lines.Add("        else if inTile.hasIndex(\"name\") and clearTypes.indexOf(inTile.name) > -1 then");
			lines.Add("            me.clearAhead");
			lines.Add("        end if");
			lines.Add("    end if");
			lines.Add("    supervisorPlanStatus = \"success\"");
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

		private void RemovePlan(PendingPlan plan) {
			if (plan == null) return;
			pendingPlans.Remove(plan);
			if (plan.Bot != null) activePlansByBot.Remove(plan.Bot.name);
		}

		private string GetPlanStatus(BotObject bot) {
			if (bot?.shell?.interpreter?.vm?.globalContext?.variables == null) return null;
			if (!bot.shell.interpreter.vm.globalContext.variables.map.TryGetValue(planStatusKey, out Value statusValue)) return null;
			return statusValue?.ToString();
		}
	}
}
