using ElinTogether.Helper;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     What a story dialog triggers in the world happens on the host, whoever is talking. Gifts are not listed
///     here: they are created by the host one by one, see <see cref="Helper.StoryGifts" />
/// </summary>
[HarmonyPatch]
internal static class StoryOutcomePatch
{
    internal const string Recruit = "emp_recruit";

    /// <summary>
    ///     About the map the player and the host stand on, or the world
    /// </summary>
    private static readonly string[] _world = [
        nameof(DramaOutcome.OnClaimLand),
        nameof(DramaOutcome.PutOutFire),
        nameof(DramaOutcome.Tutorial1),
        nameof(DramaOutcome.QuestDefense_0),
        nameof(DramaOutcome.QuestDefense_1),
        nameof(DramaOutcome.QuestDebt_reward),
        nameof(DramaOutcome.QuestExploration_MeetFarris),
        nameof(DramaOutcome.QuestExploration_MeetFarris2),
        nameof(DramaOutcome.QuestExploration_AfterCrystal),
        nameof(DramaOutcome.QuestExploration_AfterComplete),
        nameof(DramaOutcome.revive_pet),
        nameof(DramaOutcome.event_swordkeeper),
        nameof(DramaOutcome.event_az),
        nameof(DramaOutcome.event_az2),
        nameof(DramaOutcome.event_az3),
        nameof(DramaOutcome.reward_stone_dream),
    ];

    /// <summary>
    ///     About the world only, nothing on the map the dialog happens on: the host repeats them for a player
    ///     travelling alone
    /// </summary>
    private static readonly HashSet<string> _worldOnly = [
        nameof(DramaOutcome.OnClaimLand),
        nameof(DramaOutcome.QuestExploration_MeetFarris),
        nameof(DramaOutcome.QuestExploration_AfterCrystal),
        nameof(DramaOutcome.QuestExploration_AfterComplete),
        nameof(DramaOutcome.reward_stone_dream),
    ];

    private static readonly string[] _hire = [
        nameof(DramaOutcome.chara_hired),
        nameof(DramaOutcome.chara_hired_ticket),
    ];

    internal static bool RunsOnHost(string method, bool away)
    {
        return away ? _worldOnly.Contains(method) : method == Recruit || _world.Contains(method);
    }

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return _world.Concat(_hire).Select(name => AccessTools.Method(typeof(DramaOutcome), name));
    }

    [HarmonyPrefix]
    internal static bool OnOutcome(DramaOutcome __instance, MethodBase __originalMethod)
    {
        var name = __originalMethod.Name;
        if (ElinDelta.IsApplying) {
            return name != nameof(DramaOutcome.OnClaimLand) || ClaimLandOnce(__instance);
        }
        var target = __instance.manager?.tg?.chara;

        // what the dialog changed so far (the stage of the debt) is to reach the host before this
        DialogFlagSync.TellChanges();
        SharedQuests.TellChanges();

        if (_hire.Contains(name)) {
            // the player pays here, the resident joins the base on the host
            if (NetSession.Instance.Connection is ElinNetClient hiring) {
                hiring.Delta.AddRemote(new StoryOutcomeDelta {
                    Method = Recruit,
                    Target = target,
                });
            }

            return true;
        }

        var delta = new StoryOutcomeDelta {
            Method = name,
            Target = target,
        };

        if (NetSession.Instance.Connection is ElinNetClient client) {
            client.Delta.AddRemote(delta);
            return false;
        }

        if (_worldOnly.Contains(name)) {
            QuestAwaySync.Send(delta);
        }

        return name != nameof(DramaOutcome.OnClaimLand) || ClaimLandOnce(__instance);
    }

    private static readonly string[] _claimQuests = ["sharedContainer", "crafter", "defense"];

    /// <summary>
    ///     DramaOutcome.OnClaimLand made idempotent: Ash's three home quests are world quests (one log for everyone,
    ///     only main/exploration/debt are each story's), and QuestHome is the world's. With "each player or team lives
    ///     its own story" every new story claims the land with Ash again (issue 67): the game's code added the three
    ///     quests once more to everyone's log, the host saw its finished starter quests offered again (and a second
    ///     tax quest after the second shipping box), and the home quest went back to phase 2. Each is added only when
    ///     the world has never had it, the home quest only moves forward. False: the game's code is not run
    /// </summary>
    private static bool ClaimLandOnce(DramaOutcome outcome)
    {
        var quests = EClass.game.quests;
        var ash = EClass.game.cards.globalCharas.Find("ashland");
        foreach (var id in _claimQuests) {
            if (!quests.IsAdded(id)) {
                quests.globalList.Add(Quest.Create(id).SetClient(ash, assignQuest: false));
            }
        }

        if (quests.Get<QuestHome>() is { phase: < 2 } home) {
            home.ChangePhase(2);
        }

        outcome.AddMaid();
        return false;
    }
}
