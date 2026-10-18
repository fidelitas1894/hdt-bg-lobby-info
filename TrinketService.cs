using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Newtonsoft.Json.Linq;
using Database = Hearthstone_Deck_Tracker.Hearthstone.Database;

namespace BgLobbyInfo
{
	public class Trinket
	{
		public int DbfId { get; set; }
		public string CardId { get; set; }
		public string Name { get; set; }
		public string Text { get; set; }
		public string EnglishText { get; set; }
		public bool IsGreater { get; set; }
		public bool DuosOnly { get; set; }
		public bool InHearthDbPool { get; set; }
		public List<Race> RequiredTribes { get; set; } = new List<Race>(); // leer = stammunabhängig
	}

	/// <summary>
	/// Liefert die kleinen (Lesser) und großen (Greater) Trinkets, die in der aktuellen Partie überhaupt
	/// angeboten werden können:
	///  1. aktueller Saison-Pool: HearthDb-Tag BACON_TRINKET ODER öffentliche HSReplay-Trinket-Guides
	///  2. Stämme: Trinkets mit BACON_SUBSET_* nur, wenn mindestens einer dieser Stämme in der Lobby ist
	///  3. Modus: BGDUO_-Trinkets nur in Duos
	/// Klein/Groß kommt aus SPELL_SCHOOL (LESSER_TRINKET=11 / GREATER_TRINKET=12).
	/// </summary>
	public class TrinketService
	{
		private const string GuidesUrl = "https://hsreplay.net/api/v1/battlegrounds/trinket_guides/";
		private const int LesserSchool = 11;
		private const int GreaterSchool = 12;

		private static readonly (GameTag tag, Race race)[] SubsetTags =
		{
			(GameTag.BACON_SUBSET_BEAST, Race.BEAST), (GameTag.BACON_SUBSET_DEMON, Race.DEMON),
			(GameTag.BACON_SUBSET_DRAGON, Race.DRAGON), (GameTag.BACON_SUBSET_ELEMENTALS, Race.ELEMENTAL),
			(GameTag.BACON_SUBSET_MECH, Race.MECHANICAL), (GameTag.BACON_SUBSET_MURLOC, Race.MURLOC),
			(GameTag.BACON_SUBSET_NAGA, Race.NAGA), (GameTag.BACON_SUBSET_PIRATE, Race.PIRATE),
			(GameTag.BACON_SUBSET_QUILLBOAR, Race.QUILBOAR), (GameTag.BACON_SUBSET_UNDEAD, Race.UNDEAD),
			(GameTag.BACON_SUBSET_ABERRATION, Race.ABERRATION),
		};

		private List<Trinket> _all;
		private HashSet<int> _currentPool; // aus HSReplay, null solange nicht geladen
		private Task _poolTask;

		public bool PoolFromHsReplay => _currentPool != null;

		public void Prefetch()
		{
			if(_poolTask == null || (_poolTask.IsCompleted && _currentPool == null))
				_poolTask = Task.Run(LoadPool);
		}

		private async Task LoadPool()
		{
			try
			{
				using(var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
				{
					http.DefaultRequestHeaders.UserAgent.ParseAdd("BgLobbyInfo-HDT-Plugin/0.2");
					var json = JArray.Parse(await http.GetStringAsync(GuidesUrl));
					var ids = json.Where(g => g["hidden"]?.Value<bool>() != true)
						.Select(g => g["trinket"]?.Value<int>() ?? 0).Where(id => id > 0).ToList();
					if(ids.Count > 0)
						_currentPool = new HashSet<int>(ids);
					Log.Info($"Trinket pool from HSReplay: {ids.Count}");
				}
			}
			catch(Exception e)
			{
				Log.Warn("Trinket pool could not be loaded, falling back to HearthDb flag: " + e.Message);
			}
		}

		private List<Trinket> All => _all ?? (_all = Load());

		private static List<Trinket> Load()
		{
			return HearthDb.Cards.All.Values
				.Where(c => c.Type == CardType.BATTLEGROUND_TRINKET)
				.Select(c =>
				{
					var school = c.Entity.GetTag(GameTag.SPELL_SCHOOL);
					if(school != LesserSchool && school != GreaterSchool)
						return null;
					var card = Database.GetCardFromId(c.Id);
					return new Trinket
					{
						DbfId = c.DbfId,
						CardId = c.Id,
						Name = c.Name,
						Text = TextUtil.Clean(c.Text),
						EnglishText = c.Text ?? "",
						IsGreater = school == GreaterSchool,
						DuosOnly = c.Id.StartsWith("BGDUO", StringComparison.OrdinalIgnoreCase),
						RequiredTribes = SubsetTags.Where(s => c.Entity.GetTag(s.tag) > 0).Select(s => s.race).ToList(),
						InHearthDbPool = c.Entity.GetTag(GameTag.BACON_TRINKET) > 0,
					};
				})
				.Where(t => t != null)
				.OrderBy(t => t.Name)
				.ToList();
		}

		/// <summary>Alle in dieser Lobby möglichen Trinkets der gewünschten Größe.</summary>
		public IEnumerable<Trinket> Possible(bool greater, ICollection<Race> lobbyRaces, bool duos)
		{
			var pool = _currentPool;
			return All.Where(t => t.IsGreater == greater
				&& InPool(t, pool)
				&& (duos || !t.DuosOnly)
				&& (t.RequiredTribes.Count == 0 || lobbyRaces == null || lobbyRaces.Count == 0 || t.RequiredTribes.Any(lobbyRaces.Contains)));
		}

		/// <summary>
		/// Im Spiel getestet: HSReplay führt nicht jede Variante (z.B. fehlte die große "Book of Medivh"-Version,
		/// die im Shop angeboten wurde). Deshalb Vereinigung aus HSReplay, HearthDb-Flag und Basis-Karte der "t"-Variante.
		/// </summary>
		private bool InPool(Trinket t, HashSet<int> pool)
		{
			if(t.InHearthDbPool)
				return true;
			if(pool == null)
				return false;
			if(pool.Contains(t.DbfId))
				return true;
			var baseId = System.Text.RegularExpressions.Regex.Replace(t.CardId, @"t\d*$", "");
			return baseId != t.CardId && All.Any(o => o.CardId == baseId && pool.Contains(o.DbfId));
		}

		/// <summary>
		/// Blizzard (Developer Insights "Updated Trinket Rules"): Man ist in einem Stamm "drin", wenn man beim
		/// Angebot 2 Diener (Lesser) bzw. 3 Diener (Greater) dieses Stamms hat. Mehr als 2–3 erhöht die Chance nicht.
		/// Jedes Angebot hat mindestens ein Stamm-Trinket und mindestens ein stammloses Trinket.
		/// </summary>
		public static int InTypeThreshold(bool greater) => greater ? 3 : 2;

		public static List<Race> InTypes(BoardSummary board, bool greater) =>
			board?.Tribes.Where(t => t.count >= InTypeThreshold(greater)).Select(t => t.race).ToList() ?? new List<Race>();

		public class Grouped
		{
			public List<Race> InTypes = new List<Race>();
			public List<Trinket> YourTypes = new List<Trinket>();   // Stamm-Trinkets für Stämme, in denen der Spieler "drin" ist
			public List<Trinket> Untyped = new List<Trinket>();     // stammlos – mindestens eins pro Angebot
			public List<Trinket> OtherTypes = new List<Trinket>();  // andere Lobby-Stämme – nur, wenn er die Schwelle noch erreicht
		}

		public Grouped Group(bool greater, ICollection<Race> lobbyRaces, bool duos, BoardSummary board)
		{
			var g = new Grouped { InTypes = InTypes(board, greater) };
			foreach(var t in Possible(greater, lobbyRaces, duos))
			{
				if(t.RequiredTribes.Count == 0)
					g.Untyped.Add(t);
				else if(t.RequiredTribes.Any(g.InTypes.Contains))
					g.YourTypes.Add(t);
				else
					g.OtherTypes.Add(t);
			}
			return g;
		}

		public Trinket ByDbf(int dbfId) => dbfId > 0 ? All.FirstOrDefault(t => t.DbfId == dbfId) : null;

		public static string NameOf(int dbfId) => dbfId > 0 ? Database.GetCardFromDbfId(dbfId, false)?.Name : null;
	}
}
