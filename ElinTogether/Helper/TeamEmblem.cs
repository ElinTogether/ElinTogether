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
/// <param name="Font">A text layer's font: the name of one of the game's own fonts, "" for the first</param>
/// <param name="Spacing">A text layer's letter spacing, -8..16 pixels of its raster</param>
/// <param name="Curve">A text layer bent along an arc of that many degrees, -180..180 (0 straight, above 0 an arch)</param>
/// <param name="Align">A text layer's anchor at its centre point: 0 the middle, 1 its left end, 2 its right end</param>
/// <param name="Shadow">A drop shadow, 0..4 pixels down and right</param>
/// <param name="ShadowColor">The shadow's colour, 0xRRGGBBAA</param>
/// <param name="Gradient">A shape's or a text's fill: 0 flat, 1 linear, 2 radial, from <c>Color</c> to <c>GradColor</c></param>
/// <param name="GradColor">The gradient's end colour</param>
/// <param name="GradAngle">A linear gradient's direction, degrees 0..359 (90: top to bottom)</param>
/// <param name="Blend">0 normal, 1 multiply, 2 add</param>
/// <param name="ClipBelow">1: drawn only where the layer below is</param>
/// <param name="Texture">A shape's fill tiled from a sprite (a typed sprite id), "" for none</param>
/// <param name="Name">The layer's name in the editor, up to 12 characters, "" for its default name</param>
/// <param name="Lock">1: the editor does not move or change it</param>
/// <param name="Hidden">1: not drawn</param>
/// <param name="Sym">A live mirrored copy: 1 left-right, 2 top-bottom, 3 both (four copies)</param>
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
    int Thick = 30,
    string Font = "",
    int Spacing = 0,
    int Curve = 0,
    int Align = 0,
    int Shadow = 0,
    uint ShadowColor = TeamEmblem.ShadowInk,
    int Gradient = 0,
    uint GradColor = 0xFFFFFFFFu,
    int GradAngle = 90,
    int Blend = 0,
    int ClipBelow = 0,
    string Texture = "",
    string Name = "",
    int Lock = 0,
    int Hidden = 0,
    int Sym = 0)
{
    internal bool IsShape => Thing.StartsWith("g:");

    /// <summary>A text layer: <c>x:</c> and the text's UTF-8 bytes in hex</summary>
    internal bool IsText => Thing.StartsWith("x:");

    /// <summary>A picture of the game (a thing, a monster, an icon, a tile, a portrait...)</summary>
    internal bool IsSprite => !IsShape && !IsText;

    /// <summary>A paint layer: 64x64 pixels drawn in the editor (<see cref="EmblemPaint" />), drawn as a picture</summary>
    internal bool IsPaint => EmblemPaint.Is(Thing);

    /// <summary>A text layer's text</summary>
    internal string Text => IsText ? TeamEmblem.Unhex(Thing.Substring(2)) ?? "" : "";

    /// <summary>
    ///     Only the fields the e3 form had before text layers: an emblem of such layers (and of the first sprite
    ///     kinds and shapes) is read by older games too
    /// </summary>
    internal bool Classic =>
        Font == "" && Spacing == 0 && Curve == 0 && Align == 0 && Shadow == 0 && ShadowColor == TeamEmblem.ShadowInk &&
        Gradient == 0 && GradColor == 0xFFFFFFFFu && GradAngle == 90 && Blend == 0 && ClipBelow == 0 && Texture == "" &&
        Name == "" && Lock == 0 && Hidden == 0 && Sym == 0;

    /// <summary>The shape's name ("star"), empty for a sprite</summary>
    internal string Kind => IsShape ? Thing.Substring(2) : "";
}

/// <param name="Layers">Drawn in order, the first at the bottom</param>
/// <param name="Clip">Every layer is cut to the bottom layer's outline (how every emblem before layer shapes drew)</param>
/// <param name="W">The canvas' width in pixels: 64 for a team emblem, up to 256 for a drawing (<see cref="Drawing" />)</param>
/// <param name="H">The canvas' height in pixels. A layer's place is in the canvas' pixels, its size in % of 64 pixels</param>
internal sealed record Emblem(IReadOnlyList<EmblemLayer> Layers, bool Clip = false, int W = TeamEmblem.Grid, int H = TeamEmblem.Grid);

/// <summary>
///     The text of a team emblem, kept in the team's thing id (<see cref="Net.ElinNetHost.TeamThings" />) and told to
///     every game. Three forms; the oldest one that can hold the emblem is written, so games of an older version still
///     draw it:
///     <c>e1|bg|shape|L;L;...</c>, L = <c>kind:id,x,y,s,q,f,c</c> (q quarter turns, c 0..11 or 12 own colours);
///     <c>e2|bg|shape|pattern|bg2|L;L;...</c>, L = <c>kind:id,x,y,s,r,f,c,o,e</c> (r eighths of a turn, c 0..23 or 24
///     own colours, o opacity %, e outline 0/1): both a background shape of the palette, cut to it, under sprites;
///     <c>e3|clip|L;L;...</c>, L = <c>kind:id,x,y,s,r,f,RRGGBBAA[,o,w,RRGGBBAA,p,RRGGBBAA,n,a,t]</c> (r degrees, the
///     fields after the colour left out when they are the defaults). An e1/e2 text reads as e3 with its background as
///     the bottom shape layer and clip on (<see cref="FromLegacy" />), and draws exactly as before.
///     e3 grew by appending only (lead dev 2026-11-01): text layers (<c>x:</c> + hex), more sprite sources
///     (<see cref="TeamBanner.Normalize" />) and the fields after <c>t</c> (font, spacing, curve, align, shadow and its
///     colour, gradient, its colour and angle, blend, clip-to-below, texture, name in hex, lock, hidden, symmetry).
///     An emblem that uses none of them writes exactly the text it wrote before; one that does is refused by an older
///     game's reader, which then draws the team's fallback mark
/// </summary>
internal static class TeamEmblem
{
    internal const string Prefix = "e1|";
    internal const string Prefix2 = "e2|";
    internal const string Prefix3 = "e3|";

    /// <summary>The longest emblem text the host keeps (the editor says so before Save)</summary>
    internal const int MaxText = 4000;

    /// <summary>A text layer's characters, a layer name's characters</summary>
    internal const int MaxChars = 16;

    internal const int MaxName = 12;

    /// <summary>The default shadow: black at half alpha</summary>
    internal const uint ShadowInk = 0x00000080u;

    internal const int MaxShadow = 4;
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
        if (!e.Clip || e.Layers.Count is < 1 or > SecondLayers + 1 || e.Layers.Any(l => !l.Classic)) {
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
            if (!Sprite(l.Thing) || tint < 0 || l.X is < 0 or > Grid || l.Y is < 0 or > Grid || l.Rotation % 45 != 0 ||
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

    // the e3 fields after the typed id, in order: i an int, h a colour in hex, s a plain word, n a name in hex
    private const string Kinds = "iiiiihiihihiiisiiiihihiiisniii";

    // the fields always written (x, y, size, rotation, flip, colour); the others only up to the last one not at its default
    private const int Head = 6;

    private static readonly string[] Plain3 = Fields(new("", 0, 0, 0, 0, 0, 0));

    private static string[] Fields(EmblemLayer l) => [
        S(l.X), S(l.Y), S(l.Size), S(l.Rotation), S(l.Flip), H(l.Color),
        S(l.Opacity), S(l.Outline), H(l.OutlineColor), S(l.Pattern), H(l.Color2), S(l.Sides), S(l.Aspect), S(l.Thick),
        l.Font, S(l.Spacing), S(l.Curve), S(l.Align), S(l.Shadow), H(l.ShadowColor), S(l.Gradient), H(l.GradColor), S(l.GradAngle),
        S(l.Blend), S(l.ClipBelow), l.Texture, Hex(l.Name), S(l.Lock), S(l.Hidden), S(l.Sym),
    ];

    // a typed id of the e3 form: a sprite of a known source, a shape or a text (the ids themselves are checked later)
    private static bool Typed(string thing) =>
        thing.Length >= 3 && thing[1] == ':' && (thing[0] is 'g' or 'x' or 'd' || EmblemSprites.Codes.IndexOf(thing[0]) >= 0);

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
                if (f.Length < Head + 1 || f.Length > Kinds.Length + 1 || !Typed(f[0])) {
                    return null;
                }

                // the fields given, the defaults after them
                var v = new string[Kinds.Length];
                for (var i = 0; i < v.Length; i++) {
                    v[i] = i + 1 < f.Length ? f[i + 1] : Plain3[i];
                }

                var ints = new int[v.Length];
                var hexes = new uint[v.Length];
                for (var i = 0; i < v.Length; i++) {
                    switch (Kinds[i]) {
                        case 'i' when Int(v[i], out ints[i]):
                        case 'h' when Hex(v[i], out hexes[i]):
                        case 's' when v[i].Length <= 40:
                            continue;
                        case 'n' when v[i].Length <= MaxName * 8 && Unhex(v[i]) != null:
                            continue;
                        default:
                            return null;
                    }
                }

                layers.Add(new(f[0], ints[0], ints[1], ints[2], ints[3], ints[4], hexes[5], ints[6], ints[7], hexes[8], ints[9], hexes[10],
                    ints[11], ints[12], ints[13], v[14], ints[15], ints[16], ints[17], ints[18], hexes[19], ints[20], hexes[21], ints[22],
                    ints[23], ints[24], v[25], Unhex(v[26])!, ints[27], ints[28], ints[29]));
            }
        }

        return new(layers, clip == 1);
    }

    private static string S(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string H(uint rgba) => rgba.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>A text as the hex of its UTF-8 bytes (no separator of the emblem text can be in it)</summary>
    internal static string Hex(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>The text of <see cref="Hex(string)" />, null when it is not one</summary>
    internal static string? Unhex(string hex)
    {
        if (hex.Length % 2 != 0) {
            return null;
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++) {
            if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i])) {
                return null;
            }
        }

        try {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        } catch (ArgumentException) {
            return null;
        }
    }

    /// <summary>A text layer of that text, centred, in that colour</summary>
    internal static EmblemLayer TextLayer(string text, uint colour) =>
        new("x:" + Hex(text), Grid / 2, Grid / 2, 80, 0, 0, colour, Outline: 1);

    /// <summary>The canonical text of an emblem (what the host stores and tells), in the oldest form that holds it</summary>
    internal static string Format(Emblem emblem)
    {
        if (ToLegacy(emblem) is { } legacy) {
            return FormatLegacy(legacy);
        }

        var layers = emblem.Layers.Select(l => {
            var fields = Fields(l);
            var keep = fields.Length;
            while (keep > Head && fields[keep - 1] == Plain3[keep - 1]) {
                keep--;
            }

            return l.Thing + "," + string.Join(",", fields.Take(keep));
        });
        return Prefix3 + (emblem.Clip ? "1" : "0") + "|" + string.Join(";", layers);
    }

    /// <summary>The layers of an emblem or a drawing in the e3 form (a drawing's text holds them after its own fields)</summary>
    internal static string LayerText(Emblem emblem) =>
        string.Join(";", emblem.Layers.Select(l => {
            var fields = Fields(l);
            var keep = fields.Length;
            while (keep > Head && fields[keep - 1] == Plain3[keep - 1]) {
                keep--;
            }

            return l.Thing + "," + string.Join(",", fields.Take(keep));
        }));

    /// <summary>The layers of <see cref="LayerText" /> read back (not validated), null when they do not read</summary>
    internal static Emblem? ParseLayers(string layers, bool clip) => ParseThird(Prefix3 + (clip ? "1" : "0") + "|" + layers);

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
    ///     <see cref="ShapeKinds" />; a text of 1..16 characters, a name of 12 at most, a font name of plain letters,
    ///     spacing -8..16, curve -180..180, align 0..2, shadow 0..4, gradient 0..2 at 0..359 degrees, blend 0..2,
    ///     clip-to-below, lock and hidden 0..1, symmetry 0..3, a texture of a known sprite. The fields that mean
    ///     nothing for the layer's kind are set to their defaults (one text per emblem)
    /// </summary>
    internal static Emblem? Validate(Emblem? emblem, Func<string, string?> normalize)
    {
        if (emblem is null || emblem.Layers.Count is < 1 or > MaxLayers) {
            return null;
        }

        // (the canvas: 64 for an emblem; a drawing's places reach half its size past its edges, its sizes as far)
        if (emblem.W is < Drawing.MinSide or > Drawing.MaxSide || emblem.H is < Drawing.MinSide or > Drawing.MaxSide) {
            return null;
        }

        int minX = -emblem.W / 2, maxX = emblem.W + emblem.W / 2, minY = -emblem.H / 2, maxY = emblem.H + emblem.H / 2;
        var maxSize = Math.Max(MaxSize, MaxSize * Math.Max(emblem.W, emblem.H) / Grid);
        var layers = new List<EmblemLayer>();
        var d = new EmblemLayer("", 0, 0, 0, 0, 0, 0);
        foreach (var raw in emblem.Layers) {
            var l = raw;
            if (l.X < minX || l.X > maxX || l.Y < minY || l.Y > maxY || l.Size < MinSize || l.Size > maxSize ||
                l.Rotation is < 0 or > 359 || l.Flip is < 0 or > 3 || l.Opacity is < MinOpacity or > 100 ||
                l.Outline is < 0 or > MaxOutline || l.Pattern is < 0 or >= PatternCount || l.Sides is < 3 or > 12 ||
                l.Aspect is < 10 or > 100 || l.Thick is < 5 or > 90 || l.Spacing is < -8 or > 16 || l.Curve is < -180 or > 180 ||
                l.Align is < 0 or > 2 || l.Shadow is < 0 or > MaxShadow || l.Gradient is < 0 or > 2 || l.GradAngle is < 0 or > 359 ||
                l.Blend is < 0 or > 2 || l.ClipBelow is < 0 or > 1 || l.Lock is < 0 or > 1 || l.Hidden is < 0 or > 1 ||
                l.Sym is < 0 or > 3 || !PlainText(l.Name, MaxName, true) || !FontName(l.Font)) {
                return null;
            }

            // (the defaults of the fields left unused, so the same look has one text)
            if (l.Outline == 0) {
                l = l with { OutlineColor = Ink };
            }

            if (l.Shadow == 0) {
                l = l with { ShadowColor = d.ShadowColor };
            }

            if (l.Gradient == 0) {
                l = l with { GradColor = d.GradColor, GradAngle = d.GradAngle };
            }

            if (l.IsShape) {
                if (Array.IndexOf(ShapeKinds, l.Kind) < 0) {
                    return null;
                }

                if (l.Texture.Length > 0) {
                    if (normalize(l.Texture) is not { } tex || tex[0] is 'g' or 'x') {
                        return null;
                    }

                    l = l with { Texture = tex };
                }

                layers.Add(l with { Font = "", Spacing = 0, Curve = 0, Align = 0 });
                continue;
            }

            if (l.IsText) {
                if (!PlainText(l.Text, MaxChars, false)) {
                    return null;
                }

                // (one hex per text: upper case, as Hex writes it)
                layers.Add(l with { Thing = "x:" + Hex(l.Text), Pattern = 0, Color2 = 0, Sides = 5, Thick = 30, Texture = "" });
                continue;
            }

            // (a paint layer: its pixels must read; it is a picture for every other field)
            var typed = l.IsPaint ? EmblemPaint.Valid(l.Thing, Math.Max(emblem.W, emblem.H)) ? l.Thing : null : normalize(l.Thing);
            if (typed == null) {
                return null;
            }

            layers.Add(l with {
                Thing = typed, Pattern = 0, Color2 = 0, Sides = 5, Aspect = 100, Thick = 30, Font = "", Spacing = 0, Curve = 0,
                Align = 0, Gradient = 0, GradColor = d.GradColor, GradAngle = d.GradAngle, Texture = "",
            });
        }

        return emblem with { Layers = layers };
    }

    /// <summary>A text of 1..<paramref name="max" /> characters (none when <paramref name="empty" />), no control character</summary>
    private static bool PlainText(string? text, int max, bool empty) =>
        text != null && text.Length <= max && (empty || text.Length > 0) && !text.Any(char.IsControl);

    /// <summary>A font's name: letters, digits, spaces, "_", "-" and "." (the text's separators can never be in it)</summary>
    private static bool FontName(string? name) =>
        name != null && name.Length <= 40 && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '_' or '-' or '.');

    /// <summary>
    ///     What the host keeps for a leader's text: the canonical string of a valid emblem (an old single thing
    ///     upgraded on <paramref name="background" />), else null. The editor checks <see cref="MaxText" /> before Save
    /// </summary>
    internal static string? Canonical(string text, int background, Func<string, string?> normalize)
    {
        if (text.Length is 0 or > MaxText) {
            return null;
        }

        return Validate(Parse(text, background), normalize) is { } emblem && Format(emblem) is { Length: <= MaxText } canonical ? canonical : null;
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
