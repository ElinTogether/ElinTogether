using System.Collections.Generic;
using System.Linq;
using ElinTogether.Components;
using ElinTogether.Models;
using ElinTogether.Net;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Helper;

/// <summary>
///     Host box "Team banners" (PLAN_hub_features C): each team has an emblem (one of the game's element icons) and a
///     colour (one of 12), chosen by its leader and shown on the Teams page. The host keeps them
///     (<see cref="ElinNetHost.TeamBanners" />) and tells every game with the teams (<see cref="TeamsDelta.Banners" />)
/// </summary>
internal static class TeamBanner
{
    private const string Sheet = "Media/Graphics/Icon/Element/icon_elements";

    /// <summary>
    ///     The emblems offered: aliases of the game's elements, drawn from the sprite "ele_" + alias of its element sheet.
    ///     A name the game has no sprite for is left out at runtime (<see cref="Emblems" />)
    /// </summary>
    private static readonly string[] Aliases = [
        "STR", "END", "DEX", "PER", "LER", "WIL", "MAG", "CHA", "SPD", "LUC",
        "eleFire", "eleCold", "eleLightning", "eleDarkness", "eleMind", "elePoison", "eleNether", "eleSound",
        "eleNerve", "eleHoly", "eleChaos", "eleMagic", "eleImpact", "eleCut", "eleAcid", "eleEther",
    ];

    /// <summary>
    ///     12 colours that read on a dark page (the colour-blind safe set of <see cref="PeerColorizer" /> first)
    /// </summary>
    internal static readonly int[] Palette = [
        0x0072b2, 0xe69f00, 0xcc79a7, 0x009e73, 0xd55e00, 0x56b4e9,
        0xf0e442, 0x9a6fe0, 0xe0554f, 0x8fbf3f, 0x4fd1c5, 0xb0b0b0,
        // the emblem's further twelve (heraldry's deep and pale tinctures): never a team's colour
        0x1f3a93, 0x8b1a1a, 0x2e6b2e, 0x7a4a21, 0x202020, 0xf2f2f2,
        0xd4af37, 0x5b2a86, 0xf4a6b8, 0x1d6f6f, 0xa0522d, 0x606060,
    ];

    /// <summary>The team colours (chips, the banner colour): the first twelve of the palette</summary>
    internal const int ColorCount = 12;

    /// <summary>The emblem's colours: the whole palette</summary>
    internal const int EmblemColorCount = TeamEmblem.Colours;

    // a guest's copy of the host's banners
    private static Dictionary<int, (int Icon, int Color)> _told = [];
    private static Dictionary<int, string> _toldThings = [];

    private static List<(int Id, Sprite Sprite)>? _emblems;

    internal static bool On => NetSession.Instance is { InGame: true } net && EClass.pc != null &&
                               (net.Transport is ElinNetHost
                                   ? EmpConfig.Server.TeamBanner.Value && EmpConfig.Server.Teams.Value
                                   : net.Rules is { UseTeams: true, TeamBanner: true });

    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     The emblems the game really has a sprite for (id = index in <see cref="Aliases" />, kept stable)
    /// </summary>
    internal static List<(int Id, Sprite Sprite)> Emblems
    {
        get
        {
            if (_emblems is { Count: > 0 }) {
                return _emblems;
            }

            var list = new List<(int, Sprite)>();
            for (var i = 0; i < Aliases.Length; i++) {
                if (SpriteSheet.Get(Sheet, "ele_" + Aliases[i]) is { } sprite) {
                    list.Add((i, sprite));
                }
            }

            return _emblems = list;
        }
    }

    internal static Color ColorOf(int index) => ToColor(Palette[Mathf.Clamp(index, 0, Palette.Length - 1)]);

    /// <summary>
    ///     The colour of a team's chip: its banner colour, else a stable one from the team id in the same palette
    /// </summary>
    internal static Color ChipColor(int teamId) => Of(teamId) is { } banner ? ColorOf(banner.Color) : ColorOf(((teamId % ColorCount) + ColorCount) % ColorCount);

    private static Color ToColor(int rgb) => new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);

    private static Dictionary<int, (int Icon, int Color)> Book =>
        KeepsBook ? ElinNetHost.TeamBanners.Where(b => b.Value is { Length: 2 }).ToDictionary(b => b.Key, b => (b.Value[0], b.Value[1])) : _told;

    private static Dictionary<int, string> Things => KeepsBook ? ElinNetHost.TeamThings : _toldThings;

    /// <summary>
    ///     The team's emblem as its canonical text (<see cref="TeamEmblem" />), an old single-thing value
    ///     upgraded on the banner colour. Null when the team has none (else the old element emblem draws)
    /// </summary>
    internal static string? ThingOf(int teamId) =>
        On && Things.TryGetValue(teamId, out var stored) && EmblemOf(teamId, stored) is { } emblem ? TeamEmblem.Format(emblem) : null;

    /// <summary>The team's emblem, read from its stored text (ids not known to this game are left out)</summary>
    internal static Emblem? EmblemOf(int teamId) =>
        On && Things.TryGetValue(teamId, out var stored) ? EmblemOf(teamId, stored) : null;

    private static Emblem? EmblemOf(int teamId, string stored)
    {
        var bg = Book.TryGetValue(teamId, out var banner) ? banner.Color : 0;
        if (TeamEmblem.Parse(stored, bg) is not { } emblem) {
            return null;
        }

        // (a guest without a mod's thing still draws the rest)
        var layers = emblem.Layers.Where(l => l.IsShape || Normalize(l.Thing) != null).ToList();
        return layers.Count == 0 ? null : emblem with { Layers = layers };
    }

    /// <summary>
    ///     The typed form of a stored emblem id: "t:sword" (a thing) or "c:putty" (a monster). An old untyped value is a
    ///     thing. Null when the game has no such source row
    /// </summary>
    internal static string? Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) {
            return null;
        }

        var monster = value.StartsWith("c:");
        var id = value.StartsWith("t:") || monster ? value.Substring(2) : value;
        if (monster) {
            return EClass.sources.charas.map.ContainsKey(id) ? "c:" + id : null;
        }

        return EClass.sources.things.map.ContainsKey(id) ? "t:" + id : null;
    }

    /// <summary>
    ///     The sprite of a typed emblem id: the thing's, or the monster's own pixel sprite
    /// </summary>
    internal static Sprite? SpriteOf(string typed)
    {
        if (Normalize(typed) is not { } value) {
            return null;
        }

        var id = value.Substring(2);
        return value[0] == 'c' ? EClass.sources.charas.map[id].GetSprite() : EClass.sources.things.map[id].GetSprite();
    }

    internal static (int Icon, int Color)? Of(int teamId) => On && Book.TryGetValue(teamId, out var banner) ? banner : null;

    /// <summary>
    ///     In the game that keeps the book: this leader sets the banner of its team. False when the rule is off, the asker
    ///     leads no team or the emblem or colour is not one offered. A player alone founds a team of one to have an
    ///     emblem (user 2026-10-30: emblems are for teams only)
    /// </summary>
    internal static bool Set(Chara chara, int icon, int color, string thing = "")
    {
        var team = Teams.TeamOf(chara.uid);
        if (!KeepsBook || !On || team == Teams.Wanderers || !TeamRanks.Can(chara.uid, TeamPerm.Emblem, team)) {
            return false;
        }

        if (color is < 0 or >= ColorCount) {
            return false;
        }

        if (Throttled(chara.uid)) {
            EmpLog.Debug("Team {TeamId} banner: too many changes, refused", team);
            return false;
        }

        if (thing.Length > 0) {
            // an emblem of up to 16 layers, things or monsters of the game or shapes (an old single id is upgraded to one layer); any
            // text that does not read, a field out of range or an id the game does not know is refused
            if (TeamEmblem.Canonical(thing, color, Normalize) is not { } canonical) {
                return false;
            }

            // (the team's colour is the emblem's bottom shape's when it is one of the twelve team colours, else it stays)
            var had = ElinNetHost.TeamBanners.TryGetValue(team, out var old) && old.Length == 2;
            var teamColour = TeamEmblem.TeamColour(TeamEmblem.Parse(canonical)!) ?? (had ? old![1] : color);
            ElinNetHost.TeamThings[team] = canonical;
            ElinNetHost.TeamBanners[team] = [had ? old![0] : 0, teamColour];
            EmpLog.Information("Team {TeamId} banner: emblem {Emblem}", team, canonical);
            return true;
        }

        if (Emblems.All(e => e.Id != icon)) {
            return false;
        }

        ElinNetHost.TeamThings.Remove(team);
        ElinNetHost.TeamBanners[team] = [icon, color];
        EmpLog.Information("Team {TeamId} banner: emblem {Icon}, colour {Color}", team, icon, color);
        return true;
    }

    // leader uid -> the times of its last banner asks (host only): 1 per 2 s and 20 per minute
    private static readonly Dictionary<int, Queue<float>> _asks = [];

    private static bool Throttled(int uid)
    {
        var now = Time.realtimeSinceStartup;
        if (!_asks.TryGetValue(uid, out var times)) {
            _asks[uid] = times = new();
        }

        while (times.Count > 0 && now - times.Peek() > 60f) {
            times.Dequeue();
        }

        if (times.Count >= 20 || (times.Count > 0 && now - times.Last() < 2f)) {
            return true;
        }

        times.Enqueue(now);
        return false;
    }

    /// <summary>The team's baked emblem sprite, for small marks such as the players' team chip; null when none</summary>
    internal static Sprite? EmblemSprite(int teamId) => EmblemOf(teamId) is { } emblem ? TeamEmblemBaker.Get(emblem) : null;

    internal static void Remove(int teamId)
    {
        ElinNetHost.TeamBanners.Remove(teamId);
        ElinNetHost.TeamThings.Remove(teamId);
    }

    internal static Dictionary<int, string>? ToldThings() => EmpConfig.Server.TeamBanner.Value ? new(ElinNetHost.TeamThings) : null;

    /// <summary>
    ///     What the host tells with the teams: none while the box is off
    /// </summary>
    internal static Dictionary<int, (int Icon, int Color)>? Told()
    {
        return EmpConfig.Server.TeamBanner.Value
            ? ElinNetHost.TeamBanners.Where(b => b.Value is { Length: 2 }).ToDictionary(b => b.Key, b => (b.Value[0], b.Value[1]))
            : null;
    }

    internal static void Learn(Dictionary<int, (int Icon, int Color)>? banners, Dictionary<int, string>? things = null)
    {
        _told = new(banners ?? []);
        _toldThings = new(things ?? []);
    }

    internal static void Forget()
    {
        _told = [];
        _toldThings = [];
        _asks.Clear();
        TeamEmblemBaker.Clear();
    }

    /// <summary>
    ///     The team's banner in a row: its emblem tinted with its colour, then a chip of that colour. Nothing when the
    ///     box is off or the team has none
    /// </summary>
    internal static void Draw(YKLayout row, int teamId, float size = 48f)
    {
        if (Of(teamId) is not { } banner) {
            // (a team that made no emblem yet: a square of its colour, so every team has its mark, user 2026-10-30)
            if (On && teamId != Teams.Wanderers) {
                // (the swatch alone: Frame with no sprite drew the game's 100 px white image over the header)
                Swatch(row, ChipColor(teamId), size, size, true, null);
            }

            return;
        }

        var color = ColorOf(banner.Color);
        if (EmblemOf(teamId) is { } emblem) {
            // the baked emblem (background, shape and layers in one sprite shared by every view)
            // (a click shows it large, with the team's name)
            EmblemZoom.Zoomable(TeamEmblemBaker.Place(row.transform, TeamEmblemBaker.Get(emblem), size), teamId);
        } else if (Emblems.FirstOrDefault(e => e.Id == banner.Icon).Sprite is { } sprite) {
            // the emblem on a square of the colour, like an item (no detached colour bar beside it)
            Frame(row, color, sprite, size, true, null);
        }
    }

    /// <summary>
    ///     A thing's sprite on a square of the team colour (the colour is the frame, the item keeps its own colours)
    /// </summary>
    internal static void Frame(YKLayout row, Color color, Sprite? sprite, float size, bool selected, System.Action? click)
    {
        Swatch(row, selected ? color : new(color.r, color.g, color.b, 0.35f), size, size, selected, click);
        var back = row.transform.GetChild(row.transform.childCount - 1);
        var go = new GameObject("Thing", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(back, false);
        var rt = (RectTransform)go.transform;
        // the sprite at its native aspect filling ~80 % of the square, centred, pixel art kept (point filtering)
        rt.anchorMin = rt.anchorMax = rt.pivot = new(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        var image = go.GetComponent<Image>();
        // the game's sprites carry wide transparent margins: only the drawn part fills the whole square
        sprite = Cropped(sprite);
        image.sprite = sprite;
        image.preserveAspect = true;
        if (sprite != null) {
            var scale = size / Mathf.Max(sprite.rect.width, sprite.rect.height, 1f);
            rt.sizeDelta = new(sprite.rect.width * scale, sprite.rect.height * scale);
        }

        image.raycastTarget = false;
    }

    private static readonly Dictionary<Sprite, Sprite> _cropped = [];

    /// <summary>The sprite cut to its opaque pixels, point filtered (cached per sprite); itself when it cannot be read</summary>
    internal static Sprite? Cropped(Sprite? sprite)
    {
        if (sprite == null) {
            return null;
        }

        if (_cropped.TryGetValue(sprite, out var done)) {
            return done;
        }

        try {
            var src = Patches.MultiplayerHubPatch.Read(sprite);
            var box = Patches.MultiplayerHubPatch.AlphaBox(src.Pixels, src.W, src.H);
            var px = new Color32[box.width * box.height];
            for (var y = 0; y < box.height; y++) {
                System.Array.Copy(src.Pixels, (box.y + y) * src.W + box.x, px, y * box.width, box.width);
            }

            var tex = new Texture2D(box.width, box.height, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            done = Sprite.Create(tex, new Rect(0, 0, box.width, box.height), new Vector2(0.5f, 0.5f), 100f);
        } catch (System.Exception) {
            done = sprite;
        }

        _cropped[sprite] = done;
        return done;
    }

    private static string _query = "";

    // 0 items, 1 monsters
    private static int _kind;

    /// <summary>
    ///     Two pills (Items / Monsters) over a search field over one scrolled grid of every thing or monster of the
    ///     game (<see cref="SpriteGrid" />: name in the player's language, English name or id, filtered as it is typed);
    ///     a click gives the typed id to <paramref name="onPick" />
    /// </summary>
    internal static void BuildThingPicker(YKLayout card, Color colour, System.Action<string> onPick, bool itemsOnly = false)
    {
        SpriteGridView? grid = null;
        void Show() => grid?.Show(SpriteGrid.Filter(SpriteGrid.All(_kind == 1), _query));

        if (itemsOnly) {
            // (the Market: things only, no Items / Monsters pills)
            _kind = 0;
        } else {
            Pills(card, ["emp_ui_team_banner_items".lang(), "emp_ui_team_banner_monsters".lang()], _kind, i => {
                _kind = i;
                Show();
            });
        }

        var search = card.InputText(_query);
        search.field.characterLimit = 40;
        search.StyleAsSearch("emp_ui_team_banner_search".lang());
        search.field.onValueChanged.AddListener(text => {
            _query = text;
            Show();
        });
        grid = SpriteGrid.Build(card, colour, onPick);
        Show();
    }

    /// <summary>
    ///     The game's tab pills in a row of the card, in a selectable group like the page pills
    /// </summary>
    internal static void Pills(YKLayout card, IEnumerable<string> labels, int selected, System.Action<int> onPick)
    {
        if (EmpUiParts.Pill is not { } mold) {
            return;
        }

        var row = card.Row();
        var group = row.gameObject.AddComponent<UISelectableGroup>();
        group.autoInitialize = false;
        group.colorType = UISelectableGroup.ColorType.Tab;
        group.colorWhenSelected = true;
        group.useTransition = true;
        group.fixRefreshButtons = true;
        foreach (var label in labels) {
            var pill = Util.Instantiate(mold, row.transform);
            pill.mainText.SetText(label);
            if (pill.icon != null) {
                pill.icon.gameObject.SetActive(false);
            }

            pill.tooltip.enable = false;
        }

        group.SetColors();
        group.Init(selected, i => onPick(i));
    }

    /// <summary>
    ///     A small clickable square: a flat colour, or an emblem sprite tinted with the colour
    /// </summary>
    internal static void Swatch(YKLayout row, Color color, float width, float height, bool selected, System.Action? click, Sprite? sprite = null)
    {
        var go = new GameObject("Swatch", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        go.transform.SetParent(row.transform, false);
        var element = go.GetComponent<LayoutElement>();
        (element.minWidth, element.preferredWidth, element.minHeight, element.preferredHeight, element.flexibleWidth) = (width, width, height, height, 0f);
        var image = go.GetComponent<Image>();
        if (sprite != null) {
            image.sprite = sprite;
            image.preserveAspect = true;
            image.color = selected ? color : new(color.r, color.g, color.b, 0.5f);
        } else {
            image.color = color;
        }

        if (selected) {
            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new(2f, -2f);
        }

        if (click != null) {
            go.AddComponent<Button>().onClick.AddListener(() => click());
        } else {
            image.raycastTarget = false;
        }
    }

    /// <summary>
    ///     A dark rectangle with one dot per land of the team, placed by the lands' map position; the name of the land
    ///     under the pointer is said on the line below
    /// </summary>
    internal static void BuildLandMap(YKLayout card, int teamId)
    {
        var lands = Teams.LandsOf(teamId).Select(uid => EClass.game.spatials.Find(uid)).OfType<Zone>().ToList();
        if (lands.Count == 0) {
            card.TextSmall("emp_ui_team_lands_none".lang(), FontColor.Gray);
            return;
        }

        var frame = new GameObject("LandMap", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        frame.transform.SetParent(card.transform, false);
        var element = frame.GetComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = 150f;
        element.flexibleWidth = 1f;
        frame.GetComponent<Image>().color = new(0.07f, 0.08f, 0.1f, 1f);
        frame.GetComponent<Image>().raycastTarget = false;

        var hover = card.TextSmall("emp_ui_team_lands_hover".lang(), FontColor.Gray);
        var (minX, maxX) = (lands.Min(z => z.x), lands.Max(z => z.x));
        var (minY, maxY) = (lands.Min(z => z.y), lands.Max(z => z.y));
        var tint = Of(teamId) is { } banner ? ColorOf(banner.Color) : new Color(0.9f, 0.9f, 0.9f);
        foreach (var zone in lands) {
            // (inside a margin of 10 %; one land, or lands in a line, sit in the middle)
            var fx = maxX > minX ? (zone.x - minX) / (float)(maxX - minX) : 0.5f;
            var fy = maxY > minY ? (zone.y - minY) / (float)(maxY - minY) : 0.5f;
            var dot = new GameObject("Land", typeof(RectTransform), typeof(Image), typeof(EventTrigger));
            dot.transform.SetParent(frame.transform, false);
            dot.GetComponent<Image>().color = tint;
            var rt = (RectTransform)dot.transform;
            rt.anchorMin = rt.anchorMax = new(0.1f + fx * 0.8f, 0.9f - fy * 0.8f);
            rt.sizeDelta = new(12f, 12f);
            var name = zone.Name;
            var trigger = dot.GetComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerEnter, () => hover.text = name);
            AddTrigger(trigger, EventTriggerType.PointerExit, () => hover.text = "emp_ui_team_lands_hover".lang());
        }
    }

    private static void AddTrigger(EventTrigger trigger, EventTriggerType type, System.Action act)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(_ => act());
        trigger.triggers.Add(entry);
    }
}
