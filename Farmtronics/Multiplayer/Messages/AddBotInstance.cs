using System.Linq;
using Farmtronics.Bot;
using Farmtronics.Utils;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace Farmtronics.Multiplayer.Messages {
	class AddBotInstance : BaseMessage<AddBotInstance> {
		private int attempt = 1;
		public string LocationName { get; set; }
		public Vector2 TileLocation { get; set; }

		public bool HasGivenUp => false;

		public static void Send(BotObject bot) {
			var message = new AddBotInstance() {
				LocationName = bot.currentLocation.NameOrUniqueName,
				TileLocation = bot.TileLocation
			};
			message.Send(new[] {bot.owner.Value});
		}
		
		private GameLocation GetLocation() {
			return ModEntry.instance.Helper.Multiplayer.GetActiveLocations().SingleOrDefault(location => location.NameOrUniqueName == LocationName);
		}
		
		private BotObject GetBotFromLocation(GameLocation location) {
			return location.getObjectAtTile(TileLocation.GetIntX(), TileLocation.GetIntY()) as BotObject;
		}

		public override void Apply() {
			var location = GetLocation();
			if (location == null) {
				ModEntry.instance.Monitor.Log(
					$"Bot persistence WARN: location {LocationName} is not active for bot registration; retrying, attempt {attempt}.",
					LogLevel.Warn);
				if (!BotManager.lostInstances.Contains(this)) BotManager.lostInstances.Add(this);
				attempt++;
				return;
			}
			var bot = GetBotFromLocation(location);
			if (bot == null) {
				ModEntry.instance.Monitor.Log(
					$"Bot persistence WARN: could not add bot instance at {LocationName} {TileLocation.X},{TileLocation.Y}; retrying, attempt {attempt}.",
					LogLevel.Warn);
				if (!BotManager.lostInstances.Contains(this)) BotManager.lostInstances.Add(this);
				attempt++;
				return;
			}
			BotManager.lostInstances.Remove(this);
			BotManager.RegisterLocalBot(bot, "multiplayer add instance");
			bot.data.Load();
			bot.currentLocation = location;
			ModEntry.instance.Monitor.Log($"Successfully added bot to instance list: {LocationName} - {TileLocation}", LogLevel.Info);
			bot.InitShell();
		}
	}
}
