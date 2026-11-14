using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class AddRecipeDelta : ElinDelta
{
    [Key(0)]
    public required string RecipeId { get; init; }

    /// <summary>
    ///     Passed on by a player who keeps a map, alone or for visitors: those of that map know already
    /// </summary>
    [Key(1)]
    public bool Relayed { get; init; }

    /// <summary>
    ///     The character of the player who learnt it (host option "Teams learn on their own": only its team learns
    ///     it). 0 from an older game
    /// </summary>
    [Key(2)]
    public int Learner { get; init; }

    /// <summary>
    ///     Learnt from the story (a story quest, a dialog): every team learns it
    /// </summary>
    [Key(3)]
    public bool Story { get; init; }

    /// <summary>
    ///     True while a recipe that came from someone else is being learnt here, or one that is told another way
    /// </summary>
    internal static bool IsLanding { get; set; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        // a recipe of the game (security audit 2026-10-27, item 10: any id went into every team's book)
        RecipeManager.BuildList();
        return RecipeManager.Get(RecipeId) is null ? $"recipe {RecipeId} is no recipe of the game" : TeamKnowledge.CheckLearner(Learner, sender);
    }

    protected override void OnApply(ElinNetBase net)
    {
        // who learnt it: the sender itself on the host of the world, else as told (the visitor of a zone the sender
        // keeps); nobody in particular for the story, every team learns it
        // (each team living its own story: the story's recipe is its learner's, issues 25 and 31)
        var learner = Story && !Helper.TeamOwnStory.On ? 0 : net is ElinNetHost { IsZoneSession: false } world && !Relayed && world.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender)
                ? sender.uid
                : Learner;

        // never back to the player it came from, never twice to anyone: learning a known recipe again counts
        // it twice
        switch (net) {
            // the game that keeps a map for visitors: they hear it from here, the world from its own link
            case ElinNetHost { IsZoneSession: true } zone:
                TeamKnowledge.Tell(zone, this, learner, OriginPeer);
                if (NetSession.Instance.Transport is ElinNetClient main) {
                    main.SendWhileAway(new AddRecipeDelta { RecipeId = RecipeId, Relayed = true, Learner = learner, Story = Story });
                }

                break;
            // the players of that map know already: the others, on this map, hear it now; those alone elsewhere
            // get the recipes with their next world copy
            case ElinNetHost host when Relayed:
                TeamKnowledge.Tell(host, this, learner, OriginPeer, true);
                break;
            case ElinNetHost host:
                TeamKnowledge.Tell(host, this, learner, OriginPeer);
                break;
        }

        // teams learning on their own: the host keeps it in the learner's team's book, and learns it only for its own
        // team (a guest is told only its team's)
        if (net is ElinNetHost) {
            TeamKnowledge.Learnt(learner, RecipeId);
            if (!TeamKnowledge.Shares(learner, pc.uid)) {
                EmpLog.Debug("Recipe {RecipeId} learnt by player {Learner} of another team", RecipeId, learner);
                return;
            }
        }

        EmpLog.Debug("Recipe {RecipeId} learnt from peer {Peer}, relayed {Relayed}", RecipeId, OriginPeer, Relayed);
        IsLanding = true;
        try {
            player.recipes.Add(RecipeId, !player.recipes.IsKnown(RecipeId));
        } finally {
            IsLanding = false;
        }
    }
}
