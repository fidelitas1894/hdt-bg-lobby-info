using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;

namespace BgLobbyInfo
{
	public class DarkGift
	{
		public string CardId { get; set; }
		public string Name { get; set; }
		public string Text { get; set; }
		public string EnglishText { get; set; }
		public int MinTurn { get; set; }
		public int MaxTurn { get; set; }
		/// <summary>Gabe ergibt nur Sinn, wenn dieser Stamm in der Lobby ist (z.B. Polarization → Mech).</summary>
		public Race? RequiredLobbyRace { get; set; }
		/// <summary>Gabe wird nur Dienern mit diesem Keyword angeboten (z.B. Jaws of Death → Deathrattle).</summary>
		public string RequiredMinionKeyword { get; set; }
	}

	/// <summary>
	/// Liest alle Dunklen Gaben (Dark Gifts) aus HearthDb.
	/// Erkennung wie in HDT selbst: Battlegrounds-Set, IS_NIGHTMARE_BONUS (3567) &gt; 0.
	/// Angebotene Gaben sind die SPELL-Varianten mit Tag 4855 = 1 (die ENCHANTMENTs sind die Effekte auf dem Diener).
	/// Tag 4853/4854 = frühester/spätester Zug, in dem die Gabe angeboten werden kann
	/// (abgeleitet aus den Kartendaten: schwache "t"-Varianten haben frühere Fenster als die starken).
	///
	/// Anders als Trinkets haben Dunkle Gaben KEINE BACON_SUBSET-Stamm-Tags. Die Bedingungen stecken nur im
	/// Kartentext, deshalb werden sie hier aus dem englischen Text abgeleitet (siehe <see cref="Conditions"/>).
	/// </summary>
	public static class DarkGiftService
	{
		private const int IsNightmareBonus = 3567;
		private const int TagMinTurn = 4853;
		private const int TagMaxTurn = 4854;
		private const int TagOfferable = 4855;
		public const GameTag DarkGiftsActive = (GameTag)4845; // BACON_DARK_GIFTS_ACTIVE

		/// <summary>Textmuster → Bedingung. Keyword-Namen müssen zu BoardSummary.Keywords passen.</summary>
		private static readonly (string pattern, Race? race, string keyword)[] Conditions =
		{
			("Magnetize a random Mech", Race.MECHANICAL, null),
			("Fodder", Race.DEMON, null),
			("Blood Gem", Race.QUILBOAR, null),
			("minion's Spellcraft", Race.NAGA, "Spellcraft"),
			("minion's Divine Shield", null, "Divine Shield"),
			("minion's Deathrattle", null, "Deathrattle"),
			("minion's Battlecr", null, "Battlecry"),
			("minion's end of turn", null, "End of Turn"),
			("minion's Activate", null, "Activate"),
		};

		private static List<DarkGift> _all;

		public static IReadOnlyList<DarkGift> All => _all ?? (_all = Load());

		private static List<DarkGift> Load()
		{
			return HearthDb.Cards.All.Values
				.Where(c => c.Set == CardSet.BATTLEGROUNDS && c.Type == CardType.SPELL
					&& c.Entity.GetTag((GameTag)IsNightmareBonus) > 0 && c.Entity.GetTag((GameTag)TagOfferable) > 0)
				.Select(c =>
				{
					// Markup entfernen, sonst greifen Muster wie "minion's Divine Shield" nicht ("minion's <b>Divine Shield</b>")
					var english = System.Text.RegularExpressions.Regex.Replace(c.Text ?? "", "<[^>]+>", "");
					var cond = Conditions.FirstOrDefault(x => english.IndexOf(x.pattern, StringComparison.OrdinalIgnoreCase) >= 0);
					return new DarkGift
					{
						CardId = c.Id,
						Name = c.Name,
						Text = TextUtil.Clean(c.Text),
						EnglishText = english,
						MinTurn = c.Entity.GetTag((GameTag)TagMinTurn),
						MaxTurn = c.Entity.GetTag((GameTag)TagMaxTurn) is int max && max > 0 ? max : int.MaxValue,
						RequiredLobbyRace = cond.race,
						RequiredMinionKeyword = cond.keyword,
					};
				})
				.OrderBy(g => g.MinTurn).ThenBy(g => g.Name)
				.ToList();
		}

		/// <summary>
		/// Gaben, die im angegebenen Zug angeboten werden können und deren Stamm (falls nötig) in der Lobby ist.
		/// Ohne bekannte Lobby-Stämme wird nicht nach Stamm gefiltert.
		/// </summary>
		public static IEnumerable<DarkGift> AvailableAt(int turn, ICollection<Race> lobbyRaces) =>
			All.Where(g => turn >= g.MinTurn && turn <= g.MaxTurn
				&& (g.RequiredLobbyRace == null || lobbyRaces == null || lobbyRaces.Count == 0 || lobbyRaces.Contains(g.RequiredLobbyRace.Value)));

		/// <summary>Hinweis für das Popup, z.B. "only on minions with Deathrattle".</summary>
		public static string ConditionText(DarkGift gift)
		{
			switch(gift.RequiredMinionKeyword)
			{
				case "Divine Shield": return "only on minions with Divine Shield";
				case "Deathrattle": return "only on minions with Deathrattle";
				case "Battlecry": return "only on minions with Battlecry";
				case "End of Turn": return "only on minions with an end-of-turn effect";
				case "Activate": return "only on minions with Activate";
				case "Spellcraft": return "only on minions with Spellcraft";
				default: return null;
			}
		}

		// ---------------- Dark Discovery (Knopf "BG36_Button_DarkGift") ----------------

		public const string DiscoveryButtonId = "BG36_Button_DarkGift";

		/// <summary>
		/// Aktuelles Tier-Fenster des Dark-Discovery-Knopfs. Der Knopf trägt es selbst:
		/// TAG_SCRIPT_DATA_NUM_3 = untere, NUM_4 = obere Stufe, NUM_2 = verbleibende Nutzungen.
		/// Im Power.log geprüft: Das Fenster folgt nur dem Zug (nicht der eigenen Tavernenstufe) und war in allen
		/// Partien gleich – deshalb gibt es einen Fallback-Zeitplan, falls der Knopf (noch) nicht sichtbar ist.
		/// </summary>
		public static (int lo, int hi, int? usesLeft) DiscoveryWindow(int turn)
		{
			var game = Hearthstone_Deck_Tracker.API.Core.Game;
			var playerId = game.Player?.Id ?? 0;
			var button = game.Entities.Values.FirstOrDefault(e => e.CardId == DiscoveryButtonId && e.IsControlledBy(playerId));
			if(button != null)
			{
				var lo = button.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_3);
				var hi = button.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_4);
				if(lo > 0 && hi >= lo)
					return (lo, hi, button.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_2));
			}
			// Beobachteter Zeitplan (Zug → Tiers): ≤3: 2 · 4: 2–3 · 5: 3 · 6: 3–4 · 7: 4 · 8: 4–5 · 9: 4–6 · ab 10: 5–6
			if(turn <= 3) return (2, 2, null);
			if(turn == 4) return (2, 3, null);
			if(turn == 5) return (3, 3, null);
			if(turn == 6) return (3, 4, null);
			if(turn == 7) return (4, 4, null);
			if(turn == 8) return (4, 5, null);
			if(turn == 9) return (4, 6, null);
			return (5, 6, null);
		}

		/// <summary>Minions, die Dark Discovery im Tier-Fenster anbieten kann: Lobby-Stämme + stammlose, aus HDTs Minion-Pool.</summary>
		public static List<Hearthstone_Deck_Tracker.Hearthstone.Card> DiscoveryPool(int lo, int hi, ICollection<Race> lobbyRaces, bool duos)
		{
			var db = Hearthstone_Deck_Tracker.Utility.Battlegrounds.BattlegroundsDbSingleton.Current;
			if(db == null)
				return new List<Hearthstone_Deck_Tracker.Hearthstone.Card>();
			var races = new HashSet<Race>(lobbyRaces ?? (ICollection<Race>)db.Races) { Race.INVALID, Race.ALL };
			return db.GetCardsByRaces(races.ToList(), duos)
				.Where(c => c.TechLevel >= lo && c.TechLevel <= hi)
				.GroupBy(c => c.Id).Select(g => g.First())
				.OrderBy(c => c.TechLevel).ThenBy(c => c.Name)
				.ToList();
		}

		/// <summary>Keywords eines Minions, die Spezial-Gaben freischalten (Namen wie in <see cref="Conditions"/>).</summary>
		public static HashSet<string> MinionKeywords(Hearthstone_Deck_Tracker.Hearthstone.Card card)
		{
			var result = new HashSet<string>();
			var mechanics = card.Mechanics ?? new string[0];
			var text = System.Text.RegularExpressions.Regex.Replace(card.EnglishText ?? "", "<[^>]+>", "");
			if(mechanics.Contains("DEATHRATTLE") || text.Contains("Deathrattle")) result.Add("Deathrattle");
			if(mechanics.Contains("BATTLECRY") || text.Contains("Battlecry")) result.Add("Battlecry");
			if(mechanics.Contains("DIVINE_SHIELD") || text.Contains("Divine Shield")) result.Add("Divine Shield");
			if(text.IndexOf("end of your turn", StringComparison.OrdinalIgnoreCase) >= 0) result.Add("End of Turn");
			if(text.Contains("Spellcraft")) result.Add("Spellcraft");
			if(text.Contains("Activate")) result.Add("Activate");
			return result;
		}

		/// <summary>Gaben mit Diener-Bedingung, die genau dieser Minion freischaltet.</summary>
		public static List<DarkGift> SpecialGiftsFor(Hearthstone_Deck_Tracker.Hearthstone.Card card, IEnumerable<DarkGift> available)
		{
			var keywords = MinionKeywords(card);
			return available.Where(g => g.RequiredMinionKeyword != null && keywords.Contains(g.RequiredMinionKeyword)).ToList();
		}

		/// <summary>True, wenn die Gabe zum Board passt (Stamm/Keyword im Text kommt auf dem Board vor).</summary>
		public static bool Synergizes(DarkGift gift, BoardSummary board)
		{
			if(board == null)
				return false;
			if(gift.RequiredMinionKeyword != null)
				return board.Keywords.Contains(gift.RequiredMinionKeyword);
			if(gift.RequiredLobbyRace is Race race)
				return board.Tribes.Any(t => t.race == race);
			var text = gift.EnglishText;
			foreach(var kw in board.Keywords)
				if(text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			foreach(var (r, _) in board.Tribes.Take(2))
			{
				var raceName = r == Race.MECHANICAL ? "Mech" : r.ToString();
				if(text.IndexOf(raceName, StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			}
			return false;
		}
	}
}
