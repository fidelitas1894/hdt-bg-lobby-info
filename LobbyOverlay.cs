using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Hearthstone_Deck_Tracker;

namespace BgLobbyInfo
{
	/// <summary>Daten für eine Zeile der linken Bestenliste.</summary>
	public class LobbySlot
	{
		public int Place { get; set; }            // 1..8, entspricht der Position im Spiel-Leaderboard
		public int PlayerId { get; set; }
		public string PlayerName { get; set; }
		public string MmrText { get; set; }
		public bool IsSelf { get; set; }
		public string BoardText { get; set; }
		public BoardSummary Board { get; set; }
		public int PickedLesserDbf { get; set; }  // BACON_FIRST_TRINKET_DATABASE_ID
		public int PickedGreaterDbf { get; set; } // BACON_SECOND_TRINKET_DATABASE_ID
	}

	public enum PopupKind { DarkGifts, LesserTrinkets, GreaterTrinkets }

	public class PopupEntry
	{
		public string CardId { get; set; } // für das Kartenbild; ohne CardId gibt es kein Bild
		public string Name { get; set; }
		public string Text { get; set; }
		public string Badge { get; set; }  // z.B. "T4" oben links auf dem Bild
		public List<PopupOrb> Orbs { get; set; } = new List<PopupOrb>(); // kleine Kugeln unten auf dem Bild
		// Hover übers Bild: Text für die Detailzeile + IDs der Einträge, die dabei in kompakten Abschnitten hervorgehoben werden
		public string HoverTitle { get; set; }
		public string HoverText { get; set; }
		public HashSet<string> RelatedIds { get; set; }
	}

	/// <summary>Kleine Kugel auf einem Minion-Bild, z.B. eine Spezial-Gabe; Details erscheinen beim Hovern.</summary>
	public class PopupOrb
	{
		public string Label { get; set; }
		public Color Color { get; set; }
		public string Title { get; set; }
		public string Text { get; set; }
	}

	public class PopupSection
	{
		public string Title { get; set; }
		public bool Highlight { get; set; } // goldene Namen
		public bool Compact { get; set; }   // kleine Kacheln, 6 pro Zeile
		public Func<PopupEntry, bool> HighlightEntry { get; set; } // optional: einzelne Einträge gold statt ganzer Abschnitt
		public List<PopupEntry> Entries { get; set; } = new List<PopupEntry>();
	}

	public class PopupContent
	{
		public string Header { get; set; }
		public string Info { get; set; }
		public string OrbHint { get; set; } // Text der Detailzeile, solange keine Kugel gehovert ist (null = keine Zeile)
		public List<PopupSection> Sections { get; set; } = new List<PopupSection>();
		public string EmptyText { get; set; } = "Nothing available.";
	}

	/// <summary>
	/// Rein im Code aufgebautes WPF-Overlay. Das HDT-Overlay ist klick-durchlässig, daher wird
	/// Hover über die Mausposition (GetCursorPos) erkannt statt über WPF-Mausereignisse.
	///
	/// - Gegner: kleine Knöpfe (✦ L G) rechts am Porträt im linken Leaderboard
	/// - Man selbst: Knöpfe im Stil der HDT-Turn-Anzeige, direkt links neben ihr (oben rechts)
	/// </summary>
	public class LobbyOverlay
	{
		// --- Layout (relativ zur Fensterhöhe; im Spiel kalibriert) ---
		// HDT selbst rechnet die Leaderboard-Kacheln mit Höhe*0.69/8 (OverlayWindow.BattlegroundsTileHeight).
		public static double TileHeightRatio = 0.69 / 8;
		public static double TopRatio = (1 - 0.69) / 2;     // Leaderboard ist vertikal zentriert
		public static double PortraitLeftFromCenter = 0.652; // linker Rand des Porträts = Breite/2 - Höhe*x (im Spiel gemessen)

		private const double ButtonSize = 18;
		private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(255, 210, 90));

		// Werte aus HDTs TurnCounter (Controls/Overlay/TurnCounter.xaml), damit die Selbst-Leiste gleich aussieht
		private static readonly Brush TopBarBackground = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x17));
		private static readonly FontFamily Chunkfive = new FontFamily(new Uri("pack://application:,,,/"), "/HearthstoneDeckTracker;component/Resources/#Chunkfive");

		private static readonly (PopupKind kind, string label, string selfLabel, Color fill, Color border)[] ButtonDefs =
		{
			(PopupKind.DarkGifts, "✦", "Gifts", Color.FromRgb(70, 20, 90), Color.FromRgb(180, 90, 230)),
			(PopupKind.LesserTrinkets, "L", "Lesser", Color.FromRgb(30, 60, 90), Color.FromRgb(110, 170, 230)),
			(PopupKind.GreaterTrinkets, "G", "Greater", Color.FromRgb(90, 60, 10), Color.FromRgb(230, 180, 70)),
		};

		// Hover-Verhalten: erst nach kurzem Verweilen öffnen, erst nach längerem Verlassen schließen
		private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(700);

		private readonly Canvas _canvas;
		private readonly List<SlotView> _views = new List<SlotView>();
		private readonly StackPanel _selfBar;
		private readonly Dictionary<PopupKind, (Border box, HearthstoneTextBlock text)> _selfButtons = new Dictionary<PopupKind, (Border, HearthstoneTextBlock)>();
		private readonly Border _popup;
		private readonly StackPanel _popupHeader;
		private readonly StackPanel _popupContent;
		private readonly ScrollViewer _popupScroll;
		private Func<LobbySlot, PopupKind, PopupContent> _popupProvider;
		private List<LobbySlot> _slots = new List<LobbySlot>();
		private string _shownKey;
		private FrameworkElement _openButton;    // Knopf, zu dem das Popup gerade gehört
		private FrameworkElement _pendingButton; // Knopf, über dem die Maus gerade verweilt
		private DateTime _pendingSince;
		private DateTime _lastInside;            // zuletzt über Knopf oder Popup

		private class SlotView
		{
			public Border MmrBadge;
			public TextBlock MmrText;
			public Border BoardPanel;
			public TextBlock BoardText;
			public Dictionary<PopupKind, Border> Buttons = new Dictionary<PopupKind, Border>();
		}

		public LobbyOverlay(Canvas canvas)
		{
			_canvas = canvas;
			for(var i = 0; i < 8; i++)
			{
				var v = new SlotView();
				v.MmrText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, TextAlignment = TextAlignment.Center };
				v.MmrBadge = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 30)), BorderBrush = new SolidColorBrush(Color.FromRgb(200, 160, 60)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0), Child = v.MmrText };
				v.BoardText = new TextBlock { Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.NoWrap };
				v.BoardPanel = new Border { Background = new SolidColorBrush(Color.FromArgb(190, 15, 15, 25)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Child = v.BoardText };
				Add(v.MmrBadge);
				Add(v.BoardPanel);
				foreach(var def in ButtonDefs)
				{
					var btn = new Border
					{
						Background = new SolidColorBrush(def.fill), BorderBrush = new SolidColorBrush(def.border), BorderThickness = new Thickness(1),
						CornerRadius = new CornerRadius(ButtonSize / 2), Width = ButtonSize, Height = ButtonSize,
						Child = new TextBlock { Text = def.label, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
					};
					v.Buttons[def.kind] = btn;
					Add(btn);
				}
				_views.Add(v);
			}

			// Selbst-Leiste im Stil der HDT-Turn-Anzeige: dunkle Kästen, Chunkfive 20, Margin 10,3
			_selfBar = new StackPanel { Orientation = Orientation.Horizontal };
			foreach(var def in ButtonDefs)
			{
				var text = new HearthstoneTextBlock { Text = def.selfLabel, FontFamily = Chunkfive, FontSize = 20, Margin = new Thickness(10, 3, 10, 3), Fill = Brushes.White };
				var box = new Border
				{
					Background = TopBarBackground, CornerRadius = new CornerRadius(0, 0, 3, 3), Margin = new Thickness(0, 0, 2, 0),
					BorderBrush = new SolidColorBrush(def.border), BorderThickness = new Thickness(0, 0, 0, 2), Child = text,
				};
				_selfButtons[def.kind] = (box, text);
				_selfBar.Children.Add(box);
			}
			Add(_selfBar);

			_popupHeader = new StackPanel();
			_popupContent = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
			_popupScroll = new ScrollViewer
			{
				Content = _popupContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false,
			};
			var layout = new DockPanel { LastChildFill = true };
			DockPanel.SetDock(_popupHeader, Dock.Top);
			layout.Children.Add(_popupHeader);
			layout.Children.Add(_popupScroll);
			_popup = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(245, 20, 10, 30)), BorderBrush = new SolidColorBrush(Color.FromRgb(180, 90, 230)), BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Width = PopupWidth, Child = layout, Visibility = Visibility.Collapsed
			};
			_canvas.Children.Add(_popup);
			InstallWheelHook();
		}

		private void Add(UIElement el)
		{
			el.Visibility = Visibility.Collapsed;
			_canvas.Children.Add(el);
		}

		/// <summary>Beschriftung des eigenen Gifts-Knopfs, z.B. "Gifts T4–5 · 2" (Tier-Fenster · verbleibende Nutzungen).</summary>
		public string SelfGiftsLabel { get; set; }

		public void SetPopupProvider(Func<LobbySlot, PopupKind, PopupContent> provider) => _popupProvider = provider;

		/// <param name="visibleButtons">Welche Hover-Knöpfe in dieser Partie Sinn ergeben (Dunkle Gaben / Trinkets aktiv).</param>
		public void Update(List<LobbySlot> slots, ICollection<PopupKind> visibleButtons)
		{
			_slots = slots;
			var h = _canvas.ActualHeight;
			var w = _canvas.ActualWidth;
			if(h <= 0 || w <= 0)
				return;
			var tile = h * TileHeightRatio;
			var top = h * TopRatio;
			var left = w / 2 - h * PortraitLeftFromCenter;

			for(var i = 0; i < _views.Count; i++)
			{
				var v = _views[i];
				var slot = slots.FirstOrDefault(s => s.Place == i + 1);
				if(slot == null)
				{
					HideSlot(v);
					continue;
				}
				var y = top + i * tile;

				// MMR oben auf dem Porträt
				v.MmrText.Text = slot.MmrText;
				v.MmrBadge.Visibility = string.IsNullOrEmpty(slot.MmrText) ? Visibility.Collapsed : Visibility.Visible;
				v.MmrBadge.MinWidth = tile * 0.7;
				Canvas.SetLeft(v.MmrBadge, left + tile * 0.15);
				Canvas.SetTop(v.MmrBadge, y + 1);

				// Board-Zusammenfassung rechts neben dem Porträt
				v.BoardText.Text = slot.IsSelf ? "" : slot.BoardText ?? "not seen yet";
				v.BoardPanel.Visibility = slot.IsSelf ? Visibility.Collapsed : Visibility.Visible;
				Canvas.SetLeft(v.BoardPanel, left + tile * 1.05 + ButtonSize + 4);
				Canvas.SetTop(v.BoardPanel, y + tile * 0.2);

				// Hover-Knöpfe als senkrechte Spalte direkt rechts am Porträt – nur für Gegner, man selbst hat die Leiste oben
				var shown = slot.IsSelf ? new List<(PopupKind, string, string, Color, Color)>() : ButtonDefs.Where(d => visibleButtons.Contains(d.kind)).ToList();
				var colTop = y + (tile - shown.Count * (ButtonSize + 1)) / 2;
				foreach(var def in ButtonDefs)
				{
					var btn = v.Buttons[def.kind];
					var idx = shown.FindIndex(d => d.Item1 == def.kind);
					btn.Visibility = idx >= 0 ? Visibility.Visible : Visibility.Collapsed;
					Canvas.SetLeft(btn, left + tile * 1.02);
					Canvas.SetTop(btn, colTop + Math.Max(0, idx) * (ButtonSize + 1));
				}
			}
			UpdateSelfBar(slots.FirstOrDefault(s => s.IsSelf), visibleButtons);
			UpdateHover();
		}

		/// <summary>
		/// Platziert die Selbst-Leiste direkt links neben HDTs Battlegrounds-Topbar (Turn-Anzeige + Tier-Knöpfe)
		/// und übernimmt deren Skalierung. Ist die Topbar nicht sichtbar, sitzt die Leiste oben rechts.
		/// </summary>
		private void UpdateSelfBar(LobbySlot self, ICollection<PopupKind> visibleButtons)
		{
			if(self == null || visibleButtons.Count == 0)
			{
				_selfBar.Visibility = Visibility.Collapsed;
				return;
			}
			foreach(var def in ButtonDefs)
			{
				var (box, text) = _selfButtons[def.kind];
				box.Visibility = visibleButtons.Contains(def.kind) ? Visibility.Visible : Visibility.Collapsed;
				var picked = def.kind == PopupKind.LesserTrinkets && self.PickedLesserDbf > 0 || def.kind == PopupKind.GreaterTrinkets && self.PickedGreaterDbf > 0;
				text.Text = def.kind == PopupKind.DarkGifts && !string.IsNullOrEmpty(SelfGiftsLabel) ? SelfGiftsLabel
					: picked ? def.selfLabel + " ✓" : def.selfLabel;
			}
			_selfBar.Visibility = Visibility.Visible;

			var topBar = Hearthstone_Deck_Tracker.Core.Overlay?.FindName("BgsTopBar") as FrameworkElement;
			_selfBar.UpdateLayout();
			if(topBar != null && topBar.IsVisible && topBar.ActualWidth > 0 && topBar.ActualHeight > 0)
			{
				var rect = topBar.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, topBar.ActualWidth, topBar.ActualHeight));
				var scale = rect.Height / topBar.ActualHeight;
				_selfBar.LayoutTransform = new ScaleTransform(scale, scale);
				_selfBar.UpdateLayout();
				Canvas.SetLeft(_selfBar, rect.Left - _selfBar.ActualWidth * scale - 2 * scale);
				Canvas.SetTop(_selfBar, rect.Top);
			}
			else
			{
				_selfBar.LayoutTransform = Transform.Identity;
				Canvas.SetLeft(_selfBar, _canvas.ActualWidth - _selfBar.ActualWidth - 8);
				Canvas.SetTop(_selfBar, 0);
			}
		}

		private static void HideSlot(SlotView v)
		{
			v.MmrBadge.Visibility = v.BoardPanel.Visibility = Visibility.Collapsed;
			foreach(var b in v.Buttons.Values)
				b.Visibility = Visibility.Collapsed;
		}

		/// <summary>
		/// Hover-Logik (per Mausposition, läuft bei jedem HDT-Update):
		/// - Knopf ≥ 250 ms überfahren → Popup öffnet (bzw. wechselt zu diesem Knopf)
		/// - Popup bleibt offen, solange die Maus auf Knopf oder Popup ist
		/// - erst 700 ms nach dem Verlassen von beidem schließt es
		/// </summary>
		private void UpdateHover()
		{
			if(!GetCursorPos(out var p))
				return;
			var now = DateTime.UtcNow;
			var (button, slot, kind) = ButtonUnderCursor(p);

			if(button == null)
				_pendingButton = null;
			else if(button != _pendingButton)
			{
				_pendingButton = button;
				_pendingSince = now;
			}

			if(button != null && button != _openButton && now - _pendingSince >= OpenDelay)
				Open(button, slot, kind);
			else if(button != null && button == _openButton)
				ShowPopup(slot, kind, refreshOnly: true);

			if(_popup.Visibility != Visibility.Visible)
				return;
			if((button != null && button == _openButton) || Contains(_popup, p))
				_lastInside = now;
			else if(now - _lastInside > CloseDelay)
				ClosePopup();
			UpdateOrbDetail(p);
		}

		/// <summary>Zeigt Name und Effekt der Kugel unter dem Cursor in der Detailzeile oben im Popup.</summary>
		private void UpdateOrbDetail(POINT p)
		{
			if(_orbDetail == null || _popup.Visibility != Visibility.Visible)
				return;
			foreach(var (el, orb) in _orbs)
			{
				if(!Contains(el, p))
					continue;
				_orbDetail.Inlines.Clear();
				_orbDetail.Inlines.Add(new Run("● " + orb.Title) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(orb.Color) });
				if(!string.IsNullOrEmpty(orb.Text))
					_orbDetail.Inlines.Add(new Run(" – " + orb.Text) { Foreground = Brushes.White });
				_orbDetail.Tag = "orb";
				_hoveredEntry = null; // beim Zurück aufs Minion-Bild dessen Text neu zeigen
				return;
			}
			foreach(var (el, entry) in _hoverEntries)
			{
				if(!Contains(el, p))
					continue;
				if(_hoveredEntry != entry)
				{
					_hoveredEntry = entry;
					_orbDetail.Inlines.Clear();
					_orbDetail.Inlines.Add(new Run(entry.HoverTitle) { FontWeight = FontWeights.Bold, Foreground = Gold });
					_orbDetail.Inlines.Add(new Run(" " + entry.HoverText) { Foreground = Brushes.White });
					// Mögliche Gaben oben hervorheben, alle anderen abdunkeln
					foreach(var pair in _compactTiles)
						pair.Value.Opacity = entry.RelatedIds == null || entry.RelatedIds.Contains(pair.Key) ? 1 : 0.2;
					_dimmed = true;
					_orbDetail.Tag = "entry";
				}
				return;
			}
			_hoveredEntry = null;
			if(_dimmed)
			{
				_dimmed = false;
				foreach(var tile in _compactTiles.Values)
					tile.Opacity = 1;
			}
			if(_orbDetail.Tag as string != "hint")
			{
				_orbDetail.Inlines.Clear();
				_orbDetail.Inlines.Add(new Run(_orbHint) { Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
			}
			_orbDetail.Tag = "hint";
		}

		private (FrameworkElement button, LobbySlot slot, PopupKind kind) ButtonUnderCursor(POINT p)
		{
			var self = _slots.FirstOrDefault(s => s.IsSelf);
			if(self != null && _selfBar.Visibility == Visibility.Visible)
				foreach(var pair in _selfButtons)
					if(Contains(pair.Value.box, p))
						return (pair.Value.box, self, pair.Key);

			for(var i = 0; i < _views.Count; i++)
			{
				foreach(var pair in _views[i].Buttons)
				{
					if(!Contains(pair.Value, p))
						continue;
					var slot = _slots.FirstOrDefault(s => s.Place == i + 1);
					return slot == null ? (null, null, default(PopupKind)) : (pair.Value, slot, pair.Key);
				}
			}
			return (null, null, default(PopupKind));
		}

		private static bool Contains(FrameworkElement el, POINT p)
		{
			if(!el.IsVisible || el.ActualWidth <= 0)
				return false;
			var tl = el.PointToScreen(new Point(0, 0));
			var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
			return p.X >= Math.Min(tl.X, br.X) && p.X <= Math.Max(tl.X, br.X) && p.Y >= Math.Min(tl.Y, br.Y) && p.Y <= Math.Max(tl.Y, br.Y);
		}

		private void Open(FrameworkElement button, LobbySlot slot, PopupKind kind)
		{
			_openButton = button;
			_lastInside = DateTime.UtcNow;
			ShowPopup(slot, kind, refreshOnly: false);
			var h = _canvas.ActualHeight;
			_popupScroll.MaxHeight = h * 0.75;
			_popup.UpdateLayout();
			var b = button.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
			double x, y;
			if(slot.IsSelf)
			{
				// Selbst-Leiste sitzt oben rechts: Popup darunter, rechtsbündig zum Knopf, leicht überlappend
				x = b.Right - _popup.ActualWidth;
				y = b.Bottom - 2;
			}
			else
			{
				// Leaderboard links: Popup rechts neben den Knopf, leicht überlappend
				x = b.Right - 2;
				y = b.Top - 20;
			}
			Canvas.SetLeft(_popup, Math.Max(4, Math.Min(x, _canvas.ActualWidth - _popup.ActualWidth - 4)));
			Canvas.SetTop(_popup, Math.Max(4, Math.Min(y, h - _popup.ActualHeight - 4)));
		}

		private void ClosePopup()
		{
			_popup.Visibility = Visibility.Collapsed;
			_openButton = null;
			_shownKey = null;
		}

		/// <param name="refreshOnly">true: nur neu aufbauen, wenn sich die Daten geändert haben (Scrollposition bleibt erhalten)</param>
		private void ShowPopup(LobbySlot slot, PopupKind kind, bool refreshOnly)
		{
			var key = $"{slot.Place}|{slot.PlayerName}|{kind}|{slot.Board?.Turn}|{slot.Board?.MinionCount}|{slot.PickedLesserDbf}|{slot.PickedGreaterDbf}|{Hearthstone_Deck_Tracker.API.Core.Game.GetTurnNumber()}";
			if(key == _shownKey && _popup.Visibility == Visibility.Visible)
				return;
			var sameSlot = _shownKey != null && _shownKey.StartsWith($"{slot.Place}|{slot.PlayerName}|{kind}|");
			_shownKey = key;
			var offset = _popupScroll.VerticalOffset;
			Render(_popupProvider?.Invoke(slot, kind) ?? new PopupContent());
			_popup.Visibility = Visibility.Visible;
			if(refreshOnly && sameSlot)
				_popupScroll.ScrollToVerticalOffset(offset);
			else
				_popupScroll.ScrollToTop();
		}

		private void Render(PopupContent content)
		{
			_popupHeader.Children.Clear();
			_popupContent.Children.Clear();
			_popupHeader.Children.Add(new TextBlock { Text = content.Header, Foreground = new SolidColorBrush(Color.FromRgb(210, 150, 255)), FontWeight = FontWeights.Bold, FontSize = 13 });
			if(!string.IsNullOrEmpty(content.Info))
				_popupHeader.Children.Add(new TextBlock { Text = content.Info, Foreground = Brushes.LightGray, FontSize = 11, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 2) });

			// Detailzeile für Kugeln: feste Mindesthöhe, damit das Popup beim Hovern nicht springt
			_orbs.Clear();
			_hoverEntries.Clear();
			_compactTiles.Clear();
			_hoveredEntry = null;
			_dimmed = false;
			_orbHint = content.OrbHint;
			_orbDetail = null;
			if(content.OrbHint != null)
			{
				_orbDetail = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MinHeight = 30, Margin = new Thickness(0, 2, 0, 2) };
				_orbDetail.Inlines.Add(new Run(content.OrbHint) { Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
				_orbDetail.Tag = "hint";
				_popupHeader.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 2, 5, 2), Child = _orbDetail });
			}

			var sections = content.Sections.Where(s => s.Entries.Count > 0).ToList();
			if(sections.Count == 0)
			{
				_popupContent.Children.Add(new TextBlock { Text = content.EmptyText, Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
				return;
			}
			foreach(var section in sections)
			{
				_popupContent.Children.Add(SectionHeader($"{section.Title} ({section.Entries.Count})"));
				// Grid: Kacheln nebeneinander, bei Platzmangel nächste Zeile
				var grid = new WrapPanel { Orientation = Orientation.Horizontal };
				foreach(var e in section.Entries)
				{
					var hl = section.Highlight || section.HighlightEntry?.Invoke(e) == true;
					grid.Children.Add(section.Compact ? CompactTile(e, hl) : EntryTile(e, hl));
				}
				_popupContent.Children.Add(grid);
			}
		}

		// 3 Kacheln pro Zeile; Breite = 3 × Kachel + Innenabstand + Scrollleiste
		private const double TileWidth = 132;
		private const double IconSize = 64;
		private const double PopupWidth = TileWidth * 3 + 16 + 6 + 20;

		// Kompakte Kacheln: 6 pro Zeile über die Inhaltsbreite (Popup minus Padding, Scrollleiste, Rand)
		private const double CompactTileWidth = (PopupWidth - 16 - 6 - 22) / 6;
		private const double CompactIconSize = 34;

		/// <summary>Kleine Kachel: Bild (mit optionaler Kugel), Name, Effekt in kleiner Schrift.</summary>
		private FrameworkElement CompactTile(PopupEntry e, bool highlight)
		{
			var tile = new StackPanel { Width = CompactTileWidth, Margin = new Thickness(0, 2, 0, 4) };
			var icon = new System.Windows.Shapes.Ellipse
			{
				Width = CompactIconSize, Height = CompactIconSize,
				Stroke = highlight ? Gold : new SolidColorBrush(Color.FromRgb(90, 80, 110)), StrokeThickness = highlight ? 2 : 1,
				Fill = new SolidColorBrush(Color.FromRgb(40, 30, 50)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
			};
			LoadIcon(icon, e.CardId);
			var stack = new Grid { Width = CompactIconSize + 14, Height = CompactIconSize + 4, HorizontalAlignment = HorizontalAlignment.Center };
			stack.Children.Add(icon);
			tile.Children.Add(stack);
			if(!string.IsNullOrEmpty(e.CardId))
				_compactTiles[e.CardId] = tile;
			tile.Children.Add(new TextBlock
			{
				Text = e.Name, FontWeight = FontWeights.Bold, FontSize = 9, Foreground = highlight ? Gold : Brushes.White,
				TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 1, 1, 0),
			});
			if(!string.IsNullOrEmpty(e.Text))
				tile.Children.Add(new TextBlock
				{
					Text = e.Text, FontSize = 8, Foreground = Brushes.LightGray,
					TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 0, 1, 0),
				});
			return tile;
		}

		private readonly List<(FrameworkElement el, PopupOrb orb)> _orbs = new List<(FrameworkElement, PopupOrb)>();
		private readonly List<(FrameworkElement el, PopupEntry entry)> _hoverEntries = new List<(FrameworkElement, PopupEntry)>();
		private readonly Dictionary<string, FrameworkElement> _compactTiles = new Dictionary<string, FrameworkElement>();
		private PopupEntry _hoveredEntry;
		private bool _dimmed;
		private TextBlock _orbDetail;
		private string _orbHint;
		private const double OrbSize = 17;

		/// <summary>Kachel: rundes Kartenbild (mit Tier-Abzeichen und Kugeln), darunter der Name, darunter der Effekt.</summary>
		private FrameworkElement EntryTile(PopupEntry e, bool highlight)
		{
			var tile = new StackPanel { Width = TileWidth, Margin = new Thickness(0, 4, 0, 6) };
			if(!string.IsNullOrEmpty(e.CardId))
			{
				var icon = new System.Windows.Shapes.Ellipse
				{
					Width = IconSize, Height = IconSize,
					Stroke = highlight ? Gold : new SolidColorBrush(Color.FromRgb(90, 80, 110)), StrokeThickness = highlight ? 3 : 2,
					Fill = new SolidColorBrush(Color.FromRgb(40, 30, 50)),
				};
				LoadIcon(icon, e.CardId);
				// Bild + Overlays übereinander; Kugeln ragen unten leicht aus dem Bild heraus
				var stack = new Grid { Width = IconSize + 24, Height = IconSize + OrbSize / 2, HorizontalAlignment = HorizontalAlignment.Center };
				icon.HorizontalAlignment = HorizontalAlignment.Center;
				icon.VerticalAlignment = VerticalAlignment.Top;
				stack.Children.Add(icon);
				if(!string.IsNullOrEmpty(e.Badge))
					stack.Children.Add(new Border
					{
						Background = TopBarBackground, CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0),
						HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0),
						Child = new TextBlock { Text = e.Badge, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold },
					});
				if(e.Orbs.Count > 0)
				{
					var orbRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
					foreach(var orb in e.Orbs)
					{
						var ball = new Border
						{
							Width = OrbSize, Height = OrbSize, CornerRadius = new CornerRadius(OrbSize / 2), Margin = new Thickness(1, 0, 1, 0),
							Background = new RadialGradientBrush(Color.FromRgb(255, 255, 255), orb.Color) { GradientOrigin = new Point(0.35, 0.3) },
							BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
							Child = new TextBlock { Text = orb.Label, FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
						};
						_orbs.Add((ball, orb));
						orbRow.Children.Add(ball);
					}
					stack.Children.Add(orbRow);
				}
				tile.Children.Add(stack);
				if(e.HoverTitle != null)
					_hoverEntries.Add((icon, e));
			}
			tile.Children.Add(new TextBlock
			{
				Text = e.Name, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = highlight ? Gold : Brushes.White,
				TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 3, 3, 1),
			});
			if(!string.IsNullOrEmpty(e.Text))
				tile.Children.Add(new TextBlock
				{
					Text = e.Text, FontSize = 10, Foreground = Brushes.LightGray,
					TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 0),
				});
			return tile;
		}

		/// <summary>
		/// Kartenbild über HDTs eigenen Asset-Downloader (gleicher Cache wie HDT, art.hearthstonejson.com).
		/// Aus dem Cache sofort, sonst asynchron nachladen.
		/// </summary>
		private static void LoadIcon(System.Windows.Shapes.Ellipse target, string cardId)
		{
			if(string.IsNullOrEmpty(cardId))
				return;
			try
			{
				var card = Hearthstone_Deck_Tracker.Hearthstone.Database.GetCardFromId(cardId);
				var downloader = Hearthstone_Deck_Tracker.Utility.Assets.AssetDownloaders.GetCardAssetDownloader(Hearthstone_Deck_Tracker.Utility.Assets.CardAssetType.Portrait);
				if(card == null || downloader == null)
					return;
				void Apply(System.Windows.Media.Imaging.BitmapSource img)
				{
					if(img != null)
						target.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
				}
				var cached = downloader.TryGetAssetData(card, false);
				if(cached != null)
				{
					Apply(cached);
					return;
				}
				var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
				downloader.GetAssetData(card, false).ContinueWith(t =>
				{
					if(t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
						Apply(t.Result);
				}, ui);
			}
			catch(Exception ex)
			{
				Hearthstone_Deck_Tracker.Utility.Logging.Log.Warn($"Icon for {cardId} failed: {ex.Message}");
			}
		}

		private static TextBlock SectionHeader(string text) =>
			new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 190)), FontSize = 10, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };

		public void Hide()
		{
			foreach(var v in _views)
				HideSlot(v);
			_selfBar.Visibility = Visibility.Collapsed;
			ClosePopup();
		}

		public void Remove()
		{
			foreach(var v in _views)
			{
				_canvas.Children.Remove(v.MmrBadge);
				_canvas.Children.Remove(v.BoardPanel);
				foreach(var b in v.Buttons.Values)
					_canvas.Children.Remove(b);
			}
			_canvas.Children.Remove(_selfBar);
			_canvas.Children.Remove(_popup);
			if(_hook != IntPtr.Zero)
				UnhookWindowsHookEx(_hook);
			_hook = IntPtr.Zero;
		}

		// --- Mausrad ---
		// Das HDT-Overlay ist klick-durchlässig, das Mausrad landet also im Spiel. HDTs eigene Freigabe
		// (OverlayExtensions.SetIsOverlayHitTestVisible) erkennt ein wanderndes, ein-/ausgeblendetes Popup nicht
		// zuverlässig (im Spiel getestet). Daher ein Low-Level-Maus-Hook: Steht der Cursor über dem offenen Popup,
		// scrollt das Rad die Liste und das Ereignis wird nicht an Hearthstone weitergegeben.
		private const int WH_MOUSE_LL = 14;
		private const int WM_MOUSEWHEEL = 0x020A;
		private const double PixelsPerNotch = 60;
		private IntPtr _hook = IntPtr.Zero;
		private LowLevelMouseProc _hookProc; // Referenz halten, sonst sammelt der GC den Delegate ein

		private void InstallWheelHook()
		{
			_hookProc = WheelHook;
			_hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
		}

		private IntPtr WheelHook(int nCode, IntPtr wParam, IntPtr lParam)
		{
			try
			{
				if(nCode >= 0 && (int)wParam == WM_MOUSEWHEEL && _popup.Visibility == Visibility.Visible)
				{
					var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
					if(Contains(_popup, info.pt))
					{
						var delta = (short)((info.mouseData >> 16) & 0xFFFF);
						_popupScroll.ScrollToVerticalOffset(_popupScroll.VerticalOffset - delta / 120.0 * PixelsPerNotch);
						_lastInside = DateTime.UtcNow;
						return (IntPtr)1; // geschluckt
					}
				}
			}
			catch
			{
				// Ein Fehler im Hook darf die Maus nie blockieren
			}
			return CallNextHookEx(_hook, nCode, wParam, lParam);
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct POINT { public int X; public int Y; }

		[StructLayout(LayoutKind.Sequential)]
		private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

		private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out POINT p);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool UnhookWindowsHookEx(IntPtr hhk);

		[DllImport("user32.dll")]
		private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandle(string lpModuleName);
	}
}
