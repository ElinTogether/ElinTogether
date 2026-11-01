using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine.Events;

namespace ElinTogether.Net;

/// <summary>
///     A world that changes hands (the save depot): the game loads it with the character of whoever saved it
///     last as the local one, and the player who takes it over would play that character, with its bag and gold,
///     while its own stays a character of the save <br />
///     The save remembers whose its local character is. Loaded by another player who has a character in it, the two
///     are exchanged before anything is played: the one who takes the world plays its own, the former one becomes
///     the character of an absent player, as any player who left, with its standing, its random quests and its
///     companions kept for its return. Nothing is copied, only who is the local character changes <br />
///     A world last hosted before this was written does not know whose its character is: nothing is exchanged
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     The player whose character is the local one of this save -> that character's uid (one entry)
    /// </summary>
    [ElinGameIOProperty("pc_owner")]
    private static Dictionary<ulong, int> PcOwners
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Who plays in this game. Debug windows sharing one Steam account are the identity the host knows them by,
    ///     see SteamNetPeer.UseDevIdentity
    /// </summary>
    internal static ulong LocalUserId => LocalUser;

    private static ulong LocalUser =>
#if DEBUG
        EmpConfig.Dev.Identity.Value != 0 ? SteamNetPeer.DevIdentityBase + (ulong)EmpConfig.Dev.Identity.Value :
#endif
        ServerAvatar.HostUser;

    /// <summary>
    ///     Former local characters whose player is not known (a world last hosted before the owner was written): the
    ///     player who comes without a character of its own gets it back. Uid -> 1
    /// </summary>
    [ElinGameIOProperty("pc_orphan")]
    private static Dictionary<int, int> PcOrphans
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Whose the local character of this save is, 0 when the save does not tell: nothing written, written for
    ///     another character, or for a player who has a character of its own in this world (what 0.26.494 wrote
    ///     when a world taken over was hosted)
    /// </summary>
    private static ulong PcOwner()
    {
        if (PcOwners.Count != 1) {
            return 0;
        }

        var (owner, uid) = PcOwners.First();
        return uid != player.uidChara || OwnCharaOf(owner) is not null ? 0 : owner;
    }

    /// <summary>
    ///     The character this player plays as a guest of this world, when it is not the local one
    /// </summary>
    private static Chara? OwnCharaOf(ulong user)
    {
        return SavedRemoteCharas.TryGetValue(user, out var uid) && uid != player.uidChara
            ? game.cards.globalCharas.Find(uid)
            : null;
    }

    /// <summary>
    ///     That Steam account plays a character in this world
    /// </summary>
    internal static bool IsPlayerOfWorld(ulong user)
    {
        return SavedRemoteCharas.ContainsKey(user) || PcOwners.ContainsKey(user);
    }

    /// <summary>
    ///     This world is one other players play in: one of them has a character in it, or it was another player's.
    ///     A world played alone opens no session by itself, nothing changes for a solo player (EmpAutoHost)
    /// </summary>
    internal static bool IsSharedWorld =>
        SavedRemoteCharas.Count > 0 || PcOrphans.Count > 0 || (PcOwner() is var owner && owner != 0 && owner != LocalUser);

#if DEBUG
    internal static string WhoPlaysWhom =>
        $"me {LocalUser}; pc {player.uidChara} of {PcOwner()}; " +
        string.Join(",", SavedRemoteCharas.Select(p => $"{p.Key}={p.Value}"));
#endif

    private static void SetPcOwner(ulong user)
    {
        PcOwners.Clear();
        PcOwners[user] = player.uidChara;
    }

    // (a table emptied may not be written again by the save: an entry is only believed when nobody plays that character)
    private static Chara? Orphan()
    {
        return PcOrphans.Keys.Select(uid => game.cards.globalCharas.Find(uid)).FirstOrDefault(c =>
            c is { isDead: false } && c.uid != player.uidChara && !SavedRemoteCharas.ContainsValue(c.uid));
    }

    /// <summary>
    ///     Hosting: the local character of this save is ours, unless the save says whose it is
    /// </summary>
    private static void RememberPcOwner()
    {
        if (PcOwner() == 0 && OwnCharaOf(LocalUser) is null) {
            SetPcOwner(LocalUser);
        }
    }

    /// <summary>
    ///     A player who joins without a character in this world, when a former local character waits for its player:
    ///     it is that player's (with two players it cannot be anyone else's)
    /// </summary>
    private static void GiveOrphanTo(ulong user)
    {
        if (SavedRemoteCharas.ContainsKey(user) || PcOwner() == user || Orphan() is not { } orphan) {
            return;
        }

        EmpLog.Information("The former local character {Uid} goes back to player {User}", orphan.uid, user);
        PcOrphans.Remove(orphan.uid);
        SavedRemoteCharas[user] = orphan.uid;
    }

    // the character a new player just made at the title and the world it is for: kept across the load in the
    // game's memory, never in a table of the save (it would be written in the world before being anyone's)
    private static (string World, LZ4Bytes Chara, float At)? _madeChara;

    /// <summary>
    ///     The character of a new player who loads a world that is another player's. Null while it is not made: the
    ///     game then goes to the title and opens the creation screen, and loads this world again once it is made
    /// </summary>
    private static Chara? NewCharaOf(ulong me)
    {
        // made for the load that follows: one that never came (the depot's world taken by another meanwhile) is forgotten
        if (_madeChara is not { } made || made.World != Game.id || UnityEngine.Time.realtimeSinceStartup - made.At > 180f) {
            MakeOwnChara();
            return null;
        }

        // forgotten before anything can fail: one screen, one load, never a loop of loads
        _madeChara = null;
        Chara? chara = null;
        try {
            chara = made.Chara.Decompress<Chara>();
            AdoptNewPlayerChara(chara);
            GiveAxeToRemotePlayer(chara);
        } catch (System.Exception ex) {
            // played as it is, with the character of the save: said, and the screen comes back at the next load only
            EmpLog.Warning(ex, "The character made for this world could not be taken in");
            Dialog.Ok("emp_handover_failed".Loc(chara?.Name ?? ""));
            return null;
        }

        // what a player who joins gets from MakeAlly and from its first save probe
        chara.SetGlobal();
        chara.SetFaction(EClass.Home);
        PlayerStandings[chara.uid] = [0, NewPlayerKarma];
        SavedRemoteCharas[me] = chara.uid;
        if (!PlayerRosters.TryGetValue(me, out var roster)) {
            roster = PlayerRosters[me] = [];
        }

        roster.Add(chara.uid);
        EmpLog.Information("New player {User} made its own character {Uid} for this world", me, chara.uid);
        return chara;
    }

    /// <summary>
    ///     The creation screen of the game, as ElinNetClient.OnSessionNewPlayerRequest opens it for a player who joins
    /// </summary>
    private static void MakeOwnChara()
    {
        var (world, cloud) = (Game.id, game.isCloud);
        EmpLog.Information("The world {World} is another player's and we have no character in it: making one", world);

        // the screen edits the character of a game of its own: none may be loaded. The title also gives the depot's
        // world back, so a player who never validates holds nothing
        scene.Init(Scene.Mode.Title);
        core.actionsNextFrame.Add(() => {
            ui.RemoveLayer<LayerEditBio>();
            var embark = ui.AddLayer<LayerEditBio>();
            var content = embark.GetComponentInChildren<Content>();
            content.transform.Find("Mode").SetActive(false);

            // closed without this click: the player stays at the title as after the game's own "New Game", nothing
            // is kept and nothing is loaded
            var button = content.transform.Find("ButtonEmbark")!.GetComponentInChildren<UIButton>();
            button.onClick.SetPersistentListenerState(0, UnityEventCallState.Off);
            button.onClick.AddListener(() => {
                _madeChara = (world, LZ4Bytes.Create(pc), UnityEngine.Time.realtimeSinceStartup);
                game.Kill();
                ui.RemoveLayer(embark);
                core.game = null;
                core.actionsNextFrame.Add(() => {
                    LayerTitle.KillActor();
                    // loaded again the way it was loaded. The depot's world is taken again: its lock went back at
                    // the title, so it is never held twice, and a player who took it meanwhile is said
                    if (world == SaveDepot.WorldId && !cloud && SaveDepot.Enabled) {
                        SaveDepot.Take();
                    } else {
                        Game.Load(world, cloud);
                    }
                });
            });
        });
    }

    private static Chara? Giver(Quest quest)
    {
        return quest.person?.uidChara is { } uid and not 0 ? game.cards.globalCharas.Find(uid) : null;
    }

    /// <summary>
    ///     The world just loaded was another player's: our character becomes the local one. True when exchanged, the
    ///     world is then saved and loaded again so that the game starts from it as from any save
    /// </summary>
    private static bool TakeOverPc()
    {
        var me = LocalUser;
        var formerOwner = PcOwner();
        if (formerOwner == me) {
            return false;
        }

        // our own character in this world: the one we played as a guest, else a former local character nobody claims
        var mine = OwnCharaOf(me) ?? (SavedRemoteCharas.ContainsKey(me) ? null : Orphan());
        if (mine is null) {
            if (SavedRemoteCharas.ContainsKey(me)) {
                return false;
            }

            // nobody's by the save and we have no other: it is ours (the host back on a world of before)
            if (formerOwner == 0) {
                SetPcOwner(me);
                return false;
            }

            // another player's, and we never played here: a new player, who makes its own as one who joins does
            mine = NewCharaOf(me);
            if (mine is null) {
                return false;
            }
        }

        if (mine.isDead || pc.party is not { } party) {
            EmpLog.Warning("Our character {Mine} cannot take the place of the local one (dead: {Dead})", mine.uid, mine.isDead);
            Dialog.Ok("emp_handover_failed".Loc(mine.Name));
            return false;
        }

        // the save as it was, kept with the game's own backups
        try {
            GameIO.MakeBackup(new GameIndex { id = Game.id, cloud = game.isCloud });
        } catch (System.Exception ex) {
            // no copy to come back to: nothing is exchanged
            EmpLog.Warning(ex, "No backup before the exchange of characters, nothing exchanged");
            Dialog.Ok("emp_handover_failed".Loc(mine.Name));
            return false;
        }

        // (a server's stand-in left in the world by a crash goes with the exchange, see ServerAvatar)
        var standIn = ServerAvatar.Is(pc) ? pc : null;
        Exchange(mine, formerOwner, me);
        DropStandInAfterTakeOver(standIn);
        return true;
    }

    /// <summary>
    ///     <paramref name="mine" /> (owned by <paramref name="me" />) becomes the local character in place of the one
    ///     there now, which becomes the character of the absent player <paramref name="formerOwner" /> (0: not
    ///     known, it waits for whoever comes without a character, see GiveOrphanTo). Nothing is copied, only who is
    ///     the local character changes: fame, karma and random quests go to the books of whoever stops being it,
    ///     the companions of the leader are tagged to it, and the two characters swap places. Both directions of a
    ///     handover share it: the player who takes a world over (TakeOverPc) and the server that stops playing
    ///     (ServerAvatar). The caller has checked that <paramref name="mine" /> is alive and the local one has a party
    /// </summary>
    private static void Exchange(Chara mine, ulong formerOwner, ulong me)
    {
        var party = pc.party!;
        var former = pc;
        EmpLog.Information("The world was {Owner}'s (0: not known): playing our own character {Mine} in place of {Former}",
            formerOwner, mine.uid, former.uid);

        // fame, karma and random quests: the save holds the former player's, the books hold ours
        if (PlayerStandings.TryGetValue(mine.uid, out var standing) && standing.Length >= 2) {
            PlayerStandings[former.uid] = [player.fame, player.karma];
            PlayerStandings.Remove(mine.uid);
            player.fame = standing[StandingFame];
            player.karma = standing[StandingKarma];

            var theirs = PersonalLogOf(former.uid);
            foreach (var quest in game.quests.list.Where(q => q.IsRandomQuest).ToArray()) {
                theirs[quest.uid] = LZ4Bytes.Create(quest).Bytes;
                game.quests.list.Remove(quest);
                if (Giver(quest) is { } giver && giver.quest?.uid == quest.uid) {
                    giver.quest = null;
                }
            }

            foreach (var bytes in PersonalLogOf(mine.uid).Values) {
                // as PersonalQuests.Restore does for a client: who it is for is looked up again in this world
                var quest = new LZ4Bytes { Bytes = bytes }.Decompress<Quest>();
                game.quests.list.Insert(0, quest);
                if (Giver(quest) is { } giver) {
                    giver.quest = quest;
                }
            }

            PersonalQuestLogs.Remove(mine.uid);
        }

        // the companions of the leader have no owner written: they stay the former player's, and wait with it
        foreach (var companion in CompanionHelper.CompanionsOf(former)) {
            companion.SetCompanionOwner(former);
            companion.currentZone = null;
        }

        // ours come back with us, where the former character stood
        if (!party.members.Contains(mine)) {
            party.members.Add(mine);
            party.uidMembers.Add(mine.uid);
            mine.party = party;
        }

        party.SetLeader(mine);
        foreach (var arriving in CompanionHelper.CompanionsOf(mine).Append(mine)) {
            arriving.currentZone = former.currentZone;
            arriving.pos.Set(former.pos);
            if (arriving.global is { } global) {
                global.transition = null;
            }
        }

        // what our character rides or carries stands where it does (the game takes a mount off the tile it says)
        foreach (var rider in new[] { mine.ride, mine.parasite, mine.host }) {
            rider?.pos.Set(former.pos);
        }

        mine.homeZone ??= former.homeZone ?? EClass.pc.homeZone;

        // the former character is now the one of an absent player
        party.members.Remove(former);
        party.uidMembers.Remove(former.uid);
        former.party = null;
        former.currentZone = null;
        former.SetBool("remote_chara", true);
        // it has its own things: not the axe a new player is given
        former.SetBool("remote_axe_given", true);
        mine.SetBool("remote_chara", false);
        former.SetInt(CINT.IsPC, 0);
        mine.SetInt(CINT.IsPC, 1);

        player.uidChara = mine.uid;
        player.chara = mine;

        SavedRemoteCharas.Remove(me);
        PcOrphans.Remove(mine.uid);
        if (formerOwner == 0) {
            // whose it is will be known when its player comes back, see GiveOrphanTo
            PcOrphans[former.uid] = 1;
        } else {
            SavedRemoteCharas[formerOwner] = former.uid;
            if (!PlayerRosters.TryGetValue(formerOwner, out var roster)) {
                roster = PlayerRosters[formerOwner] = [];
            }

            if (!roster.Contains(former.uid)) {
                roster.Add(former.uid);
            }
        }

        SetPcOwner(me);

        // the players' characters uploaded from their games carry their own copy of the party, led by the local
        // character of that time: they follow the one that takes the seat (PartyRepair)
        PartyRepair.Repoint(former.uid, mine);
    }
}
