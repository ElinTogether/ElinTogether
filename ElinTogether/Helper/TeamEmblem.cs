using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElinTogether.Helper;

/// <summary>
///     A layer of a team emblem (PLAN_team_emblem): a sprite of the game, or a geometric shape. The emblem's old
///     background shape is a layer too (the bottom one), so the other layers may reach past it
/// </summary>
/// <param name="Thing">The typed id: "t:sword" (a thing), "c:putty" (a monster) or "g:star" (a shape, <see cref="TeamEmblem.ShapeKinds" />)</param>
/// <param name="X">Centre on the 64 grid from the left, -32..96 (outside 0..64 the layer hangs off the emblem)</param>
/// <param name="Y">Centre on the 64 grid from the top, -32..96</param>
/// <param name="Size">The longer side, in % of the emblem, 10..200</param>
/// <param name="Rotation">Degrees clockwise, 0..359</param>
/// <param name="Flip">1 horizontal, 2 vertical</param>
/// <param name="Color">0xRRGGBBAA: a shape's fill, a sprite's tint (0 for the sprite's own colours)</param>
/// <param name="Opacity">10..100 %</param>
/// <param name="Outline">The border's width in pixels of the 64 grid, 0..6</param>
/// <param name="OutlineColor">The border's colour, 0xRRGGBBAA</param>
/// <param name="Pattern">A shape's fill pattern 0..7 (<see cref="TeamEmblem.PatternCount" />)</param>
/// <param name="Color2">The pattern's second colour</param>
/// <param name="Sides">A star's points or a polygon's sides, 3..12</param>
/// <param name="Aspect">The shorter side in % of the longer one, 10..100</param>
/// <param name="Thick">A ring's, a line's or a cross's width, a star's inner radius, a rounded square's corner, 5..90 %</param>
internal readonly record struct EmblemLayer(
    string Thing,
    int X,
    int Y,
    int Size,
    int Rotation,
    int Flip,
    uint Color,
    int Opacity = 100,
    int Outline = 0,
    uint OutlineColor = TeamEmblem.Ink,
    int Pattern = 0,
    uint Color2 = 0,
    int Sides = 5,
    int Aspect = 100,
    int Thick = 30)
{
    internal bool IsShape => Thing.StartsWith("g:");

    /// <summary>The shape's name ("star"), empty for a sprite</summary>
    internal string Kind => IsShape ? Thing.Substring(2) : "";
}

/// <param name="Layers">Drawn in order, the first at the bottom</param>
/// <param name="Clip">Every layer is cut to the bottom layer's outline (how every emblem before layer shapes drew)</param>
internal sealed record Emblem(IReadOnlyList<EmblemLayer> Layers, bool Clip = false);

/// <summary>
///     The text of a team emblem, kept in the team's thing id (<see cref="Net.ElinNetHost.TeamThings" />) and told to
///     every game. Three forms; the oldest one that can hold the emblem is written, so games of an older version still
///     draw it:
///     <c>e1|bg|shape|L;L;...</c>, L = <c>kind:id,x,y,s,q,f,c</c> (q quarter turns, c 0..11 or 12 own colours);
///     <c>e2|bg|shape|pattern|bg2|L;L;...</c>, L = <c>kind:id,x,y,s,r,f,c,o,e</c> (r eighths of a turn, c 0..23 or 24
///     own colours, o opacity %, e outline 0/1): both a background shape of the palette, cut to it, under sprites;
///     <c>e3|clip|L;L;...</c>, L = <c>kind:id,x,y,s,r,f,RRGGBBAA[,o,w,RRGGBBAA,p,RRGGBBAA,n,a,t]</c> (r degrees, the
///     fields after the colour left out when they are the defaults). An e1/e2 text reads as e3 with its background as
///     the bottom shape layer and clip on (<see cref="FromLegacy" />), and draws exactly as before
/// </summary>
internal static class TeamEmblem
{
    internal const string Prefix = "e1|";
    internal const string Prefix2 = "e2|";
    internal const string Prefix3 = "e3|";
    internal const int MaxText = 2400;
    internal const int MaxLayers = 16;
    internal const int Grid = 64;
    internal const int MinSize = 10;
    internal const int MaxSize = 200;
    internal const int MinOpacity = 10;
    internal const int MinPos = -Grid / 2;
    internal const int MaxPos = Grid + Grid / 2;
    internal const int MaxOutline = 6;

    /// <summary>The colours of the old emblem palette (<see cref="TeamBanner.EmblemColorCount" />)</summary>
    internal const int Colours = 24;

    /// <summary>The old tint that keeps the sprite's own colours</summary>
    internal const int OwnColours = Colours;

    /// <summary>A sprite's tint that keeps its own colours</summary>
    internal const uint Own = 0u;

    /// <summary>The old outline's dark ink as RGBA (the baker draws it with its exact float colour)</summary>
    internal const uint Ink = 0x1A140FFFu;

    internal const int PatternCount = 8;

    /// <summary>
    ///     The shapes, by their name in the text. The first six are the old background shapes, in their old order
    /// </summary>
    internal static readonly string[] ShapeKinds = [
        "square", "round", "shield", "banner", "diamond", "hexagon",
        "rounded", "triangle", "star", "polygon", "line", "cross", "ring",
    ];

    internal const int ShapeCount = 6;

    // what the first two forms can hold
    private const int OldLayers = 8;
    private const int OldColours = 12;
    private const int OldShapes = 3;
    private const int SecondLayers = 12;

    /// <summary>One sprite layer at the centre, full size, own colours: how an old single-thing banner draws</summary>
    internal static EmblemLayer Layer(string thing) => new(thing, Grid / 2, Grid / 2, 100, 0, 0, Own);

    /// <summary>A shape layer of that kind and colour, centred</summary>
    internal static EmblemLayer Shape(string kind, uint colour, int size = 100) =>
        new("g:" + kind, Grid / 2, Grid / 2, size, 0, 0, colour, Color2: colour, Thick: kind switch {
            "star" => 45,
            "line" => 12,
            "cross" => 16,
            "ring" => 25,
            "rounded" => 30,
            _ => 30,
        });

    /// <summary>The colour of a palette index (0..23) as 0xRRGGBBAA</summary>
    internal static uint Rgba(int palette) => ((uint)TeamBanner.Palette[Math.Max(0, Math.Min(palette, TeamBanner.Palette.Length - 1))] << 8) | 0xFFu;

    /// <summary>The palette index of an opaque palette colour, else -1</summary>
    internal static int PaletteIndex(uint rgba)
    {
        if ((rgba & 0xFF) != 0xFF) {
            return -1;
        }

        return Array.IndexOf(TeamBanner.Palette, (int)(rgba >> 8));
    }

    // ---------------------------------------------------------------- the two old forms

    /// <summary>A sprite layer as the first two forms hold it</summary>
    internal readonly record struct OldLayer(string Thing, int X, int Y, int Size, int Rotation8, int Flip, int Tint, int Opacity, int Outline);

    /// <summary>An emblem of the first two forms: a background of the palette in a shape and a pattern, sprites over it</summary>
    internal sealed record Legacy(int Background, int Shape, IReadOnlyList<OldLayer> Layers, int Pattern, int Background2);

    /// <summary>The emblem an old form describes: its background as the bottom shape layer, everything cut to it</summary>
    internal static Emblem? FromLegacy(Legacy old)
    {
        if (old.Background is < 0 or >= Colours || old.Background2 is < 0 or >= Colours || old.Shape is < 0 or >= ShapeCount ||
            old.Pattern is < 0 or >= PatternCount) {
            return null;
        }

        var layers = new List<EmblemLayer> {
            new("g:" + ShapeKinds[old.Shape], Grid / 2, Grid / 2, 100, 0, 0, Rgba(old.Background), Pattern: old.Pattern,
                Color2: Rgba(old.Background2)),
        };
        foreach (var l in old.Layers) {
            if (l.Tint is < 0 or > OwnColours || l.Rotation8 is < 0 or > 7) {
                return null;
            }

            layers.Add(new(l.Thing, l.X, l.Y, l.Size, l.Rotation8 * 45, l.Flip, l.Tint == OwnColours ? Own : Rgba(l.Tint), l.Opacity,
                l.Outline != 0 ? 1 : 0));
        }

        return new(layers, true);
    }

    /// <summary>
    ///     The emblem in the old forms' terms when they hold it exactly (a palette background shape at full size, cut
    ///     to it, under up to twelve sprites of the palette or their own colours turned by eighths), else null. Such an
    ///     emblem is written in an old form and baked by the old drawing
    /// </summary>
    internal static Legacy? ToLegacy(Emblem e)
    {
        if (!e.Clip || e.Layers.Count is < 1 or > SecondLayers + 1) {
            return null;
        }

        var b = e.Layers[0];
        var shape = Array.IndexOf(ShapeKinds, b.Kind);
        var bg = PaletteIndex(b.Color);
        var bg2 = PaletteIndex(b.Color2);
        if (shape is < 0 or >= ShapeCount || bg < 0 || bg2 < 0 ||
            b != new EmblemLayer(b.Thing, Grid / 2, Grid / 2, 100, 0, 0, b.Color, Pattern: b.Pattern, Color2: b.Color2) ||
            b.Pattern is < 0 or >= PatternCount) {
            return null;
        }

        var old = new List<OldLayer>();
        foreach (var l in e.Layers.Skip(1)) {
            var tint = l.Color == Own ? OwnColours : PaletteIndex(l.Color);
            if (l.IsShape || tint < 0 || l.X is < 0 or > Grid || l.Y is < 0 or > Grid || l.Rotation % 45 != 0 ||
                l.Outline > 1 || (l.Outline == 1 && l.OutlineColor != Ink) || !Plain(l)) {
                return null;
            }

            old.Add(new(l.Thing, l.X, l.Y, l.Size, l.Rotation / 45, l.Flip, tint, l.Opacity, l.Outline));
        }

        return new(bg, shape, old, b.Pattern, bg2);
    }

    // a sprite layer's shape fields at their defaults (they mean nothing for a sprite)
    private static bool Plain(EmblemLayer l) =>
        l.Pattern == 0 && l.Color2 == 0 && l.Sides == 5 && l.Aspect == 100 && l.Thick == 30 && (l.Outline != 0 || l.OutlineColor == Ink);

    /// <summary>The first form holds it: what the oldest games can read</summary>
    private static bool FitsFirstForm(Legacy e) =>
        e.Pattern == 0 && e.Background < OldColours && e.Shape < OldShapes && e.Layers.Count <= OldLayers &&
        e.Layers.All(l => l.Rotation8 % 2 == 0 && (l.Tint < OldColours || l.Tint == OwnColours) && l.Opacity >= 100 && l.Outline == 0);

    // ---------------------------------------------------------------- read and write

    /// <summary>
    ///     The emblem of a stored value: an <c>e1|</c>, <c>e2|</c> or <c>e3|</c> text, or an old value (t:id, c:id, bare
    ///     id) as one layer on a square of <paramref name="background" />. Null when the text cannot be read. Ids are
    ///     not checked here
    /// </summary>
    internal static Emblem? Parse(string? text, int background = 0)
    {
        if (string.IsNullOrEmpty(text)) {
            return null;
        }

        if (text!.StartsWith(Prefix3)) {
            return ParseThird(text);
        }

        var second = text.StartsWith(Prefix2);
        if (!second && !text.StartsWith(Prefix)) {
            // legacy: one thing, typed or not
            if (text.IndexOfAny(['|', ';', ',']) >= 0) {
                return null;
            }

            var typed = text.StartsWith("t:") || text.StartsWith("c:") ? text : "t:" + text;
            return FromLegacy(new(background, 0, [new(typed, Grid / 2, Grid / 2, 100, 0, 0, OwnColours, 100, 0)], 0, background));
        }

        var parts = text.Split('|');
        if (parts.Length != (second ? 6 : 4) || !Int(parts[1], out var bg) || !Int(parts[2], out var shape)) {
            return null;
        }

        var (pattern, bg2) = (0, bg);
        if (second && (!Int(parts[3], out pattern) || !Int(parts[4], out bg2))) {
            return null;
        }

        var fields = second ? 8 : 6;
        var layers = new List<OldLayer>();
        var list = parts[parts.Length - 1];
        if (list.Length > 0) {
            foreach (var chunk in list.Split(';')) {
                var f = chunk.Split(',');
                if (f.Length != fields + 1 || !Sprite(f[0])) {
                    return null;
                }

                var n = new int[fields];
                for (var i = 0; i < fields; i++) {
                    if (!Int(f[i + 1], out n[i])) {
                        return null;
                    }
                }

                layers.Add(second
                    ? new(f[0], n[0], n[1], n[2], n[3], n[4], n[5], n[6], n[7])
                    // (the first form: quarter turns, and 12 for the sprite's own colours)
                    : new(f[0], n[0], n[1], n[2], n[3] * 2, n[4], n[5] == OldColours ? OwnColours : n[5], 100, 0));
            }
        }

        return FromLegacy(new(bg, shape, layers, pattern, bg2));
    }

    private static bool Sprite(string thing) => thing.Length >= 3 && (thing.StartsWith("t:") || thing.StartsWith("c:"));

    private static Emblem? ParseThird(string text)
    {
        var parts = text.Split('|');
        if (parts.Length != 3 || !Int(parts[1], out var clip) || clip is < 0 or > 1) {
            return null;
        }

        var layers = new List<EmblemLayer>();
        if (parts[2].Length > 0) {
            foreach (var chunk in parts[2].Split(';')) {
                var f = chunk.Split(',');
                if (f.Length is < 7 or > 15 || f[0].Length < 3 || (!Sprite(f[0]) && !f[0].StartsWith("g:"))) {
                    return null;
                }

                // x, y, s, r, f, colour, then the optional fields in order (two of them colours)
                var n = new long[15];
                var defaults = new EmblemLayer("", 0, 0, 0, 0, 0, 0);
                n[6] = defaults.Opacity;
                n[7] = defaults.Outline;
                n[8] = defaults.OutlineColor;
                n[9] = defaults.Pattern;
                n[10] = defaults.Color2;
                n[11] = defaults.Sides;
                n[12] = defaults.Aspect;
                n[13] = defaults.Thick;
                for (var i = 1; i < f.Length; i++) {
                    // (the fill, the outline's and the pattern's second colour are hex)
                    if (i is 6 or 9 or 11) {
                        if (!Hex(f[i], out var c)) {
                            return null;
                        }

                        n[i - 1] = c;
                    } else if (Int(f[i], out var v)) {
                        n[i - 1] = v;
                    } else {
                        return null;
                    }
                }

                layers.Add(new(f[0], (int)n[0], (int)n[1], (int)n[2], (int)n[3], (int)n[4], (uint)n[5], (int)n[6], (int)n[7],
                    (uint)n[8], (int)n[9], (uint)n[10], (int)n[11], (int)n[12], (int)n[13]));
            }
        }

        return new(layers, clip == 1);
    }

    private static string S(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string H(uint rgba) => rgba.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The canonical text of an emblem (what the host stores and tells), in the oldest form that holds it</summary>
    internal static string Format(Emblem emblem)
    {
        if (ToLegacy(emblem) is { } legacy) {
            return FormatLegacy(legacy);
        }

        var layers = emblem.Layers.Select(l => {
            var head = new List<string> { l.Thing, S(l.X), S(l.Y), S(l.Size), S(l.Rotation), S(l.Flip), H(l.Color) };
            var tail = new[] { S(l.Opacity), S(l.Outline), H(l.OutlineColor), S(l.Pattern), H(l.Color2), S(l.Sides), S(l.Aspect), S(l.Thick) };
            var d = new EmblemLayer("", 0, 0, 0, 0, 0, 0);
            var plain = new[] { S(d.Opacity), S(d.Outline), H(d.OutlineColor), S(d.Pattern), H(d.Color2), S(d.Sides), S(d.Aspect), S(d.Thick) };
            var keep = tail.Length;
            while (keep > 0 && tail[keep - 1] == plain[keep - 1]) {
                keep--;
            }

            head.AddRange(tail.Take(keep));
            return string.Join(",", head);
        });
        return Prefix3 + (emblem.Clip ? "1" : "0") + "|" + string.Join(";", layers);
    }

    private static string FormatLegacy(Legacy emblem)
    {
        if (FitsFirstForm(emblem)) {
            var old = emblem.Layers.Select(l => string.Join(",", l.Thing, S(l.X), S(l.Y), S(l.Size), S(l.Rotation8 / 2),
                S(l.Flip), S(l.Tint == OwnColours ? OldColours : l.Tint)));
            return Prefix + S(emblem.Background) + "|" + S(emblem.Shape) + "|" + string.Join(";", old);
        }

        var layers = emblem.Layers.Select(l => string.Join(",", l.Thing, S(l.X), S(l.Y), S(l.Size), S(l.Rotation8),
            S(l.Flip), S(l.Tint), S(l.Opacity), S(l.Outline)));
        return Prefix2 + S(emblem.Background) + "|" + S(emblem.Shape) + "|" + S(emblem.Pattern) + "|" +
               S(emblem.Background2) + "|" + string.Join(";", layers);
    }

    /// <summary>
    ///     The emblem with every field in range and every id typed and known (<paramref name="normalize" /> gives the
    ///     typed id or null), or null: 1..16 layers, x/y -32..96, size 10..200, rotation 0..359, flip 0..3, opacity
    ///     10..100, outline 0..6, pattern 0..7, sides 3..12, aspect 10..100, thickness 5..90, shapes of
    ///     <see cref="ShapeKinds" />. A sprite's shape fields are set to their defaults (one text per emblem)
    /// </summary>
    internal static Emblem? Validate(Emblem? emblem, Func<string, string?> normalize)
    {
        if (emblem is null || emblem.Layers.Count is < 1 or > MaxLayers) {
            return null;
        }

        var layers = new List<EmblemLayer>();
        foreach (var l in emblem.Layers) {
            if (l.X is < MinPos or > MaxPos || l.Y is < MinPos or > MaxPos || l.Size is < MinSize or > MaxSize ||
                l.Rotation is < 0 or > 359 || l.Flip is < 0 or > 3 || l.Opacity is < MinOpacity or > 100 ||
                l.Outline is < 0 or > MaxOutline || l.Pattern is < 0 or >= PatternCount || l.Sides is < 3 or > 12 ||
                l.Aspect is < 10 or > 100 || l.Thick is < 5 or > 90) {
                return null;
            }

            if (l.IsShape) {
                if (Array.IndexOf(ShapeKinds, l.Kind) < 0) {
                    return null;
                }

                layers.Add(l.Outline == 0 ? l with { OutlineColor = Ink } : l);
                continue;
            }

            if (normalize(l.Thing) is not { } typed) {
                return null;
            }

            layers.Add(l with {
                Thing = typed, Pattern = 0, Color2 = 0, Sides = 5, Aspect = 100, Thick = 30,
                OutlineColor = l.Outline == 0 ? Ink : l.OutlineColor,
            });
        }

        return emblem with { Layers = layers };
    }

    /// <summary>
    ///     What the host keeps for a leader's text: the canonical string of a valid emblem (an old single thing
    ///     upgraded on <paramref name="background" />), else null
    /// </summary>
    internal static string? Canonical(string text, int background, Func<string, string?> normalize)
    {
        if (text.Length is 0 or > MaxText) {
            return null;
        }

        return Validate(Parse(text, background), normalize) is { } emblem ? Format(emblem) : null;
    }

    /// <summary>
    ///     The team colour (0..11) an emblem gives its team: its bottom shape's fill when it is one of the twelve team
    ///     colours, else null
    /// </summary>
    internal static int? TeamColour(Emblem emblem) =>
        emblem.Layers.Count > 0 && emblem.Layers[0].IsShape && PaletteIndex(emblem.Layers[0].Color) is >= 0 and < TeamBanner.ColorCount and var i
            ? i
            : null;

    /// <summary>"#RRGGBB", or "#RRGGBBAA" when it is not opaque</summary>
    internal static string HexText(uint rgba) => "#" + ((rgba & 0xFF) == 0xFF ? (rgba >> 8).ToString("X6", CultureInfo.InvariantCulture) : H(rgba));

    /// <summary>A typed colour: "#RRGGBB" or "#RRGGBBAA", the "#" optional</summary>
    internal static bool TryHex(string text, out uint rgba)
    {
        rgba = 0;
        var t = text.Trim().TrimStart('#');
        if (t.Length is not (6 or 8) || !uint.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) {
            return false;
        }

        rgba = t.Length == 6 ? (v << 8) | 0xFFu : v;
        return true;
    }

    private static bool Hex(string s, out uint value)
    {
        value = 0;
        return s.Length == 8 && uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static bool Int(string s, out int value) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
