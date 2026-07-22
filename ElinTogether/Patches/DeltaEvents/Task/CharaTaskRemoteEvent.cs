using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Models;
using ElinTogether.Models.AI;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaTaskRemoteEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.SetAI))]
    internal static bool OnSetAI(Chara __instance, ref AIAct g)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        // propagate every host event and client player event
        switch (connection) {
            // we are host, assign all active client charas as remote
            case ElinNetHost host when host.ActiveRemoteCharas.Values.Contains(__instance):
            // we are client, assign all other charas as remote
            case ElinNetClient when !__instance.IsPC:
                g = GoalRemote.Default;
                break;
        }

        if (__instance.ai.GetType() == g.GetType() && g.IsNoGoal) {
            return false;
        }

        // a switch case is inevitable for the mapping layer
        TaskArgsBase args = g switch {
            // no goal/reset
            NoGoal and not GoalRemote => NoTask.Default,
            // task
            TaskClean task => TaskCleanArgs.Create(task),
            TaskCullLife task => TaskCullLifeArgs.Create(task),
            TaskCut task => TaskCutArgs.Create(task),
            TaskDig task => TaskDigArgs.Create(task),
            TaskDrawWater task => TaskDrawWaterArgs.Create(task),
            TaskDump task => TaskDumpArgs.Create(task),
            TaskHarvest task => TaskHarvestArgs.Create(task),
            TaskMine task => TaskMineArgs.Create(task),
            TaskPlow task => TaskPlowArgs.Create(task),
            TaskPourWater task => TaskPourWaterArgs.Create(task),
            TaskWater task => TaskWaterArgs.Create(task),
            // TaskBaseBuild task => TaskBaseBuildArgs.Create(task),
            // TaskBuild task => TaskBuildArgs.Create(task),
            // TaskChopWood task => TaskChopWoodArgs.Create(task),
            // TaskCraft task => TaskCraftArgs.Create(task),
            // TaskDesignation task => TaskDesignationArgs.Create(task),
            // TaskMoveInstalled task => TaskMoveInstalledArgs.Create(task),
            // TaskPoint task => TaskPointArgs.Create(task),
            // TaskQueue task => TaskQueueArgs.Create(task),
            // BaseTaskHarvest task => BaseTaskHarvestArgs.Create(task),
            // ai
            AI_ArmPillow ai => AIArmPillowArgs.Create(ai),
            AI_AttackHome ai => AIAttackHomeArgs.Create(ai),
            AI_Bladder ai => AIBladderArgs.Create(ai),
            AI_Churyu ai => AIChuryuArgs.Create(ai),
            AI_Clean ai => AICleanArgs.Create(ai),
            AI_Cook ai => AICookArgs.Create(ai),
            AI_Craft_Snowman ai => AICraftSnowmanArgs.Create(ai),
            AI_Craft ai => AICraftArgs.Create(ai),
            AI_Dance ai => AIDanceArgs.Create(ai),
            AI_Deconstruct ai => AIDeconstructArgs.Create(ai),
            AI_Drink ai => AIDrinkArgs.Create(ai),
            AI_Eat ai => AIEatArgs.Create(ai),
            AI_Equip ai => AIEquipArgs.Create(ai),
            AI_Farm ai => AIFarmArgs.Create(ai),
            AI_Fish ai => AIFishArgs.Create(ai),
            AI_PlayMusic ai => AIPlayMusicArgs.Create(ai),
            AI_OpenLock ai => AIOpenLockArgs.Create(ai),
            AI_Read ai => AIReadArgs.Create(ai),
            AI_Shear ai => AIShearArgs.Create(ai),
            AI_Slaughter ai => AISlaughterArgs.Create(ai),
            AI_Steal ai => AIStealArgs.Create(ai),
            AI_Fuck ai => AIFuckArgs.Create(ai),
            // AI_Goto ai => AIGotoArgs.Create(ai),
            // AI_GotoHearth ai => AIGotoHearthArgs.Create(ai),
            // AI_Grab ai => AIGrabArgs.Create(ai),
            // AI_Haul ai => AIHaulArgs.Create(ai),
            // AI_HaulResource ai => AIHaulResourceArgs.Create(ai),
            // AI_Idle ai => AIIdleArgs.Create(ai),
            // AI_LeaveMap ai => AILeaveMapArgs.Create(ai),
            // AI_Massage ai => AIMassageArgs.Create(ai),
            // AI_Meditate ai => AIMeditateArgs.Create(ai),
            // AI_Mofu ai => AIMofuArgs.Create(ai),
            // AI_Offer ai => AIOfferArgs.Create(ai),
            // AI_OpenGambleChest ai => AIOpenGambleChestArgs.Create(ai),
            // AI_Paint ai => AIPaintArgs.Create(ai),
            // AI_PassTime ai => AIPassTimeArgs.Create(ai),
            // AI_Practice ai => AIPracticeArgs.Create(ai),
            // AI_PracticeDummy ai => AIPracticeDummyArgs.Create(ai),
            // AI_Pray ai => AIPrayArgs.Create(ai),
            // AI_PryOpen ai => AIPryOpenArgs.Create(ai),
            // AI_ReleaseHeld ai => AIReleaseHeldArgs.Create(ai),
            // AI_SelfHarm ai => AISelfHarmArgs.Create(ai),
            // AI_Shopping ai => AIShoppingArgs.Create(ai),
            // AI_Sleep ai => AISleepArgs.Create(ai),
            // AI_TargetCard ai => AITargetCardArgs.Create(ai),
            // AI_TargetChara ai => AITargetCharaArgs.Create(ai),
            // AI_TargetThing ai => AITargetThingArgs.Create(ai),
            // AI_TendAnimal ai => AITendAnimalArgs.Create(ai),
            // AI_Torture ai => AITortureArgs.Create(ai),
            // AI_Trolley ai => AITrolleyArgs.Create(ai),
            AI_UseCrafter ai => AIUseCrafterArgs.Create(ai),
            // AI_Wait ai => AIWaitArgs.Create(ai),
            // AI_Water ai => AIWaterArgs.Create(ai),
            // AIProgress ai => AIProgressArgs.Create(ai),
            // AIWork ai => AIWorkArgs.Create(ai),
            // AIWork_Chore ai => AIWorkChoreArgs.Create(ai),
            // AIWork_Clean ai => AIWorkCleanArgs.Create(ai),
            // AIWork_Explore ai => AIWorkExploreArgs.Create(ai),
            // AIWork_Farm ai => AIWorkFarmArgs.Create(ai),
            // AIWork_Fish ai => AIWorkFishArgs.Create(ai),
            // AIWork_Lumberjack ai => AIWorkLumberjackArgs.Create(ai),
            // AIWork_Research ai => AIWorkResearchArgs.Create(ai),
            // DynamicAIAct ai => DynamicAIActArgs.Create(ai),
            // goal
            // Goal goal => GoalArgs.Create(goal),
            // GoalAutoCombat goal => GoalAutoCombatArgs.Create(goal),
            // GoalCombat goal => GoalCombatArgs.Create(goal),
            // GoalEndTurn goal => GoalEndTurnArgs.Create(goal),
            // GoalGraze goal => GoalGrazeArgs.Create(goal),
            // GoalHobby goal => GoalHobbyArgs.Create(goal),
            // GoalIdle goal => GoalIdleArgs.Create(goal),
            // GoalManualMove goal => GoalManualMoveArgs.Create(goal),
            // GoalNeeds goal => GoalNeedsArgs.Create(goal),
            // GoalRemote goal => GoalRemoteArgs.Create(goal),
            // GoalSearch goal => GoalSearchArgs.Create(goal),
            // GoalSiege goal => GoalSiegeArgs.Create(goal),
            // GoalSleep goal => GoalSleepArgs.Create(goal),
            // GoalSpot goal => GoalSpotArgs.Create(goal),
            // GoalTask goal => GoalTaskArgs.Create(goal),
            // GoalVisitorEnemy goal => GoalVisitorEnemyArgs.Create(goal),
            // GoalVisitorGuest goal => GoalVisitorGuestArgs.Create(goal),
            // GoalWait goal => GoalWaitArgs.Create(goal),
            // GoalWork goal => GoalWorkArgs.Create(goal),
            // NoGoal goal => NoGoalArgs.Create(goal),
            // progress
            // Progress_Custom progress => ProgressCustomArgs.Create(progress),
            // ProgressFish progress => ProgressFishArgs.Create(progress),
            // default
            _ => FakeTask.Default,
        };

        connection.Delta.AddRemote(new CharaTaskDelta {
            Owner = __instance,
            TaskArgs = args,
        });

        return true;
    }
}