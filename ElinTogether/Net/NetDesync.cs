using System;
using System.Collections.Generic;
using System.Diagnostics;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Patches;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     A few numbers about the active map, computed the same way by the game that keeps it and by every game
///     standing on it, see <see cref="NetDesync" />
/// </summary>
[MessagePackObject]
public class MapSums
{
    [Key(0)]
    public int ZoneUid { get; init; }

    [Key(1)]
    public int Charas { get; init; }

    [Key(2)]
    public int CharaMix { get; init; }

    [Key(3)]
    public int Things { get; init; }

    [Key(4)]
    public int ThingMix { get; init; }

    /// <summary>
    ///     Player chara uid -> mix of its bag and equipment
    /// </summary>
    [Key(5)]
    public Dictionary<int, int>? Bags { get; init; }

    /// <summary>
    ///     Among <see cref="Charas" />, those that are not global: the only ones a copy of the map carries
    /// </summary>
    [Key(6)]
    public int Locals { get; init; }

    [Key(7)]
    public int LocalMix { get; init; }
}

/// <summary>
///     Guest -> the game that keeps its map: its copy of the map differs, so that this journal says it too.
///     With <see cref="Resync" /> the guest asks for the map again right after
/// </summary>
[MessagePackObject]
public class DesyncReportDelta : ElinDelta
{
    [Key(0)]
    public string? ZoneFullName { get; init; }

    [Key(1)]
    public string? Detail { get; init; }

    [Key(2)]
    public bool Resync { get; init; }

    /// <summary>
    ///     What the guest counts, for the keeper's journal to name what differs: uids of its characters when they
    ///     differ, uid and amount of its floor items when they differ, and per player uid, amount and worn (0 or 1)
    ///     of what that character carries there
    /// </summary>
    [Key(3)]
    public int[]? Charas { get; init; }

    [Key(4)]
    public int[]? Things { get; init; }

    [Key(5)]
    public Dictionary<int, int[]>? Bags { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host) {
            return;
        }

        // without detail: bags alone, which are named below and nothing else
        if (!string.IsNullOrEmpty(Detail)) {
            // text written by another game: cut before it reaches this journal
            EmpLog.Warning("Player {PeerIndex} reports a map checksum that differs on {ZoneFullName} (there/here): {Detail}, reload {Resync}",
                OriginPeer, Cut(ZoneFullName), Cut(Detail), Resync);
        }

        if (Resync) {
            host.KeepSpotForResync(OriginPeer);
        }

        try {
            NetDesync.Explain(host, this, Cut(ZoneFullName));
        } catch (Exception ex) {
            EmpLog.Information(ex, "Map checksum not explained");
        }
    }

    private static string? Cut(string? text)
    {
        return text is { Length: > 200 } ? text.Substring(0, 200) : text;
    }
}

/// <summary>
///     Map checksum: nothing ever resends an item or a character a game missed, a gap lasts until the next map
///     load. The game that keeps the map sends its numbers with the player list every 2 seconds
///     (<see cref="SessionPlayersSnapshot" />), the others compare with theirs. A difference only counts when both
///     sides stood still on it for 3 comparisons in a row: what is on its way (a move, a pickup) changes the numbers
///     of one side between two comparisons. Then a warning in both journals, and with the host rule AutoResync the
///     map is asked again when a copy of the map can bring what is missing (floor items, characters that are not
///     global). Bags are compared but neither warn nor reload: the copy a game keeps of ANOTHER player's bag is
///     not held equal by the mod (dev/PLAN_journal_desync_6_octobre.md); the keeper's journal names the cards
///     that differ, at most once per <see cref="BagTellGap" /> seconds and per bag <br />
///     Left out on purpose: where characters stand (the snapshot allows 2 tiles), where items lie (what is thrown
///     or scattered lands by the dice of each game), dead characters, cards waiting for their number
///     (<see cref="PendingUid" />), ability tokens, what chests hold, the world map
/// </summary>
internal static class NetDesync
{
    private const int Strikes = 3;

    // two lists sent closer than that (a player left) count as one comparison
    private const float MinGap = 1.5f;

    // after a map load, before comparing again
    private const float LoadGrace = 6f;

    private const float Cooldown = 30f;
    private const int MaxFruitless = 3;

    // reloads of one map during one stay on it, whatever they brought
    private const int MaxRepairs = 5;
    private const int CombatRange = 8;

    private static WeakReference<Map>? _seenMap;
    private static float _nextCompare;
    private static float _quietUntil;
    private static float _nextRepair;
    private static int _zoneUid;
    private static int _mapKey;
    private static int _mapStrikes;
    private static int _bagKey;
    private static int _bagStrikes;
    private static int _repairs;
    private static int _fruitless;
    private static bool _gaveUp;

    /// <summary>
    ///     Also bring the bag of OUR OWN character to the keeper's copy, under the AutoResync box: the keeper's copy is
    ///     what is saved and what a reconnect gives anyway. On since council 2026-10-25 (2nd), after resync_suite R6
    ///     (a bag moved fast during a gap) and R6c (equip swaps, a backpack inside, an item in hand, 300 ms lag)
    /// </summary>
    internal static bool RepairOwnBag = true;

    /// <summary>
    ///     Asking again for another player's bag that differs (its copy in this game), under the AutoResync box.
    ///     On since council 2026-10-25 (resync_suite R4); our own bag: <see cref="RepairOwnBag" />
    /// </summary>
    internal static bool RepairBags = true;

    // player chara uid -> next time its bag may be asked again, how often it was, what was compared then
    private static readonly Dictionary<int, float> _nextBagAsk = [];
    private static readonly Dictionary<int, int> _bagAsks = [];

    // how often each bag was asked during this stay on the map, whatever came of it: no more after MaxBagAsks
    private const int MaxBagAsks = 3;
    private static readonly Dictionary<int, int> _bagAsksOnMap = [];
    private static readonly Dictionary<int, (int Ours, int Theirs, float At)> _askedBags = [];

    // an answer older than that is about a bag that had time to change
    private const float BagAnswerWait = 10f;

    // a bag that differs is named in the keeper's journal that often at most
    private const float BagTellGap = 300f;
    private static readonly Dictionary<int, float> _nextBagTell = [];

    // lists longer than that are not sent with a report
    private const int MaxListed = 2000;

    private static MapSums? _lastLocal;
    private static MapSums? _lastHost;
    private static string _lastWarning = "none";
    private static double _lastCostMs;

    /// <summary>
    ///     This game just became a client on the map it stands on without loading it (ZoneSoftRejoin): nothing is
    ///     compared for as long as after a map load, what the host did meanwhile is still on its way
    /// </summary>
    internal static void HoldOff()
    {
        _quietUntil = Math.Max(_quietUntil, Time.unscaledTime + LoadGrace);
        _mapStrikes = _bagStrikes = 0;
    }

    /// <summary>
    ///     The numbers of the active map as this game holds it, null when there is nothing to compare
    /// </summary>
    internal static MapSums? Collect()
    {
        try {
            if (!EClass.core.IsGameStarted || EClass._zone is not { IsRegion: false } zone || EClass._map is not { } map) {
                return null;
            }

            var watch = Stopwatch.StartNew();

            var players = new HashSet<int>();
            foreach (var state in NetSession.Instance.CurrentPlayers) {
                players.Add(state.CharaUid);
            }

            var bags = new Dictionary<int, int>();
            int charas = 0, charaMix = 0, things = 0, thingMix = 0, locals = 0, localMix = 0;

            unchecked {
                foreach (var chara in map.charas) {
                    if (!Counted(chara)) {
                        continue;
                    }

                    charas++;
                    charaMix += Mix(chara.uid, 0, 0, 0);
                    if (!chara.IsGlobal) {
                        locals++;
                        localMix += Mix(chara.uid, 0, 0, 0);
                    }

                    if (players.Contains(chara.uid)) {
                        bags[chara.uid] = Bag(chara);
                    }
                }

                foreach (var thing in map.things) {
                    if (Skipped(thing)) {
                        continue;
                    }

                    things++;
                    // not its tile: a thrown item, an arrow, scattered loot land by the dice of each game
                    thingMix += Mix(thing.uid, thing.Num, 0, 0);
                }
            }

            _lastCostMs = watch.Elapsed.TotalMilliseconds;

            return new() {
                ZoneUid = zone.uid,
                Charas = charas,
                CharaMix = charaMix,
                Things = things,
                ThingMix = thingMix,
                Bags = bags,
                Locals = locals,
                LocalMix = localMix,
            };
        } catch (Exception ex) {
            EmpLog.Information(ex, "Map checksum not computed");
            return null;
        }
    }

    /// <summary>
    ///     The numbers of the game that keeps the map arrived: compare, warn, repair
    /// </summary>
    // zone -> why its map is to be reloaded as soon as a reload may be made there, see OweRepair
    private static (int ZoneUid, string Kind, int Count)? _owed;

    /// <summary>
    ///     What the map just loaded here should have changed is lost (ElinDeltaManager, over the limit of what is
    ///     kept while loading): reloaded at the first comparison that may, under the rule and cooldowns of a
    ///     checksum repair. Nothing repaired it before (reliability audit 2026-10-27, #7)
    /// </summary>
    internal static void OweRepair(string kind, int count)
    {
        _owed = (EClass._zone?.uid ?? -1, kind, count);
    }

    internal static void Compare(MapSums? host)
    {
        try {
            CompareCore(host);
        } catch (Exception ex) {
            EmpLog.Information(ex, "Map checksum not compared");
        }
    }

    private static void CompareCore(MapSums? host)
    {
        var session = NetSession.Instance;
        if (host is null || session.Connection is not ElinNetClient client) {
            return;
        }

        var now = Time.unscaledTime;
        if (now < _nextCompare) {
            return;
        }

        _nextCompare = now + MinGap;

        // not on that map (yet)
        if (!EClass.core.IsGameStarted || EClass._zone?.uid != host.ZoneUid || EClass._map is not { } map) {
            _mapStrikes = _bagStrikes = 0;
            return;
        }

        if (host.ZoneUid != _zoneUid) {
            _zoneUid = host.ZoneUid;
            _repairs = _fruitless = 0;
            _gaveUp = false;
            _bagAsksOnMap.Clear();
            _nextBagTell.Clear();
        }

        // a map just loaded: what the host did meanwhile is still on its way
        if (_seenMap is null || !_seenMap.TryGetTarget(out var seen) || !ReferenceEquals(seen, map)) {
            _seenMap = new(map);
            // not shorter than the silence a reload asked for
            _quietUntil = Math.Max(_quietUntil, now + LoadGrace);
        }

        if (now < _quietUntil) {
            _mapStrikes = _bagStrikes = 0;
            return;
        }

        if (_owed is { } owed) {
            // another map since: that one is loaded whole
            if (owed.ZoneUid != host.ZoneUid) {
                _owed = null;
            } else if (RepairFailingDelta(owed.Kind, owed.Count)) {
                _owed = null;
                return;
            }
        }

        if (Collect() is not { } local) {
            return;
        }

        _lastLocal = local;
        _lastHost = host;

        // a strike only when neither side moved since the last comparison
        var mapDiffers = local.Charas != host.Charas || local.CharaMix != host.CharaMix ||
                         local.Things != host.Things || local.ThingMix != host.ThingMix;
        var mapKey = Mix(Mix(local.Charas, local.CharaMix, local.Things, local.ThingMix),
            Mix(host.Charas, host.CharaMix, host.Things, host.ThingMix), 0, 0);
        _mapStrikes = !mapDiffers ? 0 : mapKey == _mapKey ? _mapStrikes + 1 : 1;
        _mapKey = mapKey;

        List<int>? bags = null;
        var bagKey = 0;
        foreach (var (uid, theirs) in host.Bags ?? []) {
            if (!local.Bags!.TryGetValue(uid, out var ours)) {
                continue;
            }

            if (ours == theirs) {
                _bagAsks.Remove(uid);
                continue;
            }

            (bags ??= []).Add(uid);
            unchecked {
                bagKey += Mix(uid, ours, theirs, 0);
            }
        }

        _bagStrikes = bags is null ? 0 : bagKey == _bagKey ? _bagStrikes + 1 : 1;
        _bagKey = bagKey;

        if (!mapDiffers) {
            _fruitless = 0;
            _gaveUp = _repairs >= MaxRepairs;
        }

        // a copy of the map brings floor items and the characters that are not global, nothing else: the
        // characters of the players, their companions and the other global ones live in the world
        var charasDiffer = local.Charas != host.Charas || local.CharaMix != host.CharaMix;
        var thingsDiffer = local.Things != host.Things || local.ThingMix != host.ThingMix;
        var fixable = thingsDiffer || local.Locals != host.Locals || local.LocalMix != host.LocalMix;

        // bags never warn and never reload, see the summary
        var warn = _mapStrikes == Strikes;
        var gaveUp = false;
        var repair = _mapStrikes >= Strikes && fixable && ShouldRepair(session, client, now, out gaveUp);

        Dictionary<int, int[]>? tellBags = null;
        if (bags is not null && _bagStrikes == Strikes) {
            foreach (var uid in bags) {
                if ((_nextBagTell.TryGetValue(uid, out var next) && now < next) ||
                    map.charas.Find(c => c.uid == uid) is not { } carrier) {
                    continue;
                }

                _nextBagTell[uid] = now + BagTellGap;
                (tellBags ??= [])[uid] = BagList(carrier);
            }
        }

        // not while the map is asked again: the answer would land in the middle of its load
        if (RepairBags && !repair && bags is not null && _bagStrikes >= Strikes) {
            AskBags(session, client, now, local, host, bags);
        }

        if (!warn && !repair && !gaveUp && tellBags is null) {
            return;
        }

        var zoneName = EClass._zone.ZoneFullName;
        var detail = warn || repair || gaveUp ? Detail(local, host) : null;

        if (tellBags is not null) {
            EmpLog.Information("Bags of {Uids} differ from their keeper's on {ZoneFullName}: named in its journal, left as they are",
                string.Join(",", tellBags.Keys), zoneName);
        }

        if (warn) {
            _lastWarning = $"{DateTime.Now:HH:mm:ss} {zoneName}: {detail}";
            EmpLog.Warning("Map checksum differs on {ZoneFullName} (here/host): {Detail}",
                zoneName, detail);
        }

        if (gaveUp) {
            detail = $"persistent after {_repairs} reloads, {detail}";
            _lastWarning = $"{DateTime.Now:HH:mm:ss} {zoneName}: {detail}";
            EmpLog.Warning("Map checksum still differs on {ZoneFullName} after {Repairs} reloads, no more reload of this map: {Detail}",
                zoneName, _repairs, detail);
        }

        if (repair) {
            EmpLog.Information("Reloading {ZoneFullName} to repair it, attempt {Repairs}",
                zoneName, _repairs);
            EmpPop.Information("emp_ui_resync".lang());

            _mapStrikes = 0;
            _quietUntil = now + LoadGrace + LoadGrace;
        }

        // before the request: the host notes where we stand before it answers the map
        client.Host.Send(new WorldStateDeltaList {
            DeltaList = [
                new DesyncReportDelta {
                    ZoneFullName = zoneName,
                    Detail = detail,
                    Resync = repair,
                    Charas = warn && charasDiffer ? CharaList(map) : null,
                    Things = warn && thingsDiffer ? ThingList(map) : null,
                    Bags = tellBags,
                },
            ],
        });

        if (repair) {
            client.MarkResync();
            client.RequestZoneState(MapDataRequest.CurrentRemoteZone);
        }
    }

    /// <summary>
    ///     A kind of delta kept failing here (ElinDeltaManager): what it should have changed is missing from this
    ///     map. Reloaded as a checksum repair is, same rule, stillness and cooldowns; true when it was asked
    /// </summary>
    internal static bool RepairFailingDelta(string deltaType, int failures)
    {
        var session = NetSession.Instance;
        var now = Time.unscaledTime;
        if (session.Connection is not ElinNetClient client || !EClass.core.IsGameStarted || EClass._zone is not { } zone ||
            now < _quietUntil || !ShouldRepair(session, client, now, out _)) {
            return false;
        }

        var detail = $"{failures} failures of {deltaType}";
        _lastWarning = $"{DateTime.Now:HH:mm:ss} {zone.ZoneFullName}: {detail}";
        EmpLog.Warning("Reloading {ZoneFullName} after {Detail}, attempt {Repairs}",
            zone.ZoneFullName, detail, _repairs);
        EmpPop.Information("emp_ui_resync".lang());

        _mapStrikes = 0;
        _quietUntil = now + LoadGrace + LoadGrace;

        // before the request: the host notes where we stand before it answers the map
        client.Host.Send(new WorldStateDeltaList {
            DeltaList = [
                new DesyncReportDelta {
                    ZoneFullName = zone.ZoneFullName,
                    Detail = detail,
                    Resync = true,
                },
            ],
        });
        // (the guest keeps its own tile after the reload, as for a checksum repair)
        client.MarkResync();
        client.RequestZoneState(MapDataRequest.CurrentRemoteZone);
        return true;
    }

    /// <summary>
    ///     The reload drops what happens to the map while it loads and takes the screen for a moment: only when the
    ///     player does nothing, at most once per <see cref="Cooldown" /> (doubled after each one on the same map),
    ///     and no more after <see cref="MaxFruitless" /> reloads that never brought the numbers together or
    ///     <see cref="MaxRepairs" /> reloads of the same map
    /// </summary>
    private static bool ShouldRepair(NetSession session, ElinNetClient client, float now, out bool gaveUp)
    {
        gaveUp = false;

        if (!session.Rules.AutoResync || _gaveUp || now < _nextRepair) {
            return false;
        }

        if (_fruitless >= MaxFruitless || _repairs >= MaxRepairs) {
            gaveUp = _gaveUp = true;
            return false;
        }

        if (!client.IsQuietForResync || (session.Transport as ElinNetClient)?.IsQuietForResync == false || Busy()) {
            return false;
        }

        _fruitless++;
        _nextRepair = now + Cooldown * (1 << Math.Min(_repairs++, 4));
        return true;
    }

    /// <summary>
    ///     A bag that stood different for <see cref="Strikes" /> comparisons: ask its keeper for that character in
    ///     full (<see cref="CharaBagDelta" />), at most once per <see cref="Cooldown" /> and per character (doubled
    ///     each time until that bag is the same again) and at most <see cref="MaxBagAsks" /> times per stay on a map,
    ///     under the rule and the stillness a map reload needs
    /// </summary>
    private static void AskBags(NetSession session, ElinNetClient client, float now, MapSums local, MapSums host, List<int> bags)
    {
        if (!session.Rules.AutoResync || !Quiet(session, client)) {
            return;
        }

        foreach (var uid in bags) {
            var own = EClass.pc.uid == uid;
            if ((own && !RepairOwnBag) || (_nextBagAsk.TryGetValue(uid, out var next) && now < next) ||
                _bagAsksOnMap.GetValueOrDefault(uid) >= MaxBagAsks) {
                continue;
            }

            _bagAsksOnMap[uid] = _bagAsksOnMap.GetValueOrDefault(uid) + 1;
            var asks = _bagAsks.GetValueOrDefault(uid);
            _bagAsks[uid] = asks + 1;
            _nextBagAsk[uid] = now + Cooldown * (1 << Math.Min(asks, 4));
            if (own) {
                _askedBags[uid] = (local.Bags![uid], host.Bags![uid], now);
            }

            EmpLog.Information("Asking for the bag of {Uid} again, ours {Own}, attempt {Asks} (here {Local:X8} | host {Host:X8})",
                uid, own, asks + 1, local.Bags![uid], host.Bags![uid]);

            client.Host.Send(new WorldStateDeltaList {
                DeltaList = [
                    new CharaBagDelta {
                        Uid = uid,
                        Mix = host.Bags[uid],
                    },
                ],
            });
        }
    }

    /// <summary>
    ///     The keeper's copy of a bag arrived (<paramref name="theirs" />) and ours differs (<paramref name="ours" />):
    ///     why it must stay as it is, null to replace it. <br />
    ///     Someone else's bag is a copy nobody saves: replaced whenever this player does nothing. Our own is the
    ///     one we act on, and what we do to it shows here before the keeper hears of it: only behind
    ///     <see cref="RepairOwnBag" />, only the answer to our own request, and only if neither side moved since
    ///     the comparisons that led to it
    /// </summary>
    internal static string? BagRepairRefused(Chara chara, int ours, int theirs)
    {
        var session = NetSession.Instance;
        if (session.Connection is not ElinNetClient client || !session.Rules.AutoResync) {
            return "rule off";
        }

        if (!Quiet(session, client)) {
            return "busy";
        }

        if (!chara.IsPC) {
            return null;
        }

        if (!RepairOwnBag) {
            return "our own bag, sub-option off";
        }

        if (!_askedBags.Remove(chara.uid, out var asked) || Time.unscaledTime - asked.At > BagAnswerWait) {
            return "not asked by us";
        }

        return asked.Ours != ours || asked.Theirs != theirs ? "it moved since we asked" : null;
    }

    private static bool Quiet(NetSession session, ElinNetClient client)
    {
        return client.IsQuietForResync && (session.Transport as ElinNetClient)?.IsQuietForResync != false && !Busy();
    }

    private static bool Busy()
    {
        var pc = EClass.pc;
        if (pc is null || pc.isDead || !pc.HasNoGoal || pc.enemy is not null ||
            EClass.ui.IsActive || EClass.ui.IsDragging ||EClass.scene.mode != Scene.Mode.Zone) {
            return true;
        }

        foreach (var chara in EClass._map.charas) {
            if (!chara.isDead && chara.IsHostile() && chara.pos.Distance(pc.pos) <= CombatRange) {
                return true;
            }
        }

        return false;
    }

    private static string Detail(MapSums local, MapSums host)
    {
        var parts = new List<string>();

        if (local.Charas != host.Charas || local.CharaMix != host.CharaMix) {
            parts.Add($"charas {local.Charas}/{host.Charas}" + (local.Charas == host.Charas ? " (not the same ones)" : "") +
                      $" of which not global {local.Locals}/{host.Locals}");
        }

        if (local.Things != host.Things || local.ThingMix != host.ThingMix) {
            parts.Add($"things {local.Things}/{host.Things}" +
                      (local.Things == host.Things ? " (not the same ones or amounts)" : ""));
        }

        return string.Join(", ", parts);
    }

    private static bool Counted(Chara chara)
    {
        return !chara.isDead && chara.IsInActiveMap && !PendingUid.IsPending(chara.uid);
    }

    private static int[]? CharaList(Map map)
    {
        var list = new List<int>();
        foreach (var chara in map.charas) {
            if (Counted(chara)) {
                list.Add(chara.uid);
            }
        }

        return list.Count > MaxListed ? null : list.ToArray();
    }

    private static int[]? ThingList(Map map)
    {
        var list = new List<int>();
        foreach (var thing in map.things) {
            if (!Skipped(thing)) {
                list.Add(thing.uid);
                list.Add(thing.Num);
            }
        }

        return list.Count > MaxListed * 2 ? null : list.ToArray();
    }

    private static int[] BagList(Chara chara)
    {
        var list = new List<int>();
        foreach (var thing in chara.things.Flatten()) {
            if (!Skipped(thing) && list.Count < MaxListed * 3) {
                list.Add(thing.uid);
                list.Add(thing.Num);
                list.Add(thing.c_equippedSlot != 0 ? 1 : 0);
            }
        }

        return list.ToArray();
    }

    /// <summary>
    ///     Keeper side: a guest sent what it counts, this journal names what differs. Only numbers come from the
    ///     guest, the names are ours. With the rule AutoResync a character the guest lacks is sent to everyone
    ///     again (<see cref="CardGenDelta" />, without effect where it is known): the positions sent 5 times a
    ///     second put a known character back on a map, an unknown one never
    /// </summary>
    internal static void Explain(ElinNetHost host, DesyncReportDelta report, string? zoneName)
    {
        if (EClass._map is not { } map) {
            return;
        }

        if (report.Charas is { } theirCharas) {
            var there = new HashSet<int>(theirCharas);
            var missing = new List<string>();
            var resent = 0;
            foreach (var chara in map.charas) {
                if (!Counted(chara) || there.Remove(chara.uid)) {
                    continue;
                }

                missing.Add($"{chara.uid} {chara.id}{(chara.IsGlobal ? " global" : "")}{(chara.IsPlayer ? " player" : "")}");
                // only the card this game already tells the others about: caching another one renumbers it
                if (NetSession.Instance.Rules.AutoResync && resent < 8 && CardCache.Contains(chara)) {
                    resent++;
                    host.Delta.AddRemote(CardGenDelta.Create(chara));
                }
            }

            var extra = new List<string>();
            foreach (var uid in there) {
                extra.Add(CardCache.Find(uid) is Chara known
                    ? $"{uid} {known.id} ({(known.isDead ? "dead here" : known.currentZone == EClass._zone ? "here but not counted" : "off this map here")})"
                    : $"{uid} (unknown here)");
            }

            EmpLog.Information("Player {PeerIndex} on {ZoneFullName}: characters it lacks [{Missing}], characters only it has [{Extra}], sent again {Resent}",
                report.OriginPeer, zoneName, Some(missing), Some(extra), resent);
        }

        if (report.Things is { } theirThings) {
            var here = new Dictionary<int, Thing>();
            foreach (var thing in map.things) {
                if (!Skipped(thing)) {
                    here[thing.uid] = thing;
                }
            }

            EmpLog.Information("Player {PeerIndex} on {ZoneFullName}: floor items differ (- it lacks, + only it has, amount there/here) [{Diff}]",
                report.OriginPeer, zoneName, Some(Diff(here, theirThings, 2)));
        }

        foreach (var (uid, theirBag) in report.Bags ?? []) {
            if (map.charas.Find(c => c.uid == uid) is not { } carrier) {
                continue;
            }

            var here = new Dictionary<int, Thing>();
            foreach (var thing in carrier.things.Flatten()) {
                if (!Skipped(thing)) {
                    here[thing.uid] = thing;
                }
            }

            EmpLog.Information("Player {PeerIndex} holds another bag of {Uid} on {ZoneFullName}, left as it is (- it lacks, + only it has, amount there/here) [{Diff}]",
                report.OriginPeer, uid, zoneName, Some(Diff(here, theirBag, 3)));
        }
    }

    // theirs: uid, amount (and worn) of each card, in groups of step
    private static List<string> Diff(Dictionary<int, Thing> here, int[] theirs, int step)
    {
        var diff = new List<string>();
        var seen = new HashSet<int>();
        for (var i = 0; i + step <= theirs.Length; i += step) {
            var uid = theirs[i];
            seen.Add(uid);
            if (!here.TryGetValue(uid, out var ours)) {
                diff.Add(CardCache.Find(uid) is Thing known
                    ? $"+{uid} {known.id} ({(known.isDestroyed ? "destroyed here" : known.GetRootCard() is Chara holder ? $"carried by {holder.uid} here" : "elsewhere here")})"
                    : $"+{uid} (unknown here)");
            } else if (ours.Num != theirs[i + 1]) {
                diff.Add($"{uid} {ours.id} {theirs[i + 1]}/{ours.Num}");
            } else if (step > 2 && ours.c_equippedSlot != 0 != (theirs[i + 2] != 0)) {
                diff.Add($"{uid} {ours.id} worn {theirs[i + 2] != 0}/{ours.c_equippedSlot != 0}");
            }
        }

        foreach (var (uid, ours) in here) {
            if (!seen.Contains(uid)) {
                diff.Add($"-{uid} {ours.id} x{ours.Num}");
            }
        }

        return diff;
    }

    private static string Some(List<string> entries)
    {
        return entries.Count <= 12
            ? string.Join(", ", entries)
            : string.Join(", ", entries.GetRange(0, 12)) + $" and {entries.Count - 12} more";
    }

    internal static bool Skipped(Thing thing)
    {
        return thing.isDestroyed || PendingUid.IsPending(thing.uid) || thing.trait is TraitAbility;
    }

    internal static int Bag(Chara chara)
    {
        var mix = 0;
        foreach (var thing in chara.things.Flatten()) {
            if (Skipped(thing)) {
                continue;
            }

            unchecked {
                mix += Mix(thing.uid, thing.Num, thing.c_equippedSlot != 0 ? 1 : 0,
                    ((int)thing.blessedState * 64 + thing.encLV) * 2 + (thing.isAcidproof ? 1 : 0));
            }
        }

        return mix;
    }

    // summed over the cards: the order of the lists does not matter, and two equal cards do not cancel out
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
    ///     For emp.desync and the test bench
    /// </summary>
    internal static string Describe()
    {
        var local = Collect();
        var cost = _lastCostMs;

        return $"here: {Line(local)}, computed in {cost:F2} ms\n" +
               $"last compared: here {Line(_lastLocal)} | host {Line(_lastHost)}\n" +
               $"strikes: map {_mapStrikes}, bags {_bagStrikes}; reloads {_repairs}, fruitless {_fruitless}, gave up {_gaveUp}; " +
               $"bags asked {string.Join(" ", _bagAsks)}, own bag repair {RepairOwnBag}\n" +
               $"last warning: {_lastWarning}";
    }

    private static string Line(MapSums? sums)
    {
        if (sums is null) {
            return "nothing";
        }

        var bags = new List<string>();
        foreach (var (uid, mix) in sums.Bags ?? []) {
            bags.Add($"{uid}:{mix:X8}");
        }

        return $"zone {sums.ZoneUid} charas {sums.Charas}:{sums.CharaMix:X8} not global {sums.Locals}:{sums.LocalMix:X8} things {sums.Things}:{sums.ThingMix:X8} " +
               $"bags {string.Join(" ", bags)}";
    }
}

internal partial class ElinNetHost
{
    /// <summary>
    ///     A player loads this map again to repair its copy: it stays where it stands, not next to us, see
    ///     OnZoneDataReceivedResponse
    /// </summary>
    internal void KeepSpotForResync(int peerId)
    {
        if (States.TryGetValue(peerId, out var state) && ActiveRemoteCharas.TryGetValue(peerId, out var chara) &&
            chara.pos is { IsValid: true } stood && _zone is { } zone) {
            _returnSpots[state.User] = (zone.uid, stood.Copy(), null, Time.unscaledTime + 30f, false);
        }
    }
}

internal partial class ElinNetClient
{
    /// <summary>
    ///     Nothing of ours is between two maps: no travel asked or granted, no map being loaded
    /// </summary>
    internal bool IsQuietForResync =>
        IsConnected && _pendingTravel is null && _pendingGrant is null && !_rejoining && _awaitingActivation == 0 && !IsInTransfer;

    // the map asked again to repair it, until when: see TakeResyncSpot
    private int _resyncZone;
    private float _resyncUntil;

    /// <summary>
    ///     We ask for the map we stand on again: the tile that comes with it is the one the host's copy of our
    ///     character had, a step or more behind when we walked during the round trip
    /// </summary>
    internal void MarkResync()
    {
        _resyncZone = _zone?.uid ?? 0;
        _resyncUntil = Time.unscaledTime + 30f;
    }

    /// <summary>
    ///     The asked map is loaded again in place: our own tile, taken just before the old map is left
    /// </summary>
    private Point? TakeResyncSpot(Zone zone)
    {
        var keep = _resyncZone == zone.uid && _zone == zone && Time.unscaledTime < _resyncUntil;
        _resyncZone = 0;
        return keep && pc.pos.IsValid ? pc.pos.Copy() : null;
    }

    /// <summary>
    ///     Our own tile on the reloaded map when we can still stand there, then the host is told
    /// </summary>
    private bool MoveToResyncSpot(Point? own)
    {
        if (own is not { IsInBounds: true, IsBlocked: false } ||
            (own.FirstChara is { } other && other != pc && other != pc.ride && other != pc.parasite)) {
            return false;
        }

        EmpLog.Debug("Resync: keeping our own tile {@Pos}", (Position)own);
        pc.Place(own);
        Delta.AddRemote((CharaMoveDelta)pc);
        return true;
    }
}
