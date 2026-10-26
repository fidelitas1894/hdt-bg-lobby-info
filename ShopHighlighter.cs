using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace BgLobbyInfo
{
	/// <summary>
	/// Hebt Minions in der Taverne hervor, die eine angehakte Eigenschaft haben (Battlecry, Rally, Stamm …).
	/// - Auswahl über ein Klick-Widget (Kasten "Highlight" in der Selbst-Leiste). Klicks kommen über den
	///   Maus-Hook von <see cref="LobbyOverlay"/>, weil das HDT-Overlay klick-durchlässig ist.
	/// - Position der Taverne-Minions: HDTs eigenes OppBoardItemsControl. HDT blendet dort für jeden Minion
	///   auf dem gegnerischen Board (in BG = Bobs Taverne) ein Element ein, in Reihenfolge der ZONE_POSITION.
	/// - Auswahl wird in %APPDATA%\HearthstoneDeckTracker\BgLobbyInfo.highlight.txt gespeichert.
	/// </summary>
	public class ShopHighlighter
	{
		public class Option
		{
			public string Key;
			public string Label;
			public Func<Entity, bool> Match;
		}

		private static bool Tag(Entity e, GameTag tag) => e.GetTag(tag) > 0;

		private static bool CardHas(Entity e, string mechanic) =>
			e.Card?.Mechanics?.Contains(mechanic) == true;

		private static bool TextHas(Entity e, string text) =>
			(e.Card?.EnglishText ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

		/// <summary>Eigenschaften; geprüft wird das Entity (Tags aus dem Spiel) und zur Sicherheit die Kartendaten.</summary>
		public static readonly List<Option> Keywords = new List<Option>
		{
			new Option { Key = "battlecry", Label = "Battlecry", Match = e => Tag(e, GameTag.BATTLECRY) || CardHas(e, "BATTLECRY") },
			new Option { Key = "chooseone", Label = "Choose One", Match = e => Tag(e, GameTag.CHOOSE_ONE) || CardHas(e, "CHOOSE_ONE") || TextHas(e, "Choose One") },
			new Option { Key = "rally", Label = "Rally", Match = e => Tag(e, GameTag.BACON_RALLY) || CardHas(e, "BACON_RALLY") || TextHas(e, "Rally:") },
			new Option { Key = "deathrattle", Label = "Deathrattle", Match = e => Tag(e, GameTag.DEATHRATTLE) || CardHas(e, "DEATHRATTLE") },
			new Option { Key = "divineshield", Label = "Divine Shield", Match = e => Tag(e, GameTag.DIVINE_SHIELD) },
			new Option { Key = "taunt", Label = "Taunt", Match = e => Tag(e, GameTag.TAUNT) },
			new Option { Key = "reborn", Label = "Reborn", Match = e => Tag(e, GameTag.REBORN) },
			new Option { Key = "windfury", Label = "Windfury", Match = e => Tag(e, GameTag.WINDFURY) },
			new Option { Key = "venomous", Label = "Venomous", Match = e => Tag(e, GameTag.VENOMOUS) || Tag(e, GameTag.POISONOUS) },
			new Option { Key = "magnetic", Label = "Magnetic", Match = e => Tag(e, GameTag.MODULAR) || Tag(e, GameTag.MAGNETIC) || CardHas(e, "MODULAR") },
			new Option { Key = "avenge", Label = "Avenge", Match = e => Tag(e, GameTag.AVENGE) || CardHas(e, "AVENGE") },
			new Option { Key = "startofcombat", Label = "Start of Combat", Match = e => Tag(e, GameTag.START_OF_COMBAT) || CardHas(e, "START_OF_COMBAT") || TextHas(e, "Start of Combat") },
			new Option { Key = "endofturn", Label = "End of Turn", Match = e => Tag(e, GameTag.END_OF_TURN_TRIGGER) || TextHas(e, "end of your turn") },
			new Option { Key = "spellcraft", Label = "Spellcraft", Match = e => Tag(e, GameTag.SPELLCRAFT) || TextHas(e, "Spellcraft") },
			new Option { Key = "darkgift", Label = "Dark Gift", Match = e => Tag(e, GameTag.HAS_DARK_GIFT) || Tag(e, GameTag.DARK_GIFT) },
		};

		private static Option TribeOption(Race race) => new Option
		{
			Key = "tribe:" + race,
			Label = BoardSummary.TribeName(race),
			Match = e =>
			{
				var c = e.Card;
				return c != null && (c.RaceEnum == race || c.SecondaryRaceEnum == race || c.RaceEnum == Race.ALL);
			},
		};

		private static readonly string SettingsPath = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "BgLobbyInfo.highlight.txt");

		private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(255, 210, 90));
		private static readonly Brush PanelBackground = new SolidColorBrush(Color.FromArgb(245, 20, 10, 30));

		private readonly Canvas _canvas;
		private readonly HashSet<string> _selected;
		private readonly List<(Ellipse ring, Border label)> _marks = new List<(Ellipse, Border)>();
		private readonly Border _panel;
		private readonly StackPanel _panelContent;
		private readonly List<(FrameworkElement row, string key)> _rows = new List<(FrameworkElement, string)>();
		private List<Option> _tribeOptions = new List<Option>();
		private string _tribeKey = "";

		public ShopHighlighter(Canvas canvas)
		{
			_canvas = canvas;
			_selected = Load();
			_panelContent = new StackPanel();
			_panel = new Border
			{
				Background = PanelBackground, BorderBrush = Gold, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
				Padding = new Thickness(8, 6, 8, 6), Child = _panelContent, Visibility = Visibility.Collapsed,
			};
			_canvas.Children.Add(_panel);
			RebuildPanel();
		}

		public int SelectedCount => _selected.Count;
		public bool PanelOpen => _panel.Visibility == Visibility.Visible;

		// ---------------- Highlights in der Taverne ----------------

		/// <param name="active">Nur in der Shop-Phase einer BG-Partie; sonst alles ausblenden.</param>
		public void Update(bool active, ICollection<Race> lobbyRaces)
		{
			UpdateTribeOptions(lobbyRaces);
			var count = 0;
			if(active && _selected.Count > 0)
			{
				try
				{
					count = DrawMarks();
				}
				catch(Exception e)
				{
					Log.Warn("Shop highlight failed: " + e.Message);
				}
			}
			for(var i = count; i < _marks.Count; i++)
				_marks[i].ring.Visibility = _marks[i].label.Visibility = Visibility.Collapsed;
		}

		private int DrawMarks()
		{
			var items = Hearthstone_Deck_Tracker.Core.Overlay?.FindName("OppBoardItemsControl") as ItemsControl;
			if(items == null)
				return 0;
			var shop = Core.Game.Opponent.Board
				.Where(e => e.IsMinion)
				.OrderBy(e => e.GetTag(GameTag.ZONE_POSITION))
				.ToList();
			var options = Keywords.Concat(_tribeOptions).Where(o => _selected.Contains(o.Key)).ToList();
			var used = 0;
			for(var i = 0; i < shop.Count; i++)
			{
				var matched = options.Where(o => o.Match(shop[i])).Select(o => o.Label).ToList();
				if(matched.Count == 0)
					continue;
				if(!(items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement slot) || !slot.IsVisible || slot.ActualWidth <= 0)
					continue;
				var r = slot.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, slot.ActualWidth, slot.ActualHeight));
				var (ring, label) = Mark(used++);

				// Hearthstone-Minions sind Ovale: leicht schmaler/höher als das HDT-Feld
				ring.Width = r.Width * 0.9;
				ring.Height = r.Height * 0.9;
				Canvas.SetLeft(ring, r.Left + (r.Width - ring.Width) / 2);
				Canvas.SetTop(ring, r.Top + (r.Height - ring.Height) / 2);
				ring.Visibility = Visibility.Visible;

				((TextBlock)label.Child).Text = string.Join(" · ", matched);
				label.Visibility = Visibility.Visible;
				label.UpdateLayout();
				Canvas.SetLeft(label, r.Left + (r.Width - label.ActualWidth) / 2);
				Canvas.SetTop(label, r.Top - label.ActualHeight - 2);
			}
			return used;
		}

		private (Ellipse ring, Border label) Mark(int index)
		{
			while(_marks.Count <= index)
			{
				var ring = new Ellipse
				{
					Stroke = Gold, StrokeThickness = 4, Fill = new SolidColorBrush(Color.FromArgb(30, 255, 210, 90)),
					Effect = new DropShadowEffect { Color = Color.FromRgb(255, 200, 60), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 },
					IsHitTestVisible = false, Visibility = Visibility.Collapsed,
				};
				var label = new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(220, 20, 16, 10)), BorderBrush = Gold, BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
					Child = new TextBlock { Foreground = Gold, FontSize = 11, FontWeight = FontWeights.Bold },
				};
				_canvas.Children.Add(ring);
				_canvas.Children.Add(label);
				_marks.Add((ring, label));
			}
			return _marks[index];
		}

		// ---------------- Widget (Klick-Liste) ----------------

		private void UpdateTribeOptions(ICollection<Race> lobbyRaces)
		{
			var races = (lobbyRaces ?? new Race[0]).Where(r => r != Race.INVALID && r != Race.ALL).OrderBy(BoardSummary.TribeName).ToList();
			var key = string.Join(",", races);
			if(key == _tribeKey)
				return;
			_tribeKey = key;
			_tribeOptions = races.Select(TribeOption).ToList();
			RebuildPanel();
		}

		private void RebuildPanel()
		{
			_panelContent.Children.Clear();
			_rows.Clear();
			_panelContent.Children.Add(new TextBlock { Text = "Highlight in tavern", Foreground = Gold, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
			AddSection("KEYWORDS", Keywords);
			if(_tribeOptions.Count > 0)
				AddSection("TRIBES IN THIS LOBBY", _tribeOptions);
			var clear = new TextBlock { Text = "✕ Clear all", Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
			_panelContent.Children.Add(clear);
			_rows.Add((clear, "__clear"));
		}

		private void AddSection(string title, List<Option> options)
		{
			_panelContent.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 190)), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2) });
			// Zwei Spalten, damit die Liste nicht zu lang wird
			var grid = new WrapPanel { Width = 260 };
			foreach(var o in options)
			{
				var on = _selected.Contains(o.Key);
				var row = new Border
				{
					Width = 130, Padding = new Thickness(2, 1, 2, 1), Background = Brushes.Transparent,
					Child = new TextBlock
					{
						Text = (on ? "☑ " : "☐ ") + o.Label, FontSize = 12,
						Foreground = on ? Gold : Brushes.White, FontWeight = on ? FontWeights.Bold : FontWeights.Normal,
					},
				};
				grid.Children.Add(row);
				_rows.Add((row, o.Key));
			}
			_panelContent.Children.Add(grid);
		}

		/// <summary>
		/// Klick vom Maus-Hook. true = Klick gehört dem Widget (wird nicht ans Spiel weitergegeben).
		/// </summary>
		public bool HandleClick(Point screen, FrameworkElement toggleButton)
		{
			if(Contains(toggleButton, screen))
			{
				if(PanelOpen)
					_panel.Visibility = Visibility.Collapsed;
				else
					OpenPanel(toggleButton);
				return true;
			}
			if(!PanelOpen)
				return false;
			if(!Contains(_panel, screen))
			{
				_panel.Visibility = Visibility.Collapsed; // Klick daneben schließt, geht aber normal ans Spiel
				return false;
			}
			foreach(var (row, key) in _rows)
			{
				if(!Contains(row, screen))
					continue;
				if(key == "__clear")
					_selected.Clear();
				else if(!_selected.Remove(key))
					_selected.Add(key);
				Save();
				RebuildPanel();
				break;
			}
			return true; // Klicks im Panel nie ans Spiel durchreichen
		}

		public bool Contains(Point screen) => PanelOpen && Contains(_panel, screen);

		private void OpenPanel(FrameworkElement toggleButton)
		{
			_panel.Visibility = Visibility.Visible;
			_panel.UpdateLayout();
			var b = toggleButton.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, toggleButton.ActualWidth, toggleButton.ActualHeight));
			Canvas.SetLeft(_panel, Math.Max(4, Math.Min(b.Right - _panel.ActualWidth, _canvas.ActualWidth - _panel.ActualWidth - 4)));
			Canvas.SetTop(_panel, b.Bottom + 2);
		}

		public void ClosePanel() => _panel.Visibility = Visibility.Collapsed;

		private static bool Contains(FrameworkElement el, Point p)
		{
			if(el == null || !el.IsVisible || el.ActualWidth <= 0)
				return false;
			var tl = el.PointToScreen(new Point(0, 0));
			var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
			return p.X >= Math.Min(tl.X, br.X) && p.X <= Math.Max(tl.X, br.X) && p.Y >= Math.Min(tl.Y, br.Y) && p.Y <= Math.Max(tl.Y, br.Y);
		}

		public void Remove()
		{
			foreach(var (ring, label) in _marks)
			{
				_canvas.Children.Remove(ring);
				_canvas.Children.Remove(label);
			}
			_canvas.Children.Remove(_panel);
		}

		// ---------------- Speichern ----------------

		private static HashSet<string> Load()
		{
			try
			{
				if(File.Exists(SettingsPath))
					return new HashSet<string>(File.ReadAllLines(SettingsPath).Select(l => l.Trim()).Where(l => l.Length > 0));
			}
			catch(Exception e)
			{
				Log.Warn("Highlight settings could not be read: " + e.Message);
			}
			return new HashSet<string>();
		}

		private void Save()
		{
			try
			{
				File.WriteAllLines(SettingsPath, _selected.OrderBy(k => k));
			}
			catch(Exception e)
			{
				Log.Warn("Highlight settings could not be saved: " + e.Message);
			}
		}
	}
}
