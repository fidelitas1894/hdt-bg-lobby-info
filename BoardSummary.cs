using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgLobbyInfo
{
	/// <summary>Kurzzusammenfassung eines (zuletzt gesehenen) Gegner-Boards.</summary>
	public class BoardSummary
	{
		public int Turn { get; set; }
		public int MinionCount { get; set; }
		public int TotalAttack { get; set; }
		public int TotalHealth { get; set; }
		public int GoldenCount { get; set; }
		public List<(Race race, int count)> Tribes { get; set; } = new List<(Race, int)>();
		public HashSet<string> Keywords { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		public List<string> TopMinions { get; set; } = new List<string>();

		public static BoardSummary From(IReadOnlyCollection<Entity> minions, int turn)
		{
			var s = new BoardSummary { Turn = turn, MinionCount = minions.Count };
			var tribeCounts = new Dictionary<Race, int>();
			foreach(var m in minions)
			{
				s.TotalAttack += m.Attack;
				s.TotalHealth += m.Health;
				if(m.GetTag(GameTag.PREMIUM) == 1)
					s.GoldenCount++;
				foreach(var race in Races(m))
					tribeCounts[race] = tribeCounts.TryGetValue(race, out var c) ? c + 1 : 1;
				foreach(var kw in KeywordsOf(m))
					s.Keywords.Add(kw);
			}
			s.Tribes = tribeCounts.Where(x => x.Key != Race.ALL).OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList();
			// Amalgame (ALL) zählen für jeden Stamm mit
			if(tribeCounts.TryGetValue(Race.ALL, out var all) && all > 0)
				s.Tribes = s.Tribes.Select(t => (t.race, t.count + all)).ToList();
			s.TopMinions = minions.OrderByDescending(m => m.Attack + m.Health).Take(2)
				.Select(m => m.Card?.Name ?? m.CardId).ToList();
			return s;
		}

		private static IEnumerable<Race> Races(Entity m)
		{
			var card = m.Card;
			if(card?.RaceEnum is Race r1 && r1 != Race.INVALID && r1 != Race.BLANK)
				yield return r1;
			if(card?.SecondaryRaceEnum is Race r2 && r2 != Race.INVALID && r2 != Race.BLANK)
				yield return r2;
		}

		private static IEnumerable<string> KeywordsOf(Entity m)
		{
			if(m.GetTag(GameTag.DIVINE_SHIELD) > 0) yield return "Divine Shield";
			if(m.GetTag(GameTag.REBORN) > 0) yield return "Reborn";
			if(m.GetTag(GameTag.TAUNT) > 0) yield return "Taunt";
			if(m.GetTag(GameTag.WINDFURY) > 0) yield return "Windfury";
			if(m.GetTag(GameTag.POISONOUS) > 0 || m.GetTag(GameTag.VENOMOUS) > 0) yield return "Venomous";
			if(m.GetTag(GameTag.DEATHRATTLE) > 0) yield return "Deathrattle";
			if(m.GetTag(GameTag.BATTLECRY) > 0) yield return "Battlecry";
			var text = m.Card?.EnglishText ?? "";
			if(text.IndexOf("Spellcraft", StringComparison.OrdinalIgnoreCase) >= 0) yield return "Spellcraft";
			if(text.IndexOf("end of your turn", StringComparison.OrdinalIgnoreCase) >= 0) yield return "End of Turn";
			if(text.IndexOf("Rally", StringComparison.OrdinalIgnoreCase) >= 0) yield return "Rally";
			if(text.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) yield return "Activate";
		}

		public static string KeywordName(string keyword)
		{
			switch(keyword)
			{
				case "End of Turn": return "End of turn";
				default: return keyword;
			}
		}

		/// <summary>Zwei kompakte Zeilen fürs Overlay, z.B. "Undead ×4 · Mech ×2" / "6 minions · 87/112 · ★1 · 2 turns ago".</summary>
		public string ToShortText(int currentTurn)
		{
			var age = currentTurn - Turn;
			var ageText = age <= 0 ? "" : $" · {age} turn{(age == 1 ? "" : "s")} ago";
			// Auch beim leeren Board das Alter zeigen – sonst sieht ein uralter Snapshot aus wie der aktuelle Stand
			if(MinionCount == 0)
				return "empty board" + ageText;
			var tribes = Tribes.Count == 0 ? "no tribes" : string.Join(" · ", Tribes.Take(2).Select(t => $"{TribeName(t.race)} ×{t.count}"));
			var golden = GoldenCount > 0 ? $" · ★{GoldenCount}" : "";
			return $"{tribes}\n{MinionCount} minions · {TotalAttack}/{TotalHealth}{golden}{ageText}";
		}

		public static string TribeName(Race race)
		{
			switch(race)
			{
				case Race.BEAST: return "Beast";
				case Race.DEMON: return "Demon";
				case Race.DRAGON: return "Dragon";
				case Race.ELEMENTAL: return "Elemental";
				case Race.MECHANICAL: return "Mech";
				case Race.MURLOC: return "Murloc";
				case Race.NAGA: return "Naga";
				case Race.PIRATE: return "Pirate";
				case Race.QUILBOAR: return "Quilboar";
				case Race.UNDEAD: return "Undead";
				case Race.ABERRATION: return "Aberration";
				// Neue Stämme ohne eigenen Eintrag lesbar machen ("SOMETHING" → "Something")
				default: return race.ToString().Substring(0, 1) + race.ToString().Substring(1).ToLowerInvariant();
			}
		}
	}
}
