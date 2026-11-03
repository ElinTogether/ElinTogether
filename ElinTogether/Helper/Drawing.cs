using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     A player's free drawing (lead dev 2026-11-02): the emblem editor's layers on a canvas of any size up to 256x256
///     (sides in steps of 16), credited to its creator. Its text, small enough to go over the network (the chat, later):
///     <c>w1|W|H|clip|creator|creatorName|created|basedOn|basedOnName|title|layers</c> (names and title as hex of their
///     UTF-8, the time in Unix seconds, the layers as e3's). The credit is never dropped: a drawing saved from another
///     player keeps it, and a drawing edited by someone else becomes theirs "based on" its creator (no signature: the
///     credit is not proof, only kept)
/// </summary>
internal sealed record Drawing(
    Emblem Art,
    string Title,
    ulong Creator,
    string CreatorName,
    long Created,
    ulong BasedOn = 0,
    string BasedOnName = "")
{
    internal const string Prefix = "w1|";
    internal const int MinSide = 16;
    internal const int MaxSide = 256;
    internal const int SideStep = 16;
    internal const int MaxTitle = 24;

    /// <summary>The longest text a drawing may have (the network's limit, later the chat's)</summary>
    internal const int MaxText = 48000;

    /// <summary>The canvas sizes offered for a new drawing</summary>
    internal static readonly int[] Sides = Enumerable.Range(1, MaxSide / SideStep).Select(i => i * SideStep).ToArray();

    internal string Format() =>
        string.Join("|", "w1", I(Art.W), I(Art.H), Art.Clip ? "1" : "0", Creator.ToString(CultureInfo.InvariantCulture),
            TeamEmblem.Hex(CreatorName), Created.ToString(CultureInfo.InvariantCulture), BasedOn.ToString(CultureInfo.InvariantCulture),
            TeamEmblem.Hex(BasedOnName), TeamEmblem.Hex(Title), TeamEmblem.LayerText(Art));

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A drawing's text read and checked (sizes, layers, names), null when it is not one</summary>
    internal static Drawing? Parse(string text)
    {
        if (text.Length is 0 or > MaxText || !text.StartsWith(Prefix)) {
            return null;
        }

        var p = text.Split('|');
        if (p.Length != 11 || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || p[3] is not ("0" or "1") ||
            !ulong.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out var creator) ||
            !long.TryParse(p[6], NumberStyles.None, CultureInfo.InvariantCulture, out var created) ||
            !ulong.TryParse(p[7], NumberStyles.None, CultureInfo.InvariantCulture, out var basedOn) ||
            !Side(w) || !Side(h) ||
            TeamEmblem.Unhex(p[5]) is not { Length: <= 64 } creatorName || TeamEmblem.Unhex(p[8]) is not { Length: <= 64 } basedOnName ||
            TeamEmblem.Unhex(p[9]) is not { Length: <= MaxTitle } title || title.Any(char.IsControl) ||
            creatorName.Any(char.IsControl) || basedOnName.Any(char.IsControl)) {
            return null;
        }

        // (an empty drawing reads too: a canvas without layers yet)
        Emblem art;
        if (p[10].Length == 0) {
            art = new([], p[3] == "1", w, h);
        } else if (TeamEmblem.ParseLayers(p[10], p[3] == "1") is { } layers &&
                   TeamEmblem.Validate(layers with { W = w, H = h }, EmblemSprites.NormalizeOrKeep) is { } valid) {
            art = valid;
        } else {
            return null;
        }

        return new(art, title, creator, creatorName, created, basedOn, basedOnName);
    }

    /// <summary>
    ///     The same work as that one: the same creator, made at the same time, the same canvas and layers (its title
    ///     may differ: a player renames what they keep)
    /// </summary>
    internal bool SameWork(Drawing other) =>
        Creator == other.Creator && CreatorName == other.CreatorName && Created == other.Created && BasedOn == other.BasedOn &&
        Art.W == other.Art.W && Art.H == other.Art.H && Art.Clip == other.Art.Clip &&
        TeamEmblem.LayerText(Art) == TeamEmblem.LayerText(other.Art);

    private static bool Side(int v) => v is >= MinSide and <= MaxSide && v % SideStep == 0;

    /// <summary>The drawing baked into a texture of its size, point filtered; the caller destroys it</summary>
    internal static Texture2D Bake(Drawing drawing)
    {
        var tex = new Texture2D(drawing.Art.W, drawing.Art.H, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "emp_drawing",
        };
        tex.SetPixels32(TeamEmblemBaker.Bake(drawing.Art));
        tex.Apply(false, false);
        return tex;
    }

    // ---------------------------------------------------------------- who draws

    /// <summary>This player's Steam id (0 when Steam is away)</summary>
    internal static ulong Me
    {
        get {
            try {
                return Steamworks.SteamUser.GetSteamID().m_SteamID;
            } catch (System.Exception) {
                return 0;
            }
        }
    }

    /// <summary>This player's name as others see it (Steam's), the character's when Steam is away</summary>
    internal static string MyName
    {
        get {
            try {
                if (Steamworks.SteamFriends.GetPersonaName() is { Length: > 0 } name) {
                    return Clip(name, 64);
                }
            } catch (System.Exception) {
                // (Steam away)
            }

            return Clip(EClass.pc?.NameSimple ?? "?", 64);
        }
    }

    private static string Clip(string text, int max) => new(text.Where(c => !char.IsControl(c)).Take(max).ToArray());

    /// <summary>A new drawing by this player</summary>
    internal static Drawing New(int w, int h, string title) =>
        new(new([], false, w, h), Clip(title, MaxTitle), Me, MyName, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    ///     The drawing as this player saves an edit of it: theirs already, or a new one by them "based on" its creator
    ///     (and on the ones it was itself based on, named after it)
    /// </summary>
    internal Drawing EditedByMe(Emblem art)
    {
        if (Creator == Me) {
            return this with { Art = art };
        }

        // (based on its creator, and on whom that one was based on: every name kept, the latest first)
        var names = BasedOnName.Length > 0 ? CreatorName + ", " + BasedOnName : CreatorName;
        return new(art, Title, Me, MyName, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Creator, Clip(names, 64));
    }
}

/// <summary>
///     A player's drawings ("My drawings"), shared by every server they play on: a file in the game's own data folder
///     beside the mod's settings (ElinMP/Drawings/&lt;Steam id&gt;/drawings.txt), never in a world's save. One line per
///     drawing: a local id, a tab, its text. Drawings of other players are kept with their credit
/// </summary>
internal static class DrawingStore
{
    internal const int MaxDrawings = 100;

    private static List<(string Id, Drawing Drawing)>? _list;
    private static string _for = "";

    // lines this game could not read (a newer game's drawing): written back as they were, never dropped
    private static readonly List<string> _unread = [];

    // the file could not be read: nothing is written over it
    private static bool _broken;

    internal static string Folder
    {
        get {
            var key = Drawing.Me.ToString(CultureInfo.InvariantCulture);
#if DEBUG
            // (the bench's games share one Steam account and one data folder: each keeps its own list)
            if (EmpConfig.Dev.Identity.Value > 0) {
                key += "_" + EmpConfig.Dev.Identity.Value;
            }
#endif
            return Path.Combine(Application.persistentDataPath, "ElinMP", "Drawings", key);
        }
    }

    private static string FilePath => Path.Combine(Folder, "drawings.txt");

    /// <summary>The drawings, newest first</summary>
    internal static IReadOnlyList<(string Id, Drawing Drawing)> All
    {
        get {
            Load();
            return _list!;
        }
    }

    internal static Drawing? Get(string id) => All.FirstOrDefault(e => e.Id == id).Drawing;

    private static void Load()
    {
        var path = FilePath;
        if (_list != null && _for == path) {
            return;
        }

        _for = path;
        _list = [];
        _unread.Clear();
        _broken = false;
        try {
            // (a save stopped between its delete and its move left only the temporary file)
            var read = File.Exists(path) ? path : File.Exists(path + ".tmp") ? path + ".tmp" : null;
            if (read == null) {
                return;
            }

            foreach (var line in File.ReadAllLines(read)) {
                var tab = line.IndexOf('\t');
                if (tab > 0 && Drawing.Parse(line.Substring(tab + 1)) is { } d) {
                    _list.Add((line.Substring(0, tab), d));
                } else if (line.Length > 0) {
                    _unread.Add(line);
                }
            }
        } catch (System.Exception e) {
            _broken = true;
            EmpLog.Information(e, "Drawings: {Path} could not be read", path);
        }
    }

    private static void Write()
    {
        if (_broken) {
            return;
        }

        try {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, _list!.Select(e => e.Id + "\t" + e.Drawing.Format()).Concat(_unread));
            if (File.Exists(FilePath)) {
                File.Delete(FilePath);
            }

            File.Move(tmp, FilePath);
        } catch (System.Exception e) {
            EmpLog.Information(e, "Drawings: {Path} could not be written", FilePath);
        }
    }

    /// <summary>A drawing kept (a new one first in the list; one of that id replaced); its id, null when the list is full or it is too big</summary>
    internal static string? Save(Drawing drawing, string? id = null)
    {
        Load();
        if (drawing.Format().Length > Drawing.MaxText) {
            return null;
        }

        var at = id == null ? -1 : _list!.FindIndex(e => e.Id == id);
        if (at >= 0) {
            _list![at] = (id!, drawing);
        } else {
            if (_list!.Count >= MaxDrawings) {
                return null;
            }

            id = System.Guid.NewGuid().ToString("N").Substring(0, 12);
            _list.Insert(0, (id, drawing));
        }

        Write();
        return id;
    }

    internal static void Delete(string id)
    {
        Load();
        if (_list!.RemoveAll(e => e.Id == id) > 0) {
            Write();
        }
    }

    /// <summary>A copy of the drawing beside it ("Copy of"), the credit kept; its id</summary>
    internal static string? Duplicate(string id) =>
        Get(id) is { } d
            ? Save(d with { Title = TrimTitle("emp_ui_drawing_copy_of".lang().Replace("{0}", d.Title.Length > 0 ? d.Title : "emp_ui_drawing_untitled".lang())) })
            : null;

    internal static void Rename(string id, string title)
    {
        if (Get(id) is { } d) {
            Save(d with { Title = TrimTitle(title) }, id);
        }
    }

    private static string TrimTitle(string title) => new(title.Where(c => !char.IsControl(c)).Take(Drawing.MaxTitle).ToArray());
}

/// <summary>
///     The editor's autosaved drafts (lead dev 2026-11-02: nothing drawn is lost to a crash or a closed game): the
///     draft of each open document that differs from what is saved, as a drawing's text, in drafts.txt beside the
///     drawings. One line per document: its key (a drawing's id, a team's emblem), the Unix time, the text
/// </summary>
internal static class DrawingDrafts
{
    private static string FilePath => Path.Combine(DrawingStore.Folder, "drafts.txt");

    // (null when the file could not be read: nothing is written over it)
    private static List<string[]>? Lines()
    {
        try {
            return File.Exists(FilePath)
                ? File.ReadAllLines(FilePath).Select(l => l.Split(['\t'], 3)).Where(p => p.Length == 3).ToList()
                : [];
        } catch (System.Exception e) {
            EmpLog.Information(e, "Drawings: the drafts could not be read");
            return null;
        }
    }

    private static void Write(List<string[]> lines)
    {
        try {
            Directory.CreateDirectory(DrawingStore.Folder);
            if (lines.Count == 0) {
                if (File.Exists(FilePath)) {
                    File.Delete(FilePath);
                }

                return;
            }

            var tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, lines.Select(p => string.Join("\t", p)));
            if (File.Exists(FilePath)) {
                File.Delete(FilePath);
            }

            File.Move(tmp, FilePath);
        } catch (System.Exception e) {
            EmpLog.Information(e, "Drawings: the drafts could not be written");
        }
    }

    /// <summary>The draft of that document kept (in place of the one before)</summary>
    internal static void Put(string key, Drawing draft)
    {
        var text = draft.Format();
        if (key.IndexOfAny(['\t', '\n']) >= 0) {
            return;
        }

        if (text.Length > Drawing.MaxText) {
            // (an older, shorter draft would come back over this one)
            Drop(key);
            return;
        }

        if (Lines()?.Where(p => p[0] != key).ToList() is not { } lines) {
            return;
        }

        lines.Add([key, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), text]);
        Write(lines);
    }

    /// <summary>The draft kept for that document and when it was kept, null when there is none</summary>
    internal static (Drawing Draft, long Time)? Get(string key)
    {
        foreach (var p in Lines() ?? []) {
            if (p[0] == key && long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var time) &&
                Drawing.Parse(p[2]) is { } d) {
                return (d, time);
            }
        }

        return null;
    }

    internal static void Drop(string key)
    {
        var lines = Lines();
        if (lines != null && lines.RemoveAll(p => p[0] == key) > 0) {
            Write(lines);
        }
    }
}

/// <summary>
///     The editor's stickers kept by this player (lead dev 2026-11-02): the last ones placed and the favourites, as
///     typed ids, in stickers.txt beside the drawings. One line each: "r" (recent, newest first) or "f" (a favourite),
///     a tab, the id
/// </summary>
internal static class StickerPrefs
{
    internal const int RecentMax = 12;
    internal const int FavouriteMax = 24;

    private static readonly List<string> _recent = [];
    private static readonly List<string> _favourites = [];

    // the folder the lists were read from (another player's on the bench, or none yet)
    private static string? _folder;

    private static string FilePath => Path.Combine(DrawingStore.Folder, "stickers.txt");

    /// <summary>The stickers placed last, newest first</summary>
    internal static IReadOnlyList<string> Recent
    {
        get {
            Load();
            return _recent;
        }
    }

    internal static IReadOnlyList<string> Favourites
    {
        get {
            Load();
            return _favourites;
        }
    }

    internal static bool IsFavourite(string thing) => Favourites.Contains(thing);

    /// <summary>A sticker placed: first in the recent row</summary>
    internal static void Used(string thing)
    {
        Load();
        if (!Fits(thing)) {
            return;
        }

        _recent.Remove(thing);
        _recent.Insert(0, thing);
        if (_recent.Count > RecentMax) {
            _recent.RemoveRange(RecentMax, _recent.Count - RecentMax);
        }

        Write();
    }

    /// <summary>A sticker made a favourite, or no longer one</summary>
    internal static void ToggleFavourite(string thing)
    {
        Load();
        if (!_favourites.Remove(thing)) {
            if (!Fits(thing)) {
                return;
            }

            _favourites.Add(thing);
            if (_favourites.Count > FavouriteMax) {
                _favourites.RemoveAt(0);
            }
        }

        Write();
    }

    // (a typed id of one line: the file's format)
    private static bool Fits(string thing) => thing.Length is > 0 and <= 200 && thing.IndexOfAny(['\t', '\n', '\r']) < 0;

    private static void Load()
    {
        string folder;
        try {
            folder = DrawingStore.Folder;
        } catch {
            return;
        }

        if (_folder == folder) {
            return;
        }

        _folder = folder;
        _recent.Clear();
        _favourites.Clear();
        try {
            if (!File.Exists(FilePath)) {
                return;
            }

            foreach (var line in File.ReadAllLines(FilePath)) {
                var p = line.Split(['\t'], 2);
                if (p.Length != 2 || !Fits(p[1])) {
                    continue;
                }

                var list = p[0] == "r" ? _recent : p[0] == "f" ? _favourites : null;
                if (list != null && !list.Contains(p[1]) && list.Count < (list == _recent ? RecentMax : FavouriteMax)) {
                    list.Add(p[1]);
                }
            }
        } catch (System.Exception e) {
            EmpLog.Information(e, "Drawings: the stickers could not be read");
        }
    }

    private static void Write()
    {
        try {
            Directory.CreateDirectory(DrawingStore.Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, _recent.Select(t => "r\t" + t).Concat(_favourites.Select(t => "f\t" + t)));
            if (File.Exists(FilePath)) {
                File.Delete(FilePath);
            }

            File.Move(tmp, FilePath);
        } catch (System.Exception e) {
            EmpLog.Information(e, "Drawings: the stickers could not be written");
        }
    }
}
