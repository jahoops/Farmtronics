using System.Collections.Generic;
using System.IO;
using System.Linq;
using Farmtronics.Bot;
using Farmtronics.M1;
using Farmtronics.M1.Filesystem;
using Farmtronics.Multiplayer;
#if DEBUG
using Farmtronics.Utils;
using Microsoft.Xna.Framework;
#endif
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.GameData.BigCraftables;
using StardewValley.Menus;

namespace Farmtronics
{
	public class ModEntry : Mod {
		private static string MOD_ID;
		public static ModEntry instance;
		public static IModHelper localHelper;
		const string internalID_c = "Farmtronics_Bot";

		internal static RealFileDisk sysDisk;
		private readonly Supervisor supervisor = new();
		private bool convertedBotsForSave = false;
		private uint lastBotChestRecoveryTick = 0;
		
		Shell shell;
		
		public static string GetModDataKey(string key) {
			return $"{MOD_ID}/{key}";
		}

		public override void Entry(IModHelper helper) {
			instance = this;
			MOD_ID = ModManifest.UniqueID;
			localHelper = helper;
			I18n.Init(helper.Translation);
			helper.ConsoleCommands.Add("ft_bot_report", "Report Farmtronics bot identity, placement, storage, registry, duplicates, and inventory.", BotReportCommand);
			helper.ConsoleCommands.Add("ft_bot_dedupe_dryrun", "Preview safe Farmtronics bot duplicate cleanup without changing anything.", BotDedupeDryRunCommand);
			helper.ConsoleCommands.Add("ft_bot_dedupe", "Safely clean obvious empty Farmtronics bot duplicates and quarantine ambiguous duplicates.", BotDedupeCommand);
			helper.ConsoleCommands.Add("ft_bot_move_here", "Move one canonical functional world bot near the player. Usage: ft_bot_move_here <bot name>", BotMoveHereCommand);
			helper.ConsoleCommands.Add("ft_bot_relocate", "Alias for ft_bot_move_here. Usage: ft_bot_relocate <bot name>", BotMoveHereCommand);
			helper.ConsoleCommands.Add("ft_bot_send_home", "Move one canonical functional world bot to its configured home tile. Usage: ft_bot_send_home <bot name>", BotSendHomeCommand);
			helper.ConsoleCommands.Add("ft_bot_role", "Set bot capabilities. Usage: ft_bot_role <bot name> <capability...>", BotRoleCommand);
			helper.ConsoleCommands.Add("ft_bot_mode", "Set bot mode. Usage: ft_bot_mode <bot name> <off|work|home|follow>", BotModeCommand);
			helper.ConsoleCommands.Add("ft_bot_zone_start", "Start a bot zone rectangle at the player tile. Usage: ft_bot_zone_start <zone name>", BotZoneStartCommand);
			helper.ConsoleCommands.Add("ft_bot_zone_end", "Finish a bot zone rectangle at the player tile. Usage: ft_bot_zone_end <zone name>", BotZoneEndCommand);
			helper.ConsoleCommands.Add("ft_bot_assign_zone", "Assign a named zone to a bot. Usage: ft_bot_assign_zone <bot name> <zone name>", BotAssignZoneCommand);
			helper.ConsoleCommands.Add("ft_bot_status", "Report one bot's orders and idle reason. Usage: ft_bot_status <bot name>", BotStatusCommand);
#if DEBUG
			// HACK not needed:
			helper.Events.Input.ButtonPressed += OnButtonPressed;
#endif
			helper.Events.GameLoop.SaveCreated += OnSaveCreated;
			helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
			helper.Events.GameLoop.Saving += OnSaving;
			helper.Events.GameLoop.Saved += OnSaved;
			helper.Events.GameLoop.DayStarted += OnDayStarted;
			helper.Events.GameLoop.DayEnding += OnDayEnding;
			helper.Events.GameLoop.UpdateTicking += UpdateTicking;
			helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

			helper.Events.Display.MenuChanged += OnMenuChanged;		
            helper.Events.Content.AssetRequested += OnAssetRequested;

			helper.Events.Multiplayer.ModMessageReceived += MultiplayerManager.OnModMessageReceived;
			helper.Events.Multiplayer.PeerContextReceived += MultiplayerManager.OnPeerContextReceived;
			helper.Events.Multiplayer.PeerConnected += MultiplayerManager.OnPeerConnected;
			helper.Events.Multiplayer.PeerDisconnected += MultiplayerManager.OnPeerDisconnected;
			helper.Events.Player.Warped += BotManager.FindLostInstancesOnWarp;
			helper.Events.Player.Warped += SupervisorPlayerWarped;
			
			ModData.Initialize();
			
			Assets.Initialize(helper);
			Monitor.Log($"Loaded fontAtlas with size {Assets.FontAtlas.Width}x{Assets.FontAtlas.Height}");
			Monitor.Log($"read {Assets.FontList.Length} lines from fontList, starting with {Assets.FontList[0]}");
			sysDisk = new RealFileDisk(Path.Combine(instance.Helper.DirectoryPath, "assets", "sysdisk"));
			sysDisk.readOnly = true;
		}

		private void BotReportCommand(string command, string[] args) {
			supervisor.ReportAllBotPersistenceState();
		}

		private void BotDedupeDryRunCommand(string command, string[] args) {
			supervisor.SafeCleanupDuplicateBots(dryRun: true);
		}

		private void BotDedupeCommand(string command, string[] args) {
			supervisor.SafeCleanupDuplicateBots(dryRun: false);
			supervisor.ReportAllBotPersistenceState();
		}

		private void BotMoveHereCommand(string command, string[] args) {
			string botName = string.Join(" ", args ?? System.Array.Empty<string>()).Trim();
			if (string.IsNullOrWhiteSpace(botName)) {
				Monitor.Log($"Usage: {command} <bot name>", LogLevel.Warn);
				return;
			}
			supervisor.MoveBotHere(botName);
		}

		private void BotSendHomeCommand(string command, string[] args) {
			string botName = string.Join(" ", args ?? System.Array.Empty<string>()).Trim();
			if (string.IsNullOrWhiteSpace(botName)) {
				Monitor.Log($"Usage: {command} <bot name>", LogLevel.Warn);
				return;
			}
			supervisor.SendBotHome(botName);
		}

		private void BotRoleCommand(string command, string[] args) {
			if (args == null || args.Length < 2) {
				Monitor.Log($"Usage: {command} <bot name> <capability...>", LogLevel.Warn);
				return;
			}

			int firstCapability = -1;
			for (int i = 0; i < args.Length; i++) {
				if (supervisor.TryParseCapability(args[i], out _)) {
					firstCapability = i;
					break;
				}
			}

			if (firstCapability <= 0) {
				Monitor.Log($"Usage: {command} <bot name> <capability...>", LogLevel.Warn);
				return;
			}

			string botName = string.Join(" ", args.Take(firstCapability)).Trim();
			supervisor.SetBotRole(botName, args.Skip(firstCapability));
		}

		private void BotModeCommand(string command, string[] args) {
			if (args == null || args.Length < 2) {
				Monitor.Log($"Usage: {command} <bot name> <off|work|home|follow>", LogLevel.Warn);
				return;
			}

			string modeName = args[^1];
			string botName = string.Join(" ", args.Take(args.Length - 1)).Trim();
			supervisor.SetBotOrderMode(botName, modeName);
		}

		private void BotZoneStartCommand(string command, string[] args) {
			string zoneName = string.Join(" ", args ?? System.Array.Empty<string>()).Trim();
			if (string.IsNullOrWhiteSpace(zoneName)) {
				Monitor.Log($"Usage: {command} <zone name>", LogLevel.Warn);
				return;
			}
			supervisor.StartZoneDraft(zoneName);
		}

		private void BotZoneEndCommand(string command, string[] args) {
			string zoneName = string.Join(" ", args ?? System.Array.Empty<string>()).Trim();
			if (string.IsNullOrWhiteSpace(zoneName)) {
				Monitor.Log($"Usage: {command} <zone name>", LogLevel.Warn);
				return;
			}
			supervisor.EndZoneDraft(zoneName);
		}

		private void BotAssignZoneCommand(string command, string[] args) {
			if (args == null || args.Length < 2) {
				Monitor.Log($"Usage: {command} <bot name> <zone name>", LogLevel.Warn);
				return;
			}

			string zoneName = args[^1];
			string botName = string.Join(" ", args.Take(args.Length - 1)).Trim();
			supervisor.AssignZoneToBot(botName, zoneName);
		}

		private void BotStatusCommand(string command, string[] args) {
			string botName = string.Join(" ", args ?? System.Array.Empty<string>()).Trim();
			if (string.IsNullOrWhiteSpace(botName)) {
				Monitor.Log($"Usage: {command} <bot name>", LogLevel.Warn);
				return;
			}
			supervisor.ReportBotStatus(botName);
		}

		private void SupervisorPlayerWarped(object sender, WarpedEventArgs args) {
			supervisor.HandlePlayerWarped(args);
		}

		private void OnSaveCreated(object sender, SaveCreatedEventArgs e) {
			Monitor.Log($"CurrentSavePath: {Constants.CurrentSavePath}");
			SaveData.CreateSaveDataDirs();
			SaveData.CreateUsrDisk(Game1.player.UniqueMultiplayerID);
		}

		private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e) {
			BotManager.ClearAll();
			MultiplayerManager.remoteComputer.Clear();
			DiskController.ClearInstances();
			BotManager.botCount = 0;
			shell = null;
			supervisor.Reset();
		}

		private void UpdateTicking(object sender, UpdateTickingEventArgs e) {
			var gameTime = Game1.currentGameTime; //new GameTime(new TimeSpan(e.Ticks * 10000000 / 60), new TimeSpan(dTicks * 10000000 / 60));

			RecoverBotChests(e.Ticks);

			// update the shell here only if it is not open; if it IS open, it will
			// be updated automatically via the UI system
			if (shell != null && !shell.console.isOpen) shell.console.update(gameTime);

			// update all bots
			BotManager.UpdateAll(gameTime);
			supervisor.Update(gameTime);
		}

		private void RecoverBotChests(uint tick)
		{
			if (!Context.IsWorldReady || convertedBotsForSave)
				return;

			if (tick - lastBotChestRecoveryTick < 60)
				return;

			lastBotChestRecoveryTick = tick;
			BotManager.ConvertChestsToBots();
		}
		
		// NOTE: Only check the mailbox once per day and only when the player warps to the farm
		//		 This prevents XML serialization errors
		private void OnPlayerWarped(object sender, WarpedEventArgs args) {
			if (!args.IsLocalPlayer || args.NewLocation is not Farm) return;
			
			// Check whether we have our first-bot letter waiting in the mailbox.
			// If so, set the item to be "recovered" via the mail:
			foreach (var msg in Game1.player.mailbox) {
				Monitor.Log($"Mail in mailbox: {msg}");
				if (msg == "FarmtronicsFirstBotMail") {
					Monitor.Log($"Changing recoveredItem from {Game1.player.recoveredItem} to Bot");
					var bot = new BotObject();
					bot.ResetIdentity("first-bot-mail");
					bot.displayName = I18n.Bot_Name(BotManager.botCount);
					BotManager.botCount++;
					bot.owner.Value = Game1.player.UniqueMultiplayerID;
					Game1.player.recoveredItem = bot;
					break;
				}
			}
			
			Helper.Events.Player.Warped -= OnPlayerWarped;
		}

		// HACK used only for early testing/development:
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
				Monitor.Log("F6 pressed: Bot persistence report.");
				supervisor.ReportAllBotPersistenceState();
				break;
			case SButton.F7:
				Monitor.Log("F7 pressed: Recall bots home.");
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
				Monitor.Log("F11 pressed: non-destructive bot safety scan/quarantine.");
				//supervisor.Stop();
				supervisor.PurgeExtraBotObjects();
				break;			

			}
		}
		
		public void OnMenuChanged(object sender, MenuChangedEventArgs e) {
			Monitor.Log($"Menu opened: {e.NewMenu}");
			if (e.NewMenu is ShopMenu shop) {
				if (shop.ShopId != Game1.shop_generalStore) return;
				if (Game1.player.mailReceived.Contains("FarmtronicsFirstBotMail")) {
					// Add a bot to the store inventory.
					// Let's insert it after Flooring but before Catalogue.
					int index = 0;
					for (; index < shop.forSale.Count; index++) {
						var item = shop.forSale[index];
						Monitor.Log($"Shop item {index}: {item} with {item.Name}");
						if (item.Name == "Catalogue" || (index>0 && shop.forSale[index-1].Name == "Flooring")) break;
					}
					var botForSale = new BotObject();
					botForSale.ResetIdentity("shop-stock");
					botForSale.displayName = I18n.Bot_Name(BotManager.botCount);
					botForSale.owner.Value = Game1.player.UniqueMultiplayerID;
					shop.forSale.Insert(index, botForSale);
					shop.itemPriceAndStock.Add(botForSale, new ItemStockInformation(2500, int.MaxValue));	// sale price and available stock
				}
			}

			var dlog = e.NewMenu as DialogueBox;
			if (dlog == null) return;
			if (!dlog.isQuestion || dlog.responses[0].responseKey != "Weather") return;
			// Only allow players to use the home computer at their own s
			if (Game1.player.currentLocation.NameOrUniqueName != Game1.player.homeLocation.Value) return;

			// TV menu: insert a new option for the Home Computer
			Response r = new Response("Farmtronics", I18n.TvChannel_Label());
			List<Response> tempResponses = new List<Response>(dlog.responses);
			tempResponses.Insert(tempResponses.Count - 1,r);
			dlog.responses = tempResponses.ToArray();
			// adjust the dialog height
			var h = SpriteText.getHeightOfString(r.responseText, dlog.width) + 16;
			dlog.heightForQuestions += h; dlog.height += h;
			// intercept the handler (but call the original one for other responses)
			var prevHandler = Game1.currentLocation.afterQuestion;
			Game1.currentLocation.afterQuestion = (who, whichAnswer) => {
				Monitor.Log($"{who} selected channel {whichAnswer}");
				if (whichAnswer == "Farmtronics") PresentComputer();
				else prevHandler(who, whichAnswer);
			};
		}

		public void OnSaving(object sender, SavingEventArgs args) {
			Monitor.Log("OnSaving");
			if (convertedBotsForSave) {
				Monitor.Log("OnSaving: bots already converted for this save.");
				return;
			}

			// Host can't save without this
			BotManager.ConvertBotsToChests(true);
			BotManager.ClearAll();
			convertedBotsForSave = true;
		}

		public void OnSaved(object sender, SavedEventArgs args) {
			Monitor.Log("OnSaved");
			BotManager.ConvertChestsToBots();
			convertedBotsForSave = false;
		}

		public void OnSaveLoaded(object sender, SaveLoadedEventArgs args) {
			Monitor.Log("OnSaveLoaded");
			if (Context.IsMainPlayer) {
				SaveData.CreateSaveDataDirs();
				if (SaveData.IsOldSaveDirPresent()) SaveData.MoveOldSaveDir();
				Monitor.Log($"Setting host player ID: {Game1.player.UniqueMultiplayerID}");
				MultiplayerManager.hostID = Game1.player.UniqueMultiplayerID;
			}
			BotManager.ConvertChestsToBots();
			if (shell != null) shell.name = PerPlayerData.HomeComputerName;
		}

		public void OnDayStarted(object sender, DayStartedEventArgs args) {
			Monitor.Log("OnDayStarted");
			convertedBotsForSave = false;
			Helper.Events.Player.Warped += OnPlayerWarped;

			// Initialize the home computer and all bots for autostart.
			// This initialization will also cause all startup scripts to run.
			InitComputerShell();
			if (Context.IsMainPlayer) MultiplayerManager.InitRemoteComputer();
			BotManager.InitShellAll();
		}

		private void OnDayEnding(object sender, DayEndingEventArgs e) {
			Monitor.Log("OnDayEnding");
			// Other players need to convert their inventory before OnSaving happens
			if (!convertedBotsForSave) {
				BotManager.ConvertBotsToChests(true);
				BotManager.ClearAll();
				convertedBotsForSave = true;
			}
			// And let's also shut down the home computer, for consistency
			if (shell != null) {
				shell = null;		// well that was easy.
			}
		}

		/// <summary>
		/// Initializes the home computer shell.
		/// Effectively boots up the home computer if it is not already running.
		/// </summary>
		private void InitComputerShell() {
			if (shell == null) {
				shell = new Shell();
				shell.name = PerPlayerData.HomeComputerName;
				shell.Init(Game1.player.UniqueMultiplayerID);
			}
		}

		private void PresentComputer() {
			// Initialize the home computer if it is not already running, then present it.
			InitComputerShell();
			shell.console.Present();
		}

        /// <inheritdoc cref="IContentEvents.AssetRequested" />
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnAssetRequested(object sender, AssetRequestedEventArgs e) {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/mail")) {
                e.Edit(asset => {
                    Monitor.Log("ModEntry.Edit(Mail)");
                    var data = asset.AsDictionary<string, string>().Data;
                    data["FarmtronicsFirstBotMail"] = I18n.Mail_Text("%item itemRecovery %%");
                    foreach (var msg in Game1.player.mailbox) {
                        Monitor.Log($"mail in mailbox: {msg}");
                        if (msg == "FarmtronicsFirstBotMail") {
                            Monitor.Log($"Changing recoveredItem from {Game1.player.recoveredItem} to Bot");
							var bot = new BotObject();
							bot.ResetIdentity("first-bot-mail-asset");
							bot.displayName = I18n.Bot_Name(BotManager.botCount);
							bot.owner.Value = Game1.player.UniqueMultiplayerID;
                            Game1.player.recoveredItem = bot;
                            break;
                        }
                    }
                });
            }
			else if (e.NameWithoutLocale.IsEquivalentTo("Data/BigCraftables"))
			{
				e.Edit(
					apply: static (asset) =>
					{
						asset.AsDictionary<string, BigCraftableData>().Data[ModEntry.internalID_c] = new()
						{
							Name = "Farmtronics Bot",
							Price = 1000,
							DisplayName = I18n.Bot_Name(null),
							Description = I18n.Bot_Description(),
							Texture = localHelper.ModContent.GetInternalAssetName("assets/BotSprites.png").ToString(),
							SpriteIndex = 2,
						};
					},
					priority: AssetEditPriority.Early);
			}
        }
    }
}
