using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The game's pictures an emblem layer may use (lead dev 2026-11-01: more kinds of the game's sprites), by a one
///     letter source and an id: <c>t:</c> things, <c>c:</c> monsters, <c>e:</c> element icons (attributes,
///     resistances, feats), <c>a:</c> skill and spell icons, <c>r:</c> gods' symbols, <c>u:</c> and <c>v:</c> the
///     game's UI icons (48 and 32), <c>o:</c> map objects, <c>b:</c> walls and blocks, <c>f:</c> floors, <c>p:</c>
///     portraits, <c>z:</c> the pictures of places. Only what every copy of the game has: the base game's resources
///     and source rows (a portrait from a mod's folder is not offered). An id this game does not know reads null; the
///     baker then draws a neutral placeholder
/// </summary>
internal static class EmblemSprites
{
    /// <summary>The source letters of the pictures (the e3 reader takes these besides g: shapes and x: texts)</summary>
    internal const string Codes = "tceaurvobfpz";

    private const string ElementSheet = "Media/Graphics/Icon/Element/icon_elements";
    private const string AbilitySheet = "Media/Graphics/Icon/Element/icon_ability";
    private const string Icons48 = "Media/Graphics/Icon/icons_48";
    private const string Icons32 = "Media/Graphics/Icon/icons_32";
    private const string FactionPath = "Media/Graphics/Image/Faction/";
    private const string ZonePath = "Media/Graphics/Image/Zone/";

    /// <summary>The grid's sources, one pill each: the letters it lists, the pill's text id</summary>
    internal static readonly (string Codes, string Label)[] Pills = [
        ("t", "emp_ui_team_banner_items"),
        ("c", "emp_ui_team_banner_monsters"),
        ("ea", "emp_ui_emblem_src_icons"),
        ("r", "emp_ui_emblem_src_gods"),
        ("uv", "emp_ui_emblem_src_ui"),
        ("obf", "emp_ui_emblem_src_tiles"),
        ("p", "emp_ui_emblem_src_portraits"),
        ("z", "emp_ui_emblem_src_places"),
    ];

    /// <summary>
    ///     The editor's sticker grid's sources (lead dev 2026-11-02: some categories were useless): the gods' few
    ///     symbols with the icons, the map objects (trees, rocks, furniture) on their own; no floors nor walls (texture
    ///     strips, still offered as a shape's texture). The list is sieved (<see cref="Components.StickerSieve" />)
    /// </summary>
    internal static readonly (string Codes, string Label)[] StickerPills = [
        ("t", "emp_ui_team_banner_items"),
        ("c", "emp_ui_team_banner_monsters"),
        ("ear", "emp_ui_emblem_src_icons"),
        ("uv", "emp_ui_emblem_src_ui"),
        ("o", "emp_ui_emblem_src_scenery"),
        ("p", "emp_ui_emblem_src_portraits"),
        ("z", "emp_ui_emblem_src_places"),
    ];

    private static HashSet<string>? _zoneImages;

    private static string? Sheet(char code) => code switch {
        'e' => ElementSheet,
        'a' => AbilitySheet,
        'u' => Icons48,
        'v' => Icons32,
        _ => null,
    };

    /// <summary>An id has none of the emblem text's separators</summary>
    private static bool Clean(string id) => id.Length is > 0 and <= 40 && id.IndexOfAny([',', ';', '|']) < 0;

    /// <summary>
    ///     The typed form of an id: "t:sword" (an old untyped value is a thing); null when this game has no such
    ///     picture
    /// </summary>
    internal static string? Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) {
            return null;
        }

        if (value.Length < 3 || value[1] != ':' || Codes.IndexOf(value[0]) < 0) {
            // (an old untyped value is a thing)
            return value.IndexOf(':') < 0 && Clean(value) && EClass.sources.things.map.ContainsKey(value) ? "t:" + value : null;
        }

        var (code, id) = (value[0], value.Substring(2));
        if (!Clean(id)) {
            return null;
        }

        try {
            var known = code switch {
                't' => EClass.sources.things.map.ContainsKey(id),
                'c' => EClass.sources.charas.map.ContainsKey(id),
                'e' or 'a' or 'u' or 'v' => SpriteSheet.Get(Sheet(code)!, id) != null,
                'r' => EClass.sources.religions.map.ContainsKey(id),
                'o' => int.TryParse(id, out var o) && EClass.sources.objs.map.ContainsKey(o),
                'b' => int.TryParse(id, out var b) && EClass.sources.blocks.map.ContainsKey(b),
                'f' => int.TryParse(id, out var f) && EClass.sources.floors.map.ContainsKey(f),
                'p' => BasePortrait(id) != null,
                'z' => ZoneImages().Contains(id),
                _ => false,
            };
            return known ? code + ":" + id : null;
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Team emblem: {Id} could not be looked up", value);
            return null;
        }
    }

    /// <summary>
    ///     A typed id as a receiver keeps it: known, or well formed though this game lacks it (a mod's thing of the
    ///     host's): drawn as a placeholder, never dropped nor crashing
    /// </summary>
    internal static string? NormalizeOrKeep(string value) =>
        Normalize(value) ?? (value.Length >= 3 && value[1] == ':' && Codes.IndexOf(value[0]) >= 0 && Clean(value.Substring(2)) ? value : null);

    /// <summary>The picture of a typed id, null when this game has none</summary>
    internal static Sprite? SpriteOf(string typed)
    {
        if (Normalize(typed) is not { } value) {
            return null;
        }

        var (code, id) = (value[0], value.Substring(2));
        try {
            return code switch {
                't' => EClass.sources.things.map[id].GetSprite(),
                'c' => EClass.sources.charas.map[id].GetSprite(),
                'e' or 'a' or 'u' or 'v' => SpriteSheet.Get(Sheet(code)!, id),
                'r' => Resources.Load<Sprite>(FactionPath + id),
                'o' => EClass.sources.objs.map[int.Parse(id)].GetSprite(),
                'b' => EClass.sources.blocks.map[int.Parse(id)].GetSprite(),
                'f' => EClass.sources.floors.map[int.Parse(id)].GetSprite(),
                'p' => BasePortrait(id)?.GetObject(),
                'z' => Resources.Load<Sprite>(ZonePath + id),
                _ => null,
            };
        } catch (System.Exception e) {
            EmpLog.Debug(e, "Team emblem: the picture of {Id} could not be read", typed);
            return null;
        }
    }

    /// <summary>A layer's picture or text by name: the game's name in the player's language where it has one, else the id</summary>
    internal static string NameOf(string typed)
    {
        if (typed.Length < 3 || typed[1] != ':') {
            return typed;
        }

        var id = typed.Substring(2);
        try {
            switch (typed[0]) {
                case 'x':
                    return "\"" + (TeamEmblem.Unhex(id) ?? "") + "\"";
                case 'd':
                    return "emp_ui_emblem_paint".lang();
                case 't':
                    return EClass.sources.things.map.TryGetValue(id, out var thing) ? thing.GetName() ?? id : id;
                case 'c':
                    return EClass.sources.charas.map.TryGetValue(id, out var chara) ? chara.GetName() ?? id : id;
                case 'e':
                case 'a': {
                    var alias = typed[0] == 'e' && id.StartsWith("ele_") ? id.Substring(4) : id;
                    return EClass.sources.elements.alias.TryGetValue(alias, out var e) && e.GetName() is { Length: > 0 } n ? n : id;
                }
                case 'r':
                    return EClass.sources.religions.map.TryGetValue(id, out var god) && god.GetName() is { Length: > 0 } g ? g : id;
                case 'o' when int.TryParse(id, out var o) && EClass.sources.objs.map.TryGetValue(o, out var obj):
                    return obj.GetName() is { Length: > 0 } on ? on : id;
                case 'b' when int.TryParse(id, out var b) && EClass.sources.blocks.map.TryGetValue(b, out var block):
                    return block.GetName() is { Length: > 0 } bn ? bn : id;
                case 'f' when int.TryParse(id, out var f) && EClass.sources.floors.map.TryGetValue(f, out var floor):
                    return floor.GetName() is { Length: > 0 } fn ? fn : id;
                // (lead dev 2026-11-02: a layer list of "default", "c_f-1", "0" said nothing)
                case 'z':
                    // (a zone row without a name of its own reads as a number: the place's picture id then)
                    return EClass.sources.zones.rows.FirstOrDefault(z => z.image == id)?.GetName() is { Length: > 0 } zn && !zn.All(char.IsDigit)
                        ? zn
                        : "emp_ui_emblem_name_place".Loc(id);
                case 'p':
                    return "emp_ui_emblem_name_portrait".Loc(id);
                case 'u':
                case 'v':
                    return "emp_ui_emblem_name_icon".Loc(id);
                default:
                    return id;
            }
        } catch (System.Exception) {
            return id;
        }
    }

    /// <summary>A portrait of the base game (its file in the game's own _Elona package), not a mod's</summary>
    private static ModItem<Sprite>? BasePortrait(string id)
    {
        if (!Portrait.modPortraits.dict.TryGetValue(id, out var item) || item.fileInfo == null) {
            return null;
        }

        var path = item.fileInfo.FullName.Replace('\\', '/');
        return path.Contains("/Package/_Elona/") ? item : null;
    }

    /// <summary>The pixels at most <paramref name="max" /> on their longer side (nearest, rows as given); themselves when they fit</summary>
    internal static (Color32[] Pixels, int W, int H) Fit(Color32[] px, int w, int h, int max)
    {
        if (w <= max && h <= max) {
            return (px, w, h);
        }

        var scale = (float)max / Mathf.Max(w, h);
        int nw = Mathf.Max(1, Mathf.FloorToInt(w * scale)), nh = Mathf.Max(1, Mathf.FloorToInt(h * scale));
        var output = new Color32[nw * nh];
        for (var y = 0; y < nh; y++) {
            var sy = Mathf.Min(h - 1, y * h / nh);
            for (var x = 0; x < nw; x++) {
                output[y * nw + x] = px[sy * w + Mathf.Min(w - 1, x * w / nw)];
            }
        }

        return (output, nw, nh);
    }

    private static HashSet<string> ZoneImages() =>
        _zoneImages ??= [..EClass.sources.zones.rows.Select(z => z.image).Where(i => !string.IsNullOrEmpty(i) && Clean(i))];

    // ---------------------------------------------------------------- the grid's lists

    /// <summary>
    ///     The entries of the grid for a pill's letters (things and monsters as before: those whose sprite reads; the
    ///     others read their picture only when scrolled into view)
    /// </summary>
    internal static IEnumerable<Components.SpriteGrid.Entry> List(char code)
    {
        switch (code) {
            case 'e':
            case 'a':
            case 'u':
            case 'v': {
                var sheet = Sheet(code)!;
                SpriteSheet.Get(sheet, "");
                foreach (var sprite in Resources.LoadAll<Sprite>(sheet).Where(s => s != null && Clean(s.name)).GroupBy(s => s.name).Select(g => g.First())) {
                    var name = sprite.name;
                    var alias = code == 'e' && name.StartsWith("ele_") ? name.Substring(4) : name;
                    var shown = code is 'e' or 'a' && EClass.sources.elements.alias.TryGetValue(alias, out var row) ? row.GetName() : name;
                    yield return Entry(code + ":" + name, shown, name, () => SpriteSheet.Get(sheet, name));
                }

                break;
            }
            case 'r':
                foreach (var row in EClass.sources.religions.rows.Where(r => Clean(r.id ?? ""))) {
                    var id = row.id;
                    if (Resources.Load<Sprite>(FactionPath + id) is { } sprite) {
                        yield return Entry("r:" + id, row.GetName(), id, () => sprite);
                    }
                }

                break;
            case 'o':
                foreach (var row in EClass.sources.objs.rows) {
                    yield return Tile("o:", row);
                }

                break;
            case 'b':
                foreach (var row in EClass.sources.blocks.rows) {
                    yield return Tile("b:", row);
                }

                break;
            case 'f':
                foreach (var row in EClass.sources.floors.rows) {
                    yield return Tile("f:", row);
                }

                break;
            case 'p':
                foreach (var item in Portrait.modPortraits.list.Where(i => i.id != null && Clean(i.id) && BasePortrait(i.id) == i)) {
                    var it = item;
                    yield return Entry("p:" + it.id, it.id, it.id, () => it.GetObject());
                }

                break;
            case 'z':
                foreach (var image in ZoneImages().OrderBy(i => i)) {
                    var zone = EClass.sources.zones.rows.First(z => z.image == image);
                    var id = image;
                    yield return Entry("z:" + id, zone.GetName() is { Length: > 0 } n ? n : id, id, () => Resources.Load<Sprite>(ZonePath + id));
                }

                break;
        }
    }

    private static Components.SpriteGrid.Entry Tile(string type, TileRow row)
    {
        var r = row;
        return Entry(type + r.id, r.GetName() is { Length: > 0 } n ? n : r.alias ?? r.id.ToString(), r.alias ?? "", () => r.GetSprite());
    }

    private static Components.SpriteGrid.Entry Entry(string value, string? name, string extra, System.Func<Sprite?> get)
    {
        var shown = string.IsNullOrEmpty(name) ? value.Substring(2) : name!;
        return new(value, shown, (value.Substring(2) + "\n" + shown + "\n" + extra).ToLowerInvariant(), null, get);
    }
}
