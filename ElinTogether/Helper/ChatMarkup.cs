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
    internal static string Render(string text, int size, List<Sprite> icons, List<Thing>? links = null)
    {
        // (an item link: its name in brackets, the thing kept for the line's tooltip)
        var linked = 0;
        var withLinks = ItemCode.Replace(Escape(text), match => {
            if (++linked > MaxLinks || LinkedThing(match) is not { } thing) {
                return "[?]";
            }

            links?.Add(thing);
            return $"<b>[{thing.Name.Replace("<", "")}]</b>".TagColor(LinkColor);
        });
        return EmoteCode.Replace(BoldMentions(withLinks), match => {
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

    // ---------------------------------------------------------------- item links

    // [[item:id|material|level|enchant|blessing|count]]: what a display copy of the thing is made from
    private static readonly Regex ItemCode = new(@"\[\[item:([A-Za-z0-9_\-]{1,40})\|(-?\d{1,4})\|(-?\d{1,5})\|(-?\d{1,3})\|(-?\d{1,2})\|(\d{1,7})\]\]",
        RegexOptions.Compiled);

    // (a line draws three links at most; the copies are kept for the lines shown, a few dozen at most)
    private const int MaxLinks = 3;
    private static readonly Dictionary<string, Thing> LinkCopies = [];
    private static readonly Queue<string> LinkOrder = new();

    internal static readonly Color LinkColor = new(0.1f, 0.3f, 0.6f);

    /// <summary>The code of a link to this thing, as Shift+click in the inventory writes it</summary>
    internal static string LinkOf(Thing thing) =>
        $"[[item:{thing.id}|{thing.idMaterial}|{thing.LV}|{thing.encLV}|{(int)thing.blessedState}|{thing.Num}]]";

    /// <summary>
    ///     A copy of the linked thing for this game's eyes only (never sent, never placed): made once per code, from
    ///     values checked against the game's tables
    /// </summary>
    private static Thing? LinkedThing(Match code)
    {
        if (LinkCopies.TryGetValue(code.Value, out var known)) {
            return known;
        }

        var id = code.Groups[1].Value;
        var material = int.Parse(code.Groups[2].Value);
        if (!EClass.core.IsGameStarted || !EClass.sources.things.map.ContainsKey(id) || !EClass.sources.materials.map.ContainsKey(material)) {
            return null;
        }

        Thing thing;
        try {
            // (quiet: a card made for this game's eyes only is neither sent nor given a world uid, as the trade window's copies)
            using (Models.ElinDelta.Quiet()) {
                thing = ThingGen.Create(id, material, Mathf.Clamp(int.Parse(code.Groups[3].Value), 1, 9999));
            }

            thing.encLV = Mathf.Clamp(int.Parse(code.Groups[4].Value), -10, 99);
            var bless = int.Parse(code.Groups[5].Value);
            if (Enum.IsDefined(typeof(BlessedState), bless)) {
                thing.blessedState = (BlessedState)bless;
            }

            thing.SetNum(Mathf.Clamp(int.Parse(code.Groups[6].Value), 1, 999999));
            thing.c_IDTState = 0;
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Chat item link {Code} could not be shown", code.Value);
            return null;
        }

        LinkCopies[code.Value] = thing;
        LinkOrder.Enqueue(code.Value);
        while (LinkOrder.Count > 48) {
            LinkCopies.Remove(LinkOrder.Dequeue());
        }

        return thing;
    }

    /// <summary>"/me waves": what the speaker's character does, drawn "* Name waves"</summary>
    internal static bool IsAction(string text) => text.Length > 4 && text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The action of a "/me" line, without the command</summary>
    internal static string ActionOf(string text) => text.Substring(4).Trim();

    /// <summary>
    ///     The words of a line, for the pop text and the bubbles: no emote code; an action between stars, as the game
    ///     writes what a character does
    /// </summary>
    internal static string Plain(string text)
    {
        var source = ItemCode.Replace(IsAction(text) ? ActionOf(text) : text, match => LinkedThing(match) is { } thing ? $"[{thing.Name}]" : "[?]");
        var words = EmoteCode.Replace(source, match => IsEmote(match.Groups[1].Value) ? "" : match.Value).Trim();
        return IsAction(text) && words.Length > 0 ? $"*{words}*" : words;
    }
}
