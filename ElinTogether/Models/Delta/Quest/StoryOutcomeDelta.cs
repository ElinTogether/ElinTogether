using System;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

/// <summary>
///     To the host: a dialog triggered this in the world (a fight set up, someone moving in, land claimed)
/// </summary>
[MessagePackObject]
public class StoryOutcomeDelta : ElinDelta
{
    internal override bool InSendersStory => true;

    [Key(0)]
    public required string Method { get; init; }

    /// <summary>
    ///     Who the player was talking to
    /// </summary>
    [Key(1)]
    public required RemoteCard? Target { get; init; }

    // the same outcome of the same dialog, from the same player: once in this time (a real dialog is not replayed
    // faster; a modified game repeated a reward as often as it liked)
    private const float Cooldown = 600f;
    private const int ReachTiles = 6;
    private static readonly System.Collections.Generic.Dictionary<(int Peer, string Method, int Target), float> LastRun = [];

    /// <summary>
    ///     Security audit 2026-10-27 (item 5): a modified game could run any dialog outcome on any character from
    ///     anywhere (recruit a unique into its base, repeat a reward). The sender must stand by who it talks to (unless
    ///     it plays away, in its own zones), a recruit must be one the base offers or a common resident, and the same
    ///     outcome of the same dialog runs once in ten minutes
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (NoCharacter(sender) is { } none) {
            return none;
        }

        var target = FindTarget();
        // (each player or team living its own story: an actor of the story counts where the sender's story shows it,
        // read here inside that story, TeamOwnStory.As)
        if (target is not null && !Helper.TeamOwnStory.Shown(target)) {
            return $"outcome {Method} on {target.id}, not in the sender's story there";
        }

        var hire = target is not null && EClass.Branch?.listRecruit.Exists(h => h.chara == target) == true;
        if (!host.IsAwayPeer(OriginPeer) && target is not null && !hire &&
            (target.currentZone != sender!.currentZone || target.pos.Distance(sender.pos) > ReachTiles)) {
            return $"outcome {Method} on {target.uid}, out of the sender's reach";
        }

        if (Method == StoryOutcomePatch.Recruit && target is not null && !hire &&
            (target.IsUnique || target.IsPCFaction || target.IsPC || target.GetBool("remote_chara"))) {
            return $"recruit of {target.uid} ({target.id}), not one the base offers";
        }

        var now = UnityEngine.Time.realtimeSinceStartup;
        var key = (OriginPeer, Method, target?.uid ?? 0);
        if (LastRun.TryGetValue(key, out var last) && now - last < Cooldown) {
            return $"outcome {Method} on {target?.uid ?? 0} again after {(int)(now - last)} s";
        }

        LastRun[key] = now;
        return null;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host) {
            return;
        }

        var away = host.IsAwayPeer(OriginPeer);
        if (!StoryOutcomePatch.RunsOnHost(Method, away)) {
            EmpLog.Warning("Refusing story outcome {Method} from peer {PeerIndex}", Method, OriginPeer);
            return;
        }

        var target = FindTarget();

        // travelling alone, the player ran it in its copy of the world and got what it gives there
        using (away ? QuestRewardPatch.GiveNothing(host.PlayerChara(OriginPeer)) : QuestRewardPatch.GiveTo(host, OriginPeer)) {
            using (Simulate()) {
                try {
                    Run(target, away, host.PlayerChara(OriginPeer));
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Story outcome {Method} failed", Method);
                }
            }
        }

        EmpLog.Debug("Ran story outcome {Method} for peer {PeerIndex}", Method, OriginPeer);
    }

    private void Run(Chara? target, bool away, Chara? sender)
    {
        switch (Method) {
            case nameof(DramaOutcome.revive_pet) when sender is not null && sender != pc:
                RevivePets(sender, target);
                return;
            case StoryOutcomePatch.Recruit:
                if (target is not null && EClass.Branch is { } branch) {
                    target.SetBool(18, true);
                    branch.Recruit(target);
                }

                return;
            case nameof(DramaOutcome.QuestExploration_AfterCrystal) when away:
                // the crystal room is on the player's map, only who goes home is about the world
                if (game.cards.globalCharas.Find("fiama") is { } fiama) {
                    fiama.MoveHome(game.StartZone);
                    fiama.RemoveEditorTag(EditorTag.AINoMove);
                }

                return;
        }

        RunGameOutcome(Method, target);
    }

    /// <summary>
    ///     GitHub issue 22: Fiama's revive_pet revives the dead pets of the base of "the player" (pc.homeBranch): run
    ///     on the host for a guest, it revived the host's pets, and the guest's kitten stayed dead (the dialog kept
    ///     telling it not to let it die). The sender's base and the sender's own companions here
    /// </summary>
    private static void RevivePets(Chara sender, Chara? fiama)
    {
        var members = sender.homeBranch?.members ?? [];
        var pets = members.Concat(game.cards.globalCharas.Values.Where(c => c.CompanionOwnerUid == sender.uid))
            .Where(c => c is { isDead: true } && c.GetInt(100) != 0)
            .Distinct()
            .ToList();
        // (next to that player and into its party, not the host's: RemoteRevivePatch stands it in for the game's "pc")
        using (Patches.CharaMakeAllyEvent.GiftsFor(sender)) {
            foreach (var pet in pets) {
                pet.GetRevived();
            }
        }

        EmpLog.Information("Fiama revived {Count} pet(s) of player character {Uid}", pets.Count, sender.uid);
        fiama?.ModAffinity(sender, -2);
    }

    /// <summary>
    ///     The game's own code, with the dialog it expects around it
    /// </summary>
    internal static void RunGameOutcome(string method, Chara? target)
    {
        var stage = new GameObject("emp_story_outcome");
        stage.SetActive(false);
        try {
            var manager = stage.AddComponent<DramaManager>();
            manager.tg = target is null ? new Person() : new Person(target);

            var outcome = stage.AddComponent<DramaOutcome>();
            outcome.manager = manager;

            AccessTools.Method(typeof(DramaOutcome), method).Invoke(outcome, null);
        } finally {
            UnityEngine.Object.Destroy(stage);
        }
    }

    /// <summary>
    ///     Who the player was talking to. Not always a card the host handed out: someone waiting to be hired
    ///     only exists in the list of the base
    /// </summary>
    private Chara? FindTarget()
    {
        if (Target is null) {
            return null;
        }

        return Target.Find() as Chara ??
               game.cards.globalCharas.Find(Target.Uid) ??
               _map.charas.Find(c => c.uid == Target.Uid) ??
               EClass.Branch?.listRecruit.Find(hire => hire.chara?.uid == Target.Uid)?.chara;
    }
}
