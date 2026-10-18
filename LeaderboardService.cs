using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Newtonsoft.Json.Linq;

namespace BgLobbyInfo
{
	/// <summary>
	/// Hearthstone verrät die MMR anderer Spieler nicht. Wie Firestone nutzen wir daher die
	/// öffentliche Blizzard-Bestenliste: Wer dort steht, bekommt seine echte MMR angezeigt,
	/// alle anderen liegen unter der Mindest-MMR der Liste ("&lt; 8000" o.ä.).
	/// </summary>
	public class LeaderboardService
	{
		private const string Url = "https://hearthstone.blizzard.com/en-us/api/community/leaderboardsData?region={0}&leaderboardId={1}&page={2}";
		private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
		private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

		private readonly object _lock = new object();
		private readonly Dictionary<string, Board> _boards = new Dictionary<string, Board>();

		private class Board
		{
			public DateTime LoadedAt;
			public Task LoadTask;
			public Dictionary<string, int> Ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			public int MinRating;
		}

		/// <summary>Startet das Laden im Hintergrund (idempotent).</summary>
		public void Prefetch(string region, bool duos) => GetBoard(region, duos);

		/// <summary>Liefert die MMR oder null + die Untergrenze der Bestenliste.</summary>
		public (int? rating, int minRating, bool loaded) Lookup(string region, bool duos, string battleTagName)
		{
			var board = GetBoard(region, duos);
			if(board.LoadTask?.IsCompleted != true)
				return (null, 0, false);
			var name = StripDiscriminator(battleTagName);
			// Namen sind nicht eindeutig – bei Duplikaten nehmen wir den höchsten Eintrag.
			return board.Ratings.TryGetValue(name, out var r) ? (r, board.MinRating, true) : ((int?)null, board.MinRating, true);
		}

		public static string StripDiscriminator(string name)
		{
			if(string.IsNullOrEmpty(name))
				return "";
			var i = name.IndexOf('#');
			return i >= 0 ? name.Substring(0, i) : name;
		}

		private Board GetBoard(string region, bool duos)
		{
			var key = $"{region}|{duos}";
			lock(_lock)
			{
				if(_boards.TryGetValue(key, out var existing) && (existing.LoadTask == null || !existing.LoadTask.IsCompleted || DateTime.UtcNow - existing.LoadedAt < CacheDuration))
					return existing;
				var board = new Board();
				board.LoadTask = Task.Run(() => Load(board, region, duos ? "battlegroundsduo" : "battlegrounds"));
				// Bis die neue Liste da ist, die alte weiterverwenden.
				if(existing != null && existing.Ratings.Count > 0)
				{
					board.LoadTask.ContinueWith(_ => { lock(_lock) _boards[key] = board; });
					existing.LoadedAt = DateTime.UtcNow;
					return existing;
				}
				_boards[key] = board;
				return board;
			}
		}

		private static async Task Load(Board board, string region, string leaderboardId)
		{
			try
			{
				var first = await FetchPage(region, leaderboardId, 1);
				var totalPages = first["leaderboard"]?["pagination"]?["totalPages"]?.Value<int>() ?? 1;
				var rows = new ConcurrentBag<JToken>(Rows(first));
				using(var gate = new SemaphoreSlim(8))
				{
					var tasks = Enumerable.Range(2, Math.Max(0, totalPages - 1)).Select(async page =>
					{
						await gate.WaitAsync();
						try
						{
							foreach(var row in Rows(await FetchPage(region, leaderboardId, page)))
								rows.Add(row);
						}
						catch(Exception e) { Log.Warn($"Leaderboard page {page} failed: {e.Message}"); }
						finally { gate.Release(); }
					});
					await Task.WhenAll(tasks);
				}
				foreach(var row in rows)
				{
					var name = row["accountid"]?.Value<string>();
					var rating = row["rating"]?.Value<int>() ?? 0;
					if(string.IsNullOrEmpty(name))
						continue;
					if(!board.Ratings.TryGetValue(name, out var cur) || rating > cur)
						board.Ratings[name] = rating;
				}
				board.MinRating = board.Ratings.Count > 0 ? board.Ratings.Values.Min() : 0;
				board.LoadedAt = DateTime.UtcNow;
				Log.Info($"Loaded {board.Ratings.Count} {leaderboardId} leaderboard entries for {region} (min {board.MinRating})");
			}
			catch(Exception e)
			{
				Log.Error(e);
				board.LoadedAt = DateTime.UtcNow - CacheDuration + TimeSpan.FromMinutes(2); // in 2 Min. erneut versuchen
			}
		}

		private static IEnumerable<JToken> Rows(JObject page) => page["leaderboard"]?["rows"]?.Children() ?? Enumerable.Empty<JToken>();

		private static async Task<JObject> FetchPage(string region, string leaderboardId, int page)
		{
			var json = await Http.GetStringAsync(string.Format(Url, region, leaderboardId, page));
			return JObject.Parse(json);
		}
	}
}
