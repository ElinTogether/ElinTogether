using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     What a chat line holds besides words, written as plain text so it travels in the lines the mod already sends
///     (MsgSayDelta, WhisperDelta, within their 500 characters): :name: is one of the game's emotes (the icons of
///     Card.ShowEmo). The Chat tab draws them (Render), the pop text and the bubbles get the words (Plain)
/// </summary>
internal static class ChatMarkup
{
    /// <summary>The game's emotes (Emo), in its order: the ones a character shows over its head</summary>
    internal static readonly string[] Emotes = [..Enum.GetNames(typeof(Emo)).Where(n => n != nameof(Emo.none))];

    private static readonly Regex EmoteCode = new(":([a-z]+):", RegexOptions.Compiled);
    private static readonly Dictionary<string, Sprite?> Sprites = [];

    internal static bool IsEmote(string name) => Array.IndexOf(Emotes, name) >= 0;

    /// <summary>The icon the game draws over a character for that emote (TCText.ShowEmo)</summary>
    internal static Sprite? EmoteSprite(string name)
    {
        if (!Sprites.TryGetValue(name, out var sprite)) {
            Sprites[name] = sprite = IsEmote(name) ? SpriteSheet.Get("Media/Graphics/Icon/icons_32", "emo_" + name) : null;
        }

        return sprite;
    }

    /// <summary>The emote a line is made of alone (":happy:"), the one shown over the speaker's head</summary>
    internal static Emo? AloneEmote(string text)
    {
        var match = EmoteCode.Match(text.Trim());
        return match.Success && match.Length == text.Trim().Length && IsEmote(match.Groups[1].Value)
            ? (Emo)Enum.Parse(typeof(Emo), match.Groups[1].Value)
            : null;
    }

    /// <summary>
    ///     A player's words as rich text that cannot open a tag: "&lt;size=300&gt;" stays as typed (an empty bold tag
    ///     after each "&lt;" breaks it)
    /// </summary>
    internal static string Escape(string text) => text.Replace("<", "<<b></b>");

    /// <summary>
    ///     A line for the Chat tab: the words escaped, each emote a blank of <paramref name="size" /> (a quad) that
    ///     ChatIcons fills with the icon, in the order of <paramref name="icons" />
    /// </summary>
    internal static string Render(string text, int size, List<Sprite> icons)
    {
        return EmoteCode.Replace(BoldMentions(Escape(text)), match => {
            if (EmoteSprite(match.Groups[1].Value) is not { } sprite) {
                return match.Value;
            }

            icons.Add(sprite);
            return $"<quad size={size} width=1>";
        });
    }

    /// <summary>
    ///     The line names one of <paramref name="names" /> with an "@" before it (any case), not inside a longer word
    /// </summary>
    internal static bool Mentions(string text, IEnumerable<string> names)
    {
        foreach (var name in names) {
            var at = 0;
            while ((at = text.IndexOf("@" + name, at, StringComparison.OrdinalIgnoreCase)) >= 0) {
                var end = at + 1 + name.Length;
                if (end >= text.Length || !char.IsLetterOrDigit(text[end])) {
                    return true;
                }

                at = end;
            }
        }

        return false;
    }

    private static readonly Regex MentionCode = new(@"(?<![\w@])@[^\s@<>:]+", RegexOptions.Compiled);

    /// <summary>"@name" in bold, in the mention colour (on text already escaped)</summary>
    internal static string BoldMentions(string text) => MentionCode.Replace(text, m => $"<b>{m.Value}</b>".TagColor(MentionColor));

    /// <summary>The colour of a line naming this player and of the "@names" in any line</summary>
    internal static readonly Color MentionColor = new(0.65f, 0.25f, 0.05f);

    /// <summary>The words of a line, for the pop text and the bubbles: no emote code</summary>
    internal static string Plain(string text)
    {
        return EmoteCode.Replace(text, match => IsEmote(match.Groups[1].Value) ? "" : match.Value).Trim();
    }
}
