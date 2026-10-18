# BG Lobby Info – Hearthstone Deck Tracker plugin for Battlegrounds

A [Hearthstone Deck Tracker](https://hsreplay.net/downloads/) (HDT) plugin that adds lobby information to the Battlegrounds overlay. It is inspired by Firestone.

- **MMR** of every player in the lobby, shown on their portrait
- **Board summary** of each opponent's last seen board
- **Possible Lesser and Greater Trinkets** per player, or the ones they already picked
- **Dark Discovery helper**: the minions the Dark Gift button can offer, the Dark Gifts possible this turn, and which gifts each minion can get

All overlay text is in English, regardless of HDT's card language.

> Solo Battlegrounds only for now. EU, US and AP regions are supported; China is not.

---

## Features

### Leaderboard (left side)

| Element | What it shows |
|---|---|
| **MMR badge** on each portrait | Your own rating comes from HDT. For opponents, the plugin looks up their name on the public Blizzard leaderboard. Players who aren't listed show `<8,000` (the leaderboard cut-off). |
| **Board summary** next to each opponent | Top tribes, minion count, total stats, golden minions and how many turns ago the board was seen. For example: `Undead ×4 · Mech ×2` / `6 minions · 87/112 · ★1 · 2 turns ago`. |
| **✦ / L / G buttons** next to each opponent | Hover to open Dark Gifts, Lesser Trinkets or Greater Trinkets for that player. |

### Your bar (top right, next to HDT's turn counter)

The plugin adds `Gifts T4–5 · 2 | Lesser ✓ | Greater` next to HDT's turn counter, in the same style.

- **Gifts** shows the current tier window of the Dark Discovery button and how many uses you have left.
- A **✓** means you have already picked that trinket.

### Popups

**Opening and scrolling**
- Hover a button for about 0.25 seconds to open its popup.
- The popup stays open while the mouse is on the button or inside the popup, and closes about 0.7 seconds after you leave both.
- The mouse wheel scrolls the popup. While the cursor is over the popup, the game doesn't receive the scroll.
- Entries are shown as a grid: card art, then name, then effect.

**Trinkets (L / G)**
- Before the pick, the possible trinkets are grouped using Blizzard's published rules:
  - **In your types**: typed trinkets for tribes where the player has 2+ minions (Lesser) or 3+ minions (Greater).
  - **Untyped**: trinkets without a tribe. Every offer contains at least one.
  - **Other lobby types**: only possible if the player reaches the threshold for that tribe in time.
- After the pick, the popup shows only the picked trinket.

**Dark Gifts (✦ / Gifts)**
- **Top row**: every Dark Gift that can be offered this turn, 6 per row. Gifts that fit the player's board are gold.
- **Minions**: the minions Dark Discovery can offer in its current tier window, using HDT's minion pool for this lobby. Minions of the player's main tribe are listed first.
- **Balls on a minion's art** mark the special gifts that minion unlocks: **DR** Deathrattle, **DS** Divine Shield, **BC** Battlecry, **ET** End of Turn, **AC** Activate, **SC** Spellcraft. Hover a ball to see the gift.
- **Hover a minion's art** to see which gifts it can get. The other gifts in the top row are dimmed.
- During combat, your board is frozen at the end-of-shop state, so the popup doesn't change while minions die.

---

## How it works / data sources

| Data | Source |
|---|---|
| Lobby players and heroes | HDT / HearthMirror `BattlegroundsLobbyInfo` |
| Opponent MMR | Public Blizzard leaderboard (`hearthstone.blizzard.com/api/community/leaderboardsData`), cached for 30 minutes. Hearthstone itself doesn't expose other players' ratings. |
| Opponent boards | HDT's board snapshot after each combat |
| Lobby tribes and minion pool | HDT (`BattlegroundsUtils.GetAvailableRaces`, `BattlegroundsDbSingleton`) |
| Trinket pool for the current season | HearthDb tag `BACON_TRINKET`, combined with the public HSReplay trinket guide list |
| Trinket tribe | HearthDb `BACON_SUBSET_*` tags |
| Dark Discovery tier window and uses | The game's `BG36_Button_DarkGift` entity (`TAG_SCRIPT_DATA_NUM_3` / `_4` / `_2`) |
| Dark Gift turn windows | HearthDb tags 4853 / 4854 |
| Dark Gift conditions | Derived from the English card text |
| Card art | HDT's own asset downloader and cache (HearthstoneJSON) |

### Observations, not official rules

These come from analysing my own game logs. They may be wrong or change with patches.

- **Dark Discovery tiers** follow the turn, not your tavern tier. The sequence was T2 → T2–3 → T3 → T3–4 → T4 → T4–5 → T4–6 → T5–6, but it varied between games. The plugin therefore reads the live value from the button and only falls back to this schedule when the button isn't visible.
- **Dark Discovery offers** looked weighted by your board. In every logged offer where the player had 4+ minions of one tribe, at least one offered minion was of that tribe.
- **All Dark Gifts share the same card art**, so the top row is mainly told apart by name.

---

## Install

### Build from source

Requirements: Windows, the .NET SDK (any recent version), and HDT installed.

```bash
dotnet build -c Release -p:Deploy=true
```

This copies `BgLobbyInfo.dll` to `%APPDATA%\HearthstoneDeckTracker\Plugins\BgLobbyInfo\`. Then restart HDT and enable **BG Lobby Info** under *Options → Tracker → Plugins*.

The build references HDT's assemblies from `%LOCALAPPDATA%\HearthstoneDeckTracker\app-1.59.3`. If your HDT version differs, pass the folder:

```bash
dotnet build -c Release -p:Deploy=true -p:HdtDir="%LOCALAPPDATA%\HearthstoneDeckTracker\app-1.60.0"
```

---

## Configuration

The leaderboard layout is calibrated relative to the window height in `LobbyOverlay.cs`:

| Constant | Default | Meaning |
|---|---|---|
| `TileHeightRatio` | `0.69 / 8` | Height of one leaderboard tile (same formula as HDT) |
| `TopRatio` | `(1 - 0.69) / 2` | Top of the leaderboard |
| `PortraitLeftFromCenter` | `0.652` | Left edge of the portraits = width/2 − height × value |

Your bar positions itself automatically next to HDT's `BgsTopBar`.

---

## Known limitations

- Solo only. Duos uses a different leaderboard layout.
- Leaderboard names aren't unique, so the highest rating wins on duplicates. Players below the cut-off can't be distinguished.
- Opponents' Dark Discovery uses aren't visible; only the tier window is shown for them.
- Hearthstone's own card tooltips can appear while the mouse is over a popup, because the overlay passes the mouse through to the game.
- The plugin reads the mouse wheel with a Windows low-level mouse hook so the popup can scroll on HDT's click-through overlay.

---

## Credits

- Inspired by [Firestone](https://www.firestoneapp.com/).
- Built on [Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker) and HearthDb.
- Card art from [HearthstoneJSON](https://hearthstonejson.com/) via HDT.

Not affiliated with Blizzard Entertainment, HearthSim or Firestone. Hearthstone® is a trademark of Blizzard Entertainment, Inc.

## License

[MIT](LICENSE)
