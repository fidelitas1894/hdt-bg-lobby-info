using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace BgLobbyInfo
{
	public class BgLobbyInfoPlugin : IPlugin
	{
		public string Name => "BG Lobby Info";
		public string Description => "Battlegrounds: shows the MMR of every lobby player (via the public leaderboard), a summary of their last seen board, and on hover the Dark Gifts and Lesser/Greater Trinkets that are possible in this lobby.";
		public string ButtonText => "Reload leaderboard";
		public string Author => "fidelitas1894";
		public Version Version => new Version(0, 3, 0);
		public MenuItem MenuItem => null;

		private readonly LeaderboardService _leaderboard = new LeaderboardService();
		private LobbyOverlay _overlay;
		private DateTime _lastUpdate = DateTime.MinValue;
		private readonly Dictionary<int, BoardSummary> _boardCache = new Dictionary<int, BoardSummary>();
		private readonly TrinketService _trinkets = new TrinketService();
		private HashSet<Race> _lobbyRaces;
		private BoardSummary _selfShopBoard; // eigenes Board am Ende der Shop-Phase (im Kampf eingefroren)

		public void OnLoad()
		{
			_overlay = new LobbyOverlay(Core.OverlayCanvas);
			_overlay.SetPopupProvider(PopupFor);
			_trinkets.Prefetch();
			Hearthstone_Deck_Tracker.API.GameEvents.OnGameStart.Add(OnGameStart);
		}

		public void OnUnload()
		{
			_overlay?.Remove();
			_overlay = null;
		}

		public void OnButtonPress()
		{
			var region = RegionCode();
			if(region != null)
				_leaderboard.Prefetch(region, Core.Game.IsBattlegroundsDuosMatch);
		}

		private void OnGameStart()
		{
			_boardCache.Clear();
			_lobbyRaces = null;
			_selfShopBoard = null;
			_trinkets.Prefetch();
			if(Core.Game.IsBattlegroundsMatch && RegionCode() is string region)
				_leaderboard.Prefetch(region, Core.Game.IsBattlegroundsDuosMatch);
		}

		public void OnUpdate()
		{
			if(_overlay == null)
				return;
			try
			{
				var game = Core.Game;
				// Layout aktuell nur für Solo; Duos hat ein anderes Leaderboard-Raster.
				if(game.IsInMenu || !game.IsBattlegroundsSoloMatch || !game.IsBattlegroundsHeroPickingDone)
				{
					_overlay.Hide();
					return;
				}
				// Daten nur ~4x pro Sekunde neu berechnen, Hover-Check läuft bei jedem Update.
				if(DateTime.UtcNow - _lastUpdate < TimeSpan.FromMilliseconds(250))
				{
					_overlay.Update(_lastSlots, _lastButtons);
					return;
				}
				_lastUpdate = DateTime.UtcNow;
				_lastSlots = BuildSlots();
				_lastButtons = VisibleButtons(_lastSlots);
				var (lo, hi, uses) = DarkGiftService.DiscoveryWindow(game.GetTurnNumber());
				_overlay.SelfGiftsLabel = $"Gifts T{TierRange(lo, hi)}" + (uses.HasValue ? $" · {uses}" : "");
				_overlay.Update(_lastSlots, _lastButtons);
			}
			catch(Exception e)
			{
				Log.Error(e);
			}
		}

		private List<LobbySlot> _lastSlots = new List<LobbySlot>();
		private List<PopupKind> _lastButtons = new List<PopupKind>();

		private List<LobbySlot> BuildSlots()
		{
			var game = Core.Game;
			var turn = game.GetTurnNumber();
			var region = RegionCode();
			var lobby = game.MetaData?.BattlegroundsLobbyInfo?.Players;
			var selfHeroId = game.Player?.Hero?.Id;

			var heroes = game.Entities.Values
				.Where(e => e.IsHero && e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) && !e.IsInZone(Zone.REMOVEDFROMGAME))
				.GroupBy(e => e.GetTag(GameTag.PLAYER_ID))
				.Select(g => g.OrderByDescending(e => e.Id).First())
				.ToList();

			var slots = new List<LobbySlot>();
			foreach(var hero in heroes)
			{
				var playerId = hero.GetTag(GameTag.PLAYER_ID);
				var isSelf = hero.Id == selfHeroId;
				var lobbyPlayer = lobby?.FirstOrDefault(p => p.HeroCardId == hero.CardId);
				var name = lobbyPlayer?.Name ?? hero.Card?.Name ?? "?";

				var slot = new LobbySlot
				{
					Place = hero.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE),
					PlayerName = LeaderboardService.StripDiscriminator(name),
					PlayerId = playerId,
					IsSelf = isSelf,
					PickedLesserDbf = hero.GetTag(GameTag.BACON_FIRST_TRINKET_DATABASE_ID),
					PickedGreaterDbf = hero.GetTag(GameTag.BACON_SECOND_TRINKET_DATABASE_ID),
				};

				// MMR
				if(isSelf)
				{
					var own = game.CurrentBattlegroundsRating ?? game.BattlegroundsRatingInfo?.Rating;
					slot.MmrText = own?.ToString("N0");
				}
				else if(region != null && lobbyPlayer != null)
				{
					var (rating, min, loaded) = _leaderboard.Lookup(region, false, lobbyPlayer.Name);
					slot.MmrText = !loaded ? "…" : rating.HasValue ? rating.Value.ToString("N0") : $"<{min:N0}";
				}

				// Board (HDT speichert das Board jedes Gegners nach dem Kampf; Abfrage über die Entity-ID des Helden)
				if(!isSelf)
				{
					var snap = game.GetBattlegroundsBoardStateFor(hero.Id);
					if(snap?.Entities != null)
					{
						if(!_boardCache.TryGetValue(playerId, out var cached) || cached.Turn != snap.Turn)
							_boardCache[playerId] = cached = BoardSummary.From(snap.Entities.Where(e => e.IsMinion).ToList(), snap.Turn);
						slot.Board = cached;
						slot.BoardText = cached.ToShortText(turn);
					}
				}
				else
				{
					// Im Kampf sterben/entstehen ständig Minions – dort den Stand vom Ende der Shop-Phase weiterverwenden,
					// sonst springen Hauptstamm, Hervorhebungen und Scrollposition im offenen Popup.
					if(!game.IsBattlegroundsCombatPhase || _selfShopBoard == null)
						_selfShopBoard = BoardSummary.From(game.Player.Board.Where(e => e.IsMinion).ToList(), turn);
					slot.Board = _selfShopBoard;
				}
				slots.Add(slot);
			}
			return slots;
		}

		private List<PopupKind> VisibleButtons(List<LobbySlot> slots)
		{
			var game = Core.Game;
			var result = new List<PopupKind>();
			if(game.GameEntity?.GetTag(DarkGiftService.DarkGiftsActive) == 1)
				result.Add(PopupKind.DarkGifts);
			if(game.GameEntity?.GetTag(GameTag.BACON_TRINKETS_ACTIVE) > 0 || slots.Any(s => s.PickedLesserDbf > 0 || s.PickedGreaterDbf > 0))
			{
				result.Add(PopupKind.LesserTrinkets);
				result.Add(PopupKind.GreaterTrinkets);
			}
			return result;
		}

		private HashSet<Race> LobbyRaces()
		{
			if(_lobbyRaces != null && _lobbyRaces.Count > 0)
				return _lobbyRaces;
			try
			{
				_lobbyRaces = Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsUtils.GetAvailableRaces();
			}
			catch(Exception e)
			{
				Log.Warn("Available races unavailable: " + e.Message);
			}
			return _lobbyRaces;
		}

		private PopupContent PopupFor(LobbySlot slot, PopupKind kind)
		{
			var turn = Core.Game.GetTurnNumber();
			var races = LobbyRaces();
			var racesInfo = races != null && races.Count > 0
				? "Tribes: " + string.Join(", ", races.Where(r => r != Race.INVALID).Select(BoardSummary.TribeName))
				: "Lobby tribes unknown – not filtered by tribe";

			var who = slot.IsSelf ? "your" : "their";
			var boardInfo = slot.IsSelf ? "Based on your current board"
				: slot.Board == null ? "Their board hasn't been seen yet"
				: $"Based on their last seen board (turn {slot.Board.Turn})";

			if(kind == PopupKind.DarkGifts)
				return DarkDiscoveryPopup(slot, turn, races, racesInfo, boardInfo);

			var greater = kind == PopupKind.GreaterTrinkets;
			var size = greater ? "Greater" : "Lesser";
			var pickedDbf = greater ? slot.PickedGreaterDbf : slot.PickedLesserDbf;

			// Ab der Wahl: nur noch das gewählte Trinket zeigen
			if(pickedDbf > 0)
			{
				var picked = _trinkets.ByDbf(pickedDbf);
				return new PopupContent
				{
					Header = $"{size} Trinket – {slot.PlayerName}",
					Sections =
					{
						new PopupSection { Title = "PICKED", Highlight = true, Entries =
						{
							new PopupEntry { CardId = picked?.CardId ?? Hearthstone_Deck_Tracker.Hearthstone.Database.GetCardFromDbfId(pickedDbf, false)?.Id, Name = picked?.Name ?? TrinketService.NameOf(pickedDbf) ?? $"Unknown trinket ({pickedDbf})", Text = picked?.Text },
						} },
					},
				};
			}

			// Blizzard-Regel: "drin" in einem Stamm mit 2 (Lesser) bzw. 3 (Greater) Dienern dieses Stamms
			var g2 = _trinkets.Group(greater, races, Core.Game.IsBattlegroundsDuosMatch, slot.Board);
			var threshold = TrinketService.InTypeThreshold(greater);
			var inText = g2.InTypes.Count == 0
				? $"{(slot.IsSelf ? "You're" : "They're")} not in any type yet (needs {threshold} minions of a type)"
				: $"{(slot.IsSelf ? "You're" : "They're")} in: " + string.Join(", ", g2.InTypes.Select(r => $"{BoardSummary.TribeName(r)} ({slot.Board.Tribes.First(t => t.race == r).count})"));
			var info = new List<string>
			{
				inText,
				$"{boardInfo} · hand and hero power can add types too",
				racesInfo,
			};
			if(!_trinkets.PoolFromHsReplay)
				info.Add("Season pool: HearthDb fallback (HSReplay unreachable)");

			PopupEntry TEntry(Trinket t) => new PopupEntry { CardId = t.CardId, Name = t.Name, Text = t.Text };
			return new PopupContent
			{
				Header = $"{size} Trinkets – {slot.PlayerName}",
				Info = string.Join("\n", info),
				EmptyText = "No possible trinkets found.",
				Sections =
				{
					new PopupSection { Title = $"IN {who.ToUpper()} TYPES – typed trinkets for types with {threshold}+ minions", Highlight = true,
						Entries = g2.YourTypes.Select(TEntry).ToList() },
					new PopupSection { Title = "UNTYPED – every offer has at least one",
						Entries = g2.Untyped.Select(TEntry).ToList() },
					new PopupSection { Title = $"OTHER LOBBY TYPES – only if {(slot.IsSelf ? "you reach" : "they reach")} {threshold} of that type in time",
						Entries = g2.OtherTypes.Select(TEntry).ToList() },
				},
			};
		}

		private static string TierRange(int lo, int hi) => lo == hi ? $"{lo}" : $"{lo}–{hi}";

		// Farbe + Kürzel der Kugel je Diener-Keyword
		private static readonly Dictionary<string, (string label, System.Windows.Media.Color color)> OrbStyle = new Dictionary<string, (string, System.Windows.Media.Color)>
		{
			["Deathrattle"] = ("DR", System.Windows.Media.Color.FromRgb(170, 120, 230)),
			["Battlecry"] = ("BC", System.Windows.Media.Color.FromRgb(255, 150, 60)),
			["Divine Shield"] = ("DS", System.Windows.Media.Color.FromRgb(255, 220, 80)),
			["End of Turn"] = ("ET", System.Windows.Media.Color.FromRgb(90, 170, 255)),
			["Activate"] = ("AC", System.Windows.Media.Color.FromRgb(110, 220, 120)),
			["Spellcraft"] = ("SC", System.Windows.Media.Color.FromRgb(60, 210, 200)),
		};

		/// <summary>
		/// Dark Discovery: Minions aus dem aktuellen Tier-Fenster des Knopfs. Im Power.log beobachtet (20 Angebote):
		/// sobald man einen Hauptstamm hat, war in jedem Angebot mindestens ein Minion dieses Stamms – der Rest
		/// kommt gemischt aus den Lobby-Stämmen und stammlosen Minions. Kugeln = Spezial-Gaben, die der Minion freischaltet.
		/// </summary>
		private PopupContent DarkDiscoveryPopup(LobbySlot slot, int turn, HashSet<Race> races, string racesInfo, string boardInfo)
		{
			var (lo, hi, uses) = DarkGiftService.DiscoveryWindow(turn);
			var gifts = DarkGiftService.AvailableAt(turn, races).ToList();
			var pool = DarkGiftService.DiscoveryPool(lo, hi, races, Core.Game.IsBattlegroundsDuosMatch);
			var main = slot.Board?.Tribes.FirstOrDefault();
			var mainRace = main.HasValue && main.Value.count > 0 ? main.Value.race : (Race?)null;

			bool IsMain(Hearthstone_Deck_Tracker.Hearthstone.Card c) => mainRace.HasValue &&
				(c.RaceEnum == mainRace || c.SecondaryRaceEnum == mainRace || c.RaceEnum == Race.ALL);

			var generalCount = gifts.Count(g => g.RequiredMinionKeyword == null);

			PopupEntry MinionEntry(Hearthstone_Deck_Tracker.Hearthstone.Card c)
			{
				var specials = DarkGiftService.SpecialGiftsFor(c, gifts);
				return new PopupEntry
				{
					CardId = c.Id,
					Name = c.Name,
					Text = TextUtil.Clean(c.EnglishText),
					Badge = $"T{c.TechLevel}",
					// Kugeln = Spezial-Gaben, die dieser Minion freischaltet (Hover über die Kugel zeigt die Gabe)
					Orbs = specials.Select(g =>
					{
						var style = OrbStyle.TryGetValue(g.RequiredMinionKeyword, out var s) ? s : ("?", System.Windows.Media.Colors.Gray);
						return new PopupOrb { Label = style.Item1, Color = style.Item2, Title = g.Name, Text = g.Text };
					}).ToList(),
					// Hover übers Minion-Bild: welche Gaben dieser Minion bekommen kann (oben in der Reihe hervorgehoben)
					RelatedIds = new HashSet<string>(gifts.Where(g => g.RequiredMinionKeyword == null).Concat(specials).Select(g => g.CardId)),
					HoverTitle = c.Name,
					HoverText = specials.Count == 0
						? $"can get any of the {generalCount} general gifts (no special ones)"
						: $"can get {string.Join(", ", specials.Select(g => g.Name))} (special) + {generalCount} general gifts",
				};
			}

			var usesText = uses.HasValue ? $" · {uses} use{(uses == 1 ? "" : "s")} left" : slot.IsSelf ? "" : " · same tier window for everyone";
			var info = new List<string>
			{
				$"Dark Discovery: Tier {TierRange(lo, hi)}{usesText}",
				// Beobachtung aus 20 geloggten Angeboten: ab 4 Minions eines Stamms war immer mind. einer davon dabei; darunter nur eine Tendenz
				!mainRace.HasValue ? "No main tribe yet – offers are mixed"
					: main.Value.count >= 4 ? $"Main tribe: {BoardSummary.TribeName(mainRace.Value)} ({main.Value.count}) – with 4+ every logged offer had at least one"
					: $"Main tribe: {BoardSummary.TribeName(mainRace.Value)} ({main.Value.count}) – only a slight bias with so few minions",
				$"{boardInfo} · {racesInfo}",
			};

			// Alle Gaben dieses Zugs als kompakte Reihe ganz oben; Spezial-Gaben mit derselben Kugel wie auf den Minions.
			// Hinweis: Alle Dark Gifts haben dasselbe offizielle Bild (geprüft) – die Kugel unterscheidet sie.
			var synergy = new HashSet<string>(gifts.Where(g => DarkGiftService.Synergizes(g, slot.Board)).Select(g => g.CardId));
			var giftEntries = gifts
				.OrderBy(g => g.RequiredMinionKeyword == null ? 1 : 0).ThenBy(g => g.Name)
				.Select(g => new PopupEntry { CardId = g.CardId, Name = g.Name, Text = g.Text })
				.ToList();

			return new PopupContent
			{
				Header = $"Dark Gifts – {slot.PlayerName}",
				Info = string.Join("\n", info),
				OrbHint = "Hover a minion to see which Dark Gifts it can get (highlighted above). Balls = special gifts – hover a ball for details.",
				EmptyText = "No minions in the current tier window.",
				Sections =
				{
					new PopupSection { Title = "POSSIBLE DARK GIFTS THIS TURN – gold = fits the board", Compact = true,
						Entries = giftEntries, HighlightEntry = e => synergy.Contains(e.CardId) },
					new PopupSection { Title = mainRace.HasValue ? $"MAIN TRIBE – {BoardSummary.TribeName(mainRace.Value).ToUpper()}" : "MAIN TRIBE", Highlight = true,
						Entries = pool.Where(IsMain).Select(MinionEntry).ToList() },
					new PopupSection { Title = "OTHER POSSIBLE MINIONS",
						Entries = pool.Where(c => !IsMain(c)).Select(MinionEntry).ToList() },
				},
			};
		}

		private static string RegionCode()
		{
			switch(Core.Game.CurrentRegion)
			{
				case Region.US: return "US";
				case Region.EU: return "EU";
				case Region.ASIA: return "AP";
				default: return null; // China hat keine öffentliche Bestenliste über diese API
			}
		}
	}
}
