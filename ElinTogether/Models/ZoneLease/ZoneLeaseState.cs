using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Helper.Extensions;
using HarmonyLib;
using Newtonsoft.Json;

namespace ElinTogether.Models;

/// <summary>
///     What travels with a zone lease: the map files and the packed zone state
/// </summary>
internal static class ZoneLeaseState
{
    // Spatial._ints entries owned by the host: uid, world map x, world map y
    private static readonly HashSet<int> _hostOwnedInts = [1, 3, 4];

    /// <summary>
    ///     Map files of the zone, saving the loaded map first. Null when there is nothing on disk
    /// </summary>
    internal static Dictionary<string, LZ4Bytes>? CollectMap(Zone zone)
    {
        CommitStaged(zone.uid);
        zone.map?.Save(zone.pathSave);

        if (!Directory.Exists(zone.pathSave)) {
            return null;
        }

        return Directory
            .GetFiles(zone.pathSave, "*.*", SearchOption.TopDirectoryOnly)
            .ToDictionary(MapFileKey, LZ4Bytes.CreateFromFile);
    }

    /// <summary>
    ///     A file of a saved map, by its name. A layer of cell ids over 255 is written as "objs.w2" or "objs.w4"
    ///     (Map.WriteCellIds) and read back under that name: keyed without it, it reached the other game as "objs"
    ///     and loaded as an empty layer
    /// </summary>
    internal static string MapFileKey(string path)
    {
        var name = Path.GetFileName(path);
        return IsWideIds(name) ? name : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    ///     A name the game gives a map file: letters, digits and _, maybe .w2/.w4. Nothing else is written, from
    ///     whoever it comes (security audit 2026-10-27: "C:name", "name:stream", "CON" escaped the zone's folder),
    ///     nor more than the game writes
    /// </summary>
    internal static bool IsMapFileKey(string id)
    {
        var name = IsWideIds(id) ? id.Substring(0, id.Length - 3) : id;
        return name.Length is > 0 and <= 32 && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') &&
               !Reserved.Contains(name.ToUpperInvariant());
    }

    private static readonly HashSet<string> Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "LPT1", "LPT2", "LPT3"];

    private static bool IsWideIds(string name)
    {
        return name.EndsWith(".w2") || name.EndsWith(".w4");
    }

    internal static void WriteMap(Zone zone, Dictionary<string, LZ4Bytes> map)
    {
        WriteFiles(zone.pathSave, map);
    }

    private static void WriteFiles(string folder, Dictionary<string, LZ4Bytes> map)
    {
        Directory.CreateDirectory(folder);

        // (the game writes some twenty files for a map)
        if (!ZoneDataResponse.IsMapSizeSafe(map)) {
            return;
        }

        // a map of another game in the save: its maps are read with the types of the game and its mods only from
        // then on, also when the save is played alone (PeerMapPatch)
        Patches.PeerMapPatch.MarkCurrentSave();

        var left = ZoneDataResponse.MaxMapUnpacked;
        foreach (var (id, asset) in map.Take(64)) {
            if (!IsMapFileKey(id)) {
                continue;
            }

            // beside it, then swapped: a write cut short (disk full, the game killed) leaves the old file whole
            var path = Path.Combine(folder, id);
            left -= asset.DecompressToFile(path + ".part", left);
            Swap(path + ".part", path);
        }
    }

    private static void Swap(string from, string to)
    {
        if (File.Exists(to)) {
            File.Replace(from, to, null);
        } else {
            File.Move(from, to);
        }
    }

    /// <summary>
    ///     Zone uid -> folder of a map a player handed back to the host, waiting for the host's next save. Written
    ///     to the save at once, such a map was ahead of game.txt until that save (up to two minutes after a
    ///     checkpoint): a crash then left a map that no longer matched the characters (a thing picked up gone from
    ///     both, one put down in both), and the uid ranges of the lease, kept in game.txt, were handed out again by
    ///     the next session (reliability audit 2026-10-27, #2). Moved in when the host saves, or before anything
    ///     reads the map: the host walks in, it is leased again
    /// </summary>
    private static readonly Dictionary<int, string> _staged = [];

    private static string StageRoot => GameIO.pathCurrentSave + "_lease/";

    internal static bool IsStaged(int zoneUid)
    {
        return _staged.ContainsKey(zoneUid);
    }

    /// <summary>
    ///     The host keeps a map a player handed back: in the save with the next save of the host's game
    /// </summary>
    internal static void StageMap(Zone zone, Dictionary<string, LZ4Bytes> map)
    {
        var folder = StageRoot + zone.uid + "/";
        // (the save before may still be moving the staged maps in, see CommitAllAfterSave)
        Helper.SaveWriter.Wait();
        if (Directory.Exists(folder)) {
            Directory.Delete(folder, true);
        }

        WriteFiles(folder, map);
        _staged[zone.uid] = folder;
    }

    /// <summary>
    ///     The map of that zone, if one waits, goes into the save now
    /// </summary>
    internal static void CommitStaged(int zoneUid, bool saved = false)
    {
        if (!_staged.TryGetValue(zoneUid, out var folder)) {
            return;
        }

        // forgotten only once it is in: a failing swap leaves a half-written map, and the staged one is the whole
        // map to try again with. Never throws: the callers are the game's own Zone.Activate and a map being sent
        try {
            Commit(zoneUid, folder, saved);
            _staged.Remove(zoneUid);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "The map of zone {ZoneUid} a player handed back could not be put in the save, kept for the next try", zoneUid);
        }
    }

    /// <summary>
    ///     The host's game is being saved: every map waiting goes in with it. What is left in the folder (a crash
    ///     before the save it waited for) belongs to no save and goes
    /// </summary>
    internal static void CommitAllStaged(bool saved = false)
    {
        foreach (var zoneUid in _staged.Keys.ToList()) {
            CommitStaged(zoneUid, saved);
        }

        // a map that failed keeps its folder and its entry for the next save
        if (_staged.Count == 0 && Directory.Exists(StageRoot)) {
            Directory.Delete(StageRoot, true);
        }
    }

    /// <summary>
    ///     The host's game was saved (Game.Save returned true): the maps waiting go in now, after game.txt and never
    ///     before it (robustness audit 2026-11-01: committed in the save's prefix, a save that failed, or one whose
    ///     files were still being written beside the game (FastJoinsAndSaves) when the game stopped, left maps ahead
    ///     of the characters). Under the writer beside the game, they go in on its thread right after its files,
    ///     and only if every one of them was written
    /// </summary>
    internal static void CommitAllAfterSave()
    {
        // read here, on the game's thread: the writer's thread only moves files
        var jobs = _staged
            .Where(kv => kv.Value.StartsWith(GameIO.pathCurrentSave))
            .Select(kv => (Uid: kv.Key, Folder: kv.Value, PathSave: EClass.game.spatials.Find(kv.Key)?.pathSave))
            .ToList();
        var root = StageRoot;

        var queued = Helper.SaveWriter.AfterWrites(() => {
            foreach (var job in jobs) {
                try {
                    MoveIn(job.Uid, job.Folder, job.PathSave, true);
                } catch (Exception ex) {
                    // its folder stays, marked: the next save or load puts it in
                    EmpLog.Error(ex, "The map of zone {ZoneUid} a player handed back could not be put in the save", job.Uid);
                }
            }

            if (Directory.Exists(root) && Directory.GetDirectories(root).Length == 0) {
                Directory.Delete(root, true);
            }
        });

        if (!queued) {
            CommitAllStaged(true);
        }
    }

    /// <summary>
    ///     The save is loaded: a staged map marked as saved (the game stopped while it was being moved in after its
    ///     save) goes in, it matches game.txt. One never saved belongs to no save and goes, as do the maps staged for
    ///     the game played before
    /// </summary>
    [ElinPostLoad]
    internal static void OnLoad(GameIOContext context)
    {
        _staged.Clear();

        try {
            if (!Directory.Exists(StageRoot)) {
                return;
            }

            foreach (var folder in Directory.GetDirectories(StageRoot)) {
                if (File.Exists(Path.Combine(folder, SavedMark)) && int.TryParse(Path.GetFileName(folder), out var zoneUid)) {
                    EmpLog.Warning("Map of zone {ZoneUid} handed back by a player was saved but not moved in, moving it now", zoneUid);
                    MoveIn(zoneUid, folder, EClass.game.spatials.Find(zoneUid)?.pathSave, true);
                }
            }

            Directory.Delete(StageRoot, true);
        } catch (Exception ex) {
            EmpLog.Error(ex, "Maps handed back by players and left beside the save could not be sorted out");
        }
    }

    // in a staged folder: the save holding that map was written, the map belongs in it
    private const string SavedMark = "emp_saved.mark";

    private static void Commit(int zoneUid, string folder, bool saved)
    {
        // (a save still being written beside the game would write its older map over this one)
        Helper.SaveWriter.Wait();

        // staged for another save (the host loaded another game since): not this one's
        if (!folder.StartsWith(GameIO.pathCurrentSave) || !Directory.Exists(folder)) {
            return;
        }

        // a zone destroyed meanwhile (the zone of a quest) has no map to keep
        MoveIn(zoneUid, folder, EClass.game.spatials.Find(zoneUid)?.pathSave, saved);
    }

    // files only, also on the save writer's thread
    private static void MoveIn(int zoneUid, string folder, string? pathSave, bool saved)
    {
        if (!Directory.Exists(folder)) {
            return;
        }

        if (saved) {
            File.WriteAllText(Path.Combine(folder, SavedMark), "");
        }

        if (pathSave is not null) {
            Directory.CreateDirectory(pathSave);
            foreach (var file in Directory.GetFiles(folder)) {
                if (Path.GetFileName(file) != SavedMark) {
                    Swap(file, Path.Combine(pathSave, Path.GetFileName(file)));
                }
            }

            EmpLog.Debug("Map of zone {ZoneUid} handed back by a player is in the save now", zoneUid);
        }

        Directory.Delete(folder, true);
    }

    /// <summary>
    ///     What a copy of the map carries, as numbers two games compare when the map changes hands: count then mix
    ///     of the things on the floor (uid, amount), of what they hold (uid, holder, amount) and of the
    ///     characters saved with the map (uid). <br />
    ///     Left out: the terrain, what characters carry, where they stand, where things lie (a thrown or scattered
    ///     thing lands by the dice of each game, see NetDesync: it would reload the map at every departure after
    ///     a fight), and the characters of the world (players, companions, residents), which Map.Save leaves out too
    /// </summary>
    internal static int[] Sums(Map map)
    {
        var sums = new int[6];

        unchecked {
            foreach (var thing in map.things) {
                if (Skipped(thing)) {
                    continue;
                }

                sums[0]++;
                sums[1] += Mix(thing.uid, thing.Num, 0, 0);

                foreach (var held in thing.things.Flatten()) {
                    if (!Skipped(held)) {
                        sums[2]++;
                        sums[3] += Mix(held.uid, held.parentCard?.uid ?? 0, held.Num, 0);
                    }
                }
            }

            foreach (var chara in map.charas) {
                if (!chara.IsGlobal && !chara.isDead && !PendingUid.IsPending(chara.uid)) {
                    sums[4]++;
                    sums[5] += Mix(chara.uid, 0, 0, 0);
                }
            }
        }

        return sums;
    }

    /// <summary>
    ///     What decides a reload when a map changes hands: the things on the floor and the characters. Not what
    ///     the things hold: nothing keeps the content of a container the same in every game while they play (the
    ///     map check leaves it out, see NetDesync; a stack changed without Card.ModNum is not told, a thing put
    ///     in stacks by the rules of each game), so it differs for less than a reload under the player is worth
    /// </summary>
    internal static bool SameFloor(int[] ours, int[] theirs)
    {
        return ours.Length >= 6 && theirs.Length >= 6 &&
               ours[0] == theirs[0] && ours[1] == theirs[1] && ours[4] == theirs[4] && ours[5] == theirs[5];
    }

    internal static string TellSums(int[] sums)
    {
        return sums.Length < 6
            ? "none"
            : $"things {sums[0]}:{sums[1]:X8}, held {sums[2]}:{sums[3]:X8}, charas {sums[4]}:{sums[5]:X8}";
    }

    // as NetDesync does: cards waiting for their number and ability tokens are each game's own
    private static bool Skipped(Thing thing)
    {
        return thing.isDestroyed || PendingUid.IsPending(thing.uid) || thing.trait is TraitAbility;
    }

    // summed over the cards: the order of the lists does not matter
    private static int Mix(int a, int b, int c, int d)
    {
        unchecked {
            var h = (uint)a * 0x9E3779B1u;
            h = (h ^ (uint)b) * 0x85EBCA6Bu;
            h = (h ^ (uint)c) * 0xC2B2AE35u;
            h = (h ^ (uint)d) * 0x27D4EB2Fu;
            return (int)(h ^ (h >> 15));
        }
    }

    /// <summary>
    ///     Flags, visit count, dates... everything Elin persists about a zone besides its map
    /// </summary>
    internal static int[] GetState(Zone zone)
    {
        var state = zone._ints.ToArray();
        // flags live in bits at runtime, _ints[0] is only refreshed when serializing
        state[0] = (int)zone.bits.Bits;
        return state;
    }

    /// <summary>
    ///     The zone's fortune drum (Zone.fortuneRoll) is world data beside _ints, not in the map: a player who rolled
    ///     it alone handed the zone back without it, the host kept its old one and every world copy brought that
    ///     back. That player's prizes of the month never changed (a stale refresh date made the same month again)
    ///     and the prizes it won were back on the list (GitHub issue 39)
    /// </summary>
    internal static string? GetFortuneRoll(Zone zone)
    {
        return FortuneRoll(zone) is { } data ? JsonConvert.SerializeObject(data, FortuneJson) : null;
    }

    // (private in Zone)
    private static readonly AccessTools.FieldRef<Zone, FortuneRollData?> FortuneRoll =
        AccessTools.FieldRefAccess<Zone, FortuneRollData?>("fortuneRoll");

    // as the game writes it in its save (only its JsonProperty fields), without type names: read from another game
    private static readonly JsonSerializerSettings FortuneJson = new() {
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = ShouldSerializeContractResolver.Instance,
    };

    internal static void ApplyFortuneRoll(Zone zone, string? json)
    {
        if (json is null || json.Length > 64 * 1024) {
            return;
        }

        try {
            if (JsonConvert.DeserializeObject<FortuneRollData>(json, FortuneJson) is not { prizes: not null } data) {
                return;
            }

            // (from another player's game: only prizes this game knows, as many as the game lists)
            data.prizes = data.prizes.Where(p => p?.id is not null && EClass.sources.cards.map.ContainsKey(p.id)).Take(16).ToList();
            FortuneRoll(zone) = data;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "The fortune drum of zone {ZoneUid} handed back could not be read, the host keeps its own", zone.uid);
        }
    }

    /// <summary>
    ///     Zones this game is about to enter while another player is in them, with the state that player's game
    ///     sent. See BossFleePatch
    /// </summary>
    internal static readonly HashSet<int> Imported = [];

    internal static void ApplyState(Zone zone, int[] state, string? subset)
    {
        for (var i = 0; i < Math.Min(state.Length, zone._ints.Length); i++) {
            // "never" in a date (9 expiry, 12 regeneration) is what a client's copy of a map carries, see
            // ZoneDatesRepairPatch: not a date to keep
            if (!_hostOwnedInts.Contains(i) && !(i is 9 or 12 && state[i] == int.MaxValue)) {
                zone._ints[i] = state[i];
            }
        }

        zone.bits.Bits = (uint)state[0];
        zone.idCurrentSubset = subset;
    }
}
