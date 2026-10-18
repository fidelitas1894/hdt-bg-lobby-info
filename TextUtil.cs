using System.Text.RegularExpressions;

namespace BgLobbyInfo
{
	public static class TextUtil
	{
		/// <summary>Entfernt Kartentext-Markup (&lt;b&gt;, [x], $, {0}-Platzhalter) für die Anzeige im Overlay.</summary>
		public static string Clean(string text)
		{
			if(string.IsNullOrEmpty(text))
				return "";
			// Mehrteilige Texte ("...{0} turns left!)</i>2...") – nur den ersten Teil behalten
			text = Regex.Replace(text, @"</i>\d.*$", "</i>", RegexOptions.Singleline);
			text = Regex.Replace(text, "</?[bi]>", "");
			text = text.Replace("\n", " ").Replace("$", "").Replace("[x]", "");
			return Regex.Replace(text, @"\(\{\d\}\)|\{\d\}", "?").Trim();
		}
	}
}
