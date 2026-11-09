using ElinTogether.LangMod;
using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     A guest's bout with an adventurer (the "Duel" of the game's dialog), played by the game's rules as the host
///     plays one (council 7, step 5). The guest fights in a zone of its own, as on any map it goes to alone, with its
///     copy of the adventurer. The adventurer itself is the host's: it leaves its town for the bout and comes back
///     after it, healed and friendly; the week before its next bout starts for everyone; a won bout counts (recruiting
///     opens) and its reward, the best piece the adventurer wears or some platinum, is taken from the host's
///     adventurer and given by the host. What is the guest's own stays in its game: the fame it wins or loses. <br />
///     Before, all of it happened in the guest's copy of the world only: the adventurer stood in town in the host's
///     game during the bout, and nothing of the bout was left after the guest's next copy of the world
/// </summary>
public static class AdventurerBout
{
    // CINT.dateBout, and how often it was beaten in a bout (recruiting a unique one opens after the first)
    private const int DateBout = 59;
    private const int Beaten = 111;

    // a bout in the host's save (ElinNetHost.AdventurerBouts): where the adventurer stood, who fights it, how it went
    private const int RecZone = 0;
    private const int RecX = 1;
    private const int RecZ = 2;
    private const int RecGuest = 3;
    private const int RecResult = 4;

    // the guest's game: the adventurer of the bout it plays, and whether how it went was told
    private static int _target;
    private static bool _told;

    // the host's adventurer back in its town, come while this game still stands in the bout: taken once it leaves
    private static AdventurerBoutDelta? _home;

    internal static bool Enabled => NetSession.Instance.Rules.GuestBouts;

    private static Chara? Find(int uid)
    {
        return EClass.game.cards.Find(uid) ?? EClass._map?.FindChara(uid);
    }

    // ---------------------------------------------------------------- the guest's game

    /// <summary>
    ///     The game's dialog made the zone of a bout (SpatialGen.CreateInstance): a guest tells the host, the host
    ///     tells everyone what its own bout changes on the adventurer
    /// </summary>
    internal static void OnCreated(Zone zone)
    {
        if (zone.instance is not ZoneInstanceBout { uidTarget: not 0 } bout || ElinDelta.IsApplying) {
            return;
        }

        switch (NetSession.Instance.Transport) {
            case ElinNetClient client when Enabled:
                _target = bout.uidTarget;
                _told = false;
                Send(client, AdventurerBoutDelta.Start);
                break;
            case ElinNetHost when NetSession.Instance.Connection is ElinNetHost:
                // (the dialog sets the date right after making the zone)
                var uid = bout.uidTarget;
                CoroutineHelper.Deferred(() => TellMarks(uid));
                break;
        }
    }

    /// <summary>
    ///     The adventurer of this guest's bout says how it went (Card.DamageHP: "bout_win", "bout_lose"): the host
    ///     settles the adventurer and the reward. The fame is this player's own, as the game's ZonePreEnterBoutWin
    ///     gives it (the fame lost is taken by DamageHP itself)
    /// </summary>
    internal static void OnDialog(Chara chara, string step)
    {
        if (_target == 0 || chara.uid != _target || _told || step is not ("bout_win" or "bout_lose") ||
            NetSession.Instance.Transport is not ElinNetClient client || EClass._zone.instance is not ZoneInstanceBout) {
            return;
        }

        var won = step == "bout_win";
        if (won) {
            SE.Play("questComplete");
            EClass.player.ModFame(EClass.rndHalf(Mathf.Max(10, 10 + (chara.LV - EClass.pc.LV) * 3)));
        }

        Send(client, won ? AdventurerBoutDelta.Won : AdventurerBoutDelta.Lost);
    }

    /// <summary>
    ///     This guest leaves the zone of its bout (ElinNetClient.TryTravel): left before it ended, the host puts the
    ///     adventurer back without a winner. This game's copy of the adventurer is dropped: the host's comes back to
    ///     its town, and an old copy here would be taken for it (still hostile, still wearing the reward)
    /// </summary>
    internal static void Leave(ZoneInstanceBout bout)
    {
        if (bout.uidTarget == 0 || bout.uidTarget != _target || NetSession.Instance.Transport is not ElinNetClient client) {
            return;
        }

        if (!_told) {
            Send(client, AdventurerBoutDelta.Left);
        }

        _target = 0;
        if (Find(bout.uidTarget) is { IsPC: false } copy) {
            EClass.game.cards.globalCharas.Remove(copy);
            if (copy.currentZone == EClass._zone && EClass._map.charas.Contains(copy)) {
                EClass._zone.RemoveCard(copy);
            } else {
                copy.parent = null;
                copy.currentZone = null;
            }

            CardCache.Remove(copy.uid);
            foreach (var thing in copy.things.Flatten()) {
                CardCache.Remove(thing.uid);
            }
        }

        if (_home is { } home && home.TargetUid == bout.uidTarget) {
            _home = null;
            BackHome(home);
        }
    }

    /// <summary>
    ///     The game's own reward when this guest comes back to town (ZonePreEnterBoutWin): the host gives it
    /// </summary>
    internal static bool SkipsReward => Enabled && NetSession.Instance.Transport is ElinNetClient;

    private static void Send(ElinNetClient client, int kind)
    {
        _told = kind != AdventurerBoutDelta.Start;
        var delta = new AdventurerBoutDelta {
            Kind = kind,
            TargetUid = _target,
        };

        // (in the zone of the bout this game is alone: what it says goes as chat does)
        if (NetSession.Instance.IsAway) {
            client.SendWhileAway(delta);
        } else {
            client.Delta.AddRemote(delta);
        }
    }

    /// <summary>
    ///     What the host's adventurer keeps of its bouts: the dialog of every game offers a bout by the same numbers
    /// </summary>
    internal static void Learn(AdventurerBoutDelta marks)
    {
        if (Find(marks.TargetUid) is { } chara) {
            chara.SetInt(DateBout, marks.DateBout);
            chara.SetInt(Beaten, marks.Beaten);
        }
    }

    /// <summary>
    ///     Away, the adventurer of this guest's bout is back in its town in the host's game: this game, which dropped
    ///     its copy when it left the bout, takes the host's (GitHub issue 86: Misaki was gone from Nefu after a won bout
    ///     until a later copy of the world)
    /// </summary>
    internal static void BackHome(AdventurerBoutDelta home)
    {
        if (home.Chara is null) {
            return;
        }

        // (told as soon as the bout is won, before this game walks out of its zone: its own copy is still there)
        if (home.TargetUid == _target) {
            _home = home;
            return;
        }

        if (Find(home.TargetUid) is { isDestroyed: false }) {
            return;
        }

        var chara = home.Chara.Decompress<Chara>();
        chara.uid = home.TargetUid;
        EClass.game.cards.globalCharas.Add(chara);
        Recache(chara);
        if (EClass.game.spatials.Find(home.ZoneUid) is Zone town) {
            chara.MoveZone(town, new ZoneTransition {
                state = ZoneTransition.EnterState.Exact,
                x = home.X,
                z = home.Z,
            });
        }

        EmpLog.Information("The adventurer {Target} of our bout is back in {Zone}", chara.uid, home.ZoneUid);
    }

    // ---------------------------------------------------------------- the host

    /// <summary>
    ///     The bout of a guest away is over and the adventurer back in its town here: that guest gets it (its game
    ///     dropped its copy, see Leave)
    /// </summary>
    private static void SendHome(ElinNetHost host, int peer, int uid, int[] rec)
    {
        // (a bout from the host's own town: the winner walks back onto this map, which brings it the adventurer)
        if (peer < 0 || !host.IsAwayPeer(peer) || rec[RecZone] == EClass._zone?.uid || Find(uid) is not { } target) {
            return;
        }

        host.SendDeltaTo(peer, new AdventurerBoutDelta {
            Kind = AdventurerBoutDelta.Home,
            TargetUid = uid,
            Chara = LZ4Bytes.Create(target),
            ZoneUid = rec[RecZone],
            X = rec[RecX],
            Z = rec[RecZ],
        });
    }

    internal static void TellMarks(int uid, int peer = -1)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host || Find(uid) is not { } chara) {
            return;
        }

        var marks = new AdventurerBoutDelta {
            Kind = AdventurerBoutDelta.Marks,
            TargetUid = uid,
            DateBout = chara.GetInt(DateBout),
            Beaten = chara.GetInt(Beaten),
        };

        if (peer >= 0) {
            host.SendDeltaTo(peer, marks);
        } else {
            host.Delta.AddRemote(marks);
        }
    }

    internal static void OnGuest(AdventurerBoutDelta delta, ElinNetHost host)
    {
        if (!Enabled || host.IsZoneSession || host.PlayerChara(delta.OriginPeer) is not { } guest) {
            return;
        }

        var bouts = ElinNetHost.AdventurerBouts;
        var uid = delta.TargetUid;
        if (delta.Kind == AdventurerBoutDelta.Start) {
            if (Find(uid) is not { } target || bouts.ContainsKey(uid) || !CanBout(target)) {
                // its game fights a copy of its own: nothing of it counts here, and it learns how things stand
                EmpLog.Information("Bout of player {Guest} with {Target} not taken: the adventurer offers none now",
                    guest.uid, uid);
                TellMarks(uid, delta.OriginPeer);
                return;
            }

            bouts[uid] = [target.currentZone.uid, target.pos.x, target.pos.z, guest.uid, 0];
            target.SetGlobal();
            target.SetInt(DateBout, EClass.world.date.GetRaw());
            // off its town for the bout, as the game's own bout takes it to the bout's zone
            target.MoveZone(EClass.game.spatials.Find("somewhere"));
            EmpLog.Information("Player {Guest} fights a bout with {Target}", guest.uid, uid);
            TellMarks(uid);
            return;
        }

        // only the one fighting it, once
        if (!bouts.TryGetValue(uid, out var rec) || rec[RecGuest] != guest.uid || rec[RecResult] != 0) {
            return;
        }

        Restore(uid, rec);
        if (delta.Kind == AdventurerBoutDelta.Won && Find(uid) is { } beaten) {
            beaten.SetInt(Beaten, beaten.GetInt(Beaten) + 1);
            beaten.ModAffinity(guest, 10, false);
            // the reward waits for the guest on this map
            rec[RecResult] = AdventurerBoutDelta.Won;
        } else {
            bouts.Remove(uid);
            SendHome(host, delta.OriginPeer, uid, rec);
        }

        EmpLog.Information("Player {Guest} bout with {Target}: {Kind}", guest.uid, uid, delta.Kind);
        TellMarks(uid);
    }

    /// <summary>
    ///     The two ways the game's dialog offers a bout (DramaCustomSequence): a unique or wandering character never
    ///     beaten yet, before it can be recruited; an adventurer once a week
    /// </summary>
    private static bool CanBout(Chara c)
    {
        if (c.IsPC || c.IsRemotePlayer || c.IsPCFaction || c.isDead || c.currentZone is not { IsInstance: false }) {
            return false;
        }

        var date = EClass.world.date.GetRaw();
        return ((c.trait.IsUnique || c.IsGlobal) && c.GetInt(Beaten) == 0 && c.trait.NeedBoutToJoin &&
                c.affinity.CanInvite() && c.c_bossType == BossType.none) ||
               (c.trait.CanBout && c.IsGlobal && c.GetInt(DateBout) + 10080 < date);
    }

    private static void Recache(Chara c)
    {
        CardCache.Set(c);
        foreach (var thing in c.things.Flatten()) {
            CardCache.Set(thing);
        }
    }

    /// <summary>
    ///     The adventurer back where it stood, healed and friendly, as the game's ZoneInstanceBout.OnLeaveZone does
    /// </summary>
    private static void Restore(int uid, int[] rec)
    {
        if (Find(uid) is not { } c) {
            return;
        }

        if (c.isDead) {
            c.Revive();
        }

        c.HealAll();
        c.hostility = c.c_originalHostility = Hostility.Friend;
        c.SetEnemy();
        c.SetAI(new NoGoal());
        if (EClass.game.spatials.Find(rec[RecZone]) is { } home && c.currentZone != home) {
            c.MoveZone(home, new ZoneTransition {
                state = ZoneTransition.EnterState.Exact,
                x = rec[RecX],
                z = rec[RecZ],
            });
        }
    }

    /// <summary>
    ///     The host, every frame: a won bout's reward to its guest once it stands on this map; a bout whose guest
    ///     dropped out (or from a save made during it) puts the adventurer back
    /// </summary>
    internal static void Update()
    {
        var bouts = ElinNetHost.AdventurerBouts;
        if (bouts.Count == 0 || NetSession.Instance.Connection is not ElinNetHost { IsZoneSession: false } host) {
            return;
        }

        foreach (var (uid, rec) in bouts.ToArray()) {
            var peer = host.PeerOfChara(rec[RecGuest]);
            if (rec[RecResult] != AdventurerBoutDelta.Won) {
                if (peer < 0) {
                    EmpLog.Information("Bout with {Target}: its player is gone, the adventurer goes back", uid);
                    Restore(uid, rec);
                    bouts.Remove(uid);
                    TellMarks(uid);
                }

                continue;
            }

            // travelling alone, the winner goes back to a town of its own and never stands on this map: the reward
            // comes by letter, as what the host gives any player who is away (GitHub issue 79)
            // (not a bout from the host's own town: its winner walks back here, in its zone of the bout for now)
            if (peer >= 0 && host.IsAwayPeer(peer) && rec[RecZone] != EClass._zone?.uid) {
                bouts.Remove(uid);
                if (Find(uid) is { } beaten) {
                    RewardByLetter(beaten, rec[RecGuest]);
                }

                // (after the reward left it)
                SendHome(host, peer, uid, rec);

                continue;
            }

            if (peer < 0 || !host.IsSettled(peer) || !host.ActiveRemoteCharas.TryGetValue(peer, out var guest) ||
                !EClass._map.charas.Contains(guest)) {
                continue;
            }

            bouts.Remove(uid);
            if (Find(uid) is { } target) {
                Reward(target, guest);
            }
        }
    }

    /// <summary>
    ///     The game's reward of a won bout (ZonePreEnterBoutWin), given by the host: the most precious piece the
    ///     adventurer wears and was not given, or some platinum
    /// </summary>
    private static void RewardByLetter(Chara target, int guestUid)
    {
        Recache(target);
        Thing best;
        using (ElinDelta.Simulate()) {
            best = TakeBest(target);
        }

        if (!Mailbox.Post(guestUid, best, "emp_bout_reward_mail".Loc(target.NameSimple), target.NameSimple)) {
            // (no letter could be written: the adventurer keeps it, in its bag)
            using (ElinDelta.Simulate()) {
                if (best.id != "plat") {
                    target.AddThing(best);
                }
            }

            EmpLog.Warning("Bout reward {Thing} from {Target} to player {Guest}: no letter, not given", best.id, target.uid, guestUid);
            return;
        }

        EmpLog.Information("Bout reward {Thing} from {Target} to player {Guest}, away: by letter", best.id, target.uid, guestUid);
    }

    // the most precious piece the adventurer wears and was not given, taken off it, or some platinum
    private static Thing TakeBest(Chara target)
    {
        Thing? best = null;
        var price = 0;
        foreach (var thing in target.things) {
            if (thing.isEquipped && !thing.isGifted && (thing.GetPrice() > price || thing.rarity >= Rarity.Artifact)) {
                best = thing;
                price = thing.GetPrice();
            }
        }

        if (best is null) {
            return ThingGen.Create("plat").SetNum(EClass.rndHalf(target.LV / 10 + 2));
        }

        target.RemoveCard(best);
        target.EQ_CAT(best.category.id);
        return best;
    }

    private static void Reward(Chara target, Chara guest)
    {
        // the host's adventurer and its gear are often out of the card table after a bout (3 adventurers of 3 on the
        // bench, with 0 or 1 of their 8 to 10 worn things): what is said about an uncached card is dropped before it
        // goes out (CardAddThingDelta.OnRefresh finds nothing), the guest never got the reward, only the bag check
        // brought it some 25 s later (GitHub issue 62, bench 2026-11-02). This game's adventurer is the real one
        Recache(target);

        // the real item changes hands here; the host's own patches tell everyone, as for a trade
        var picked = PlayerTrade.HasRoom(guest, 1, 0);
        Thing best;
        using (ElinDelta.Simulate()) {
            best = TakeBest(target);
            if (picked) {
                guest.AddThing(best, false);
            } else {
                EClass._zone.AddCard(best, guest.pos);
            }
        }

        // the game picks it up in front of the winner ("You pick up ...", ZonePreEnterBoutWin); given silently, the
        // guest saw no reward (GitHub issue 62). Worded by this game for its own player: "You", as the guest reads it
        using (MsgRelayContext.RedirectTo(guest)) {
            if (picked) {
                EClass.pc.Say("pick_thing", EClass.pc, best);
            } else {
                EClass.pc.Say("backpack_full_drop", best);
            }
        }

        EmpLog.Information("Bout reward {Thing} from {Target} to player {Guest}", best.id, target.uid, guest.uid);
    }
}
