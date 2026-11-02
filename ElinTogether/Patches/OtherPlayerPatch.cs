using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Game code that reads "the player" where it means the character at hand, run for another player
///     (audit after the hallucination report, 2026-10-15)
/// </summary>
[HarmonyPatch]
internal static class OtherPlayerPatch
{
    private static bool IsMultiplayer => NetSession.Instance.Transport is not null;

    /// <summary>
    ///     Gene gear asks the feat points of "the player": for another player's equipment, those of the local one
    ///     (a guest with points could not wear a gene the host has no points for, in the host's game). The owner's
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(CharaBody), nameof(CharaBody.IsEquippable))]
    internal static IEnumerable<CodeInstruction> OnIsEquippable(IEnumerable<CodeInstruction> instructions)
    {
        var getPc = AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.pc));
        var getFeat = AccessTools.PropertyGetter(typeof(Card), nameof(Card.feat));
        var owner = AccessTools.Field(typeof(CharaBody), nameof(CharaBody.owner));
        var list = instructions.ToList();
        for (var i = 0; i < list.Count - 1; i++) {
            if (list[i].Calls(getPc) && list[i + 1].Calls(getFeat)) {
                var labels = list[i].labels;
                list[i] = new CodeInstruction(OpCodes.Ldarg_0) { labels = labels };
                list.Insert(i + 1, new CodeInstruction(OpCodes.Ldfld, owner));
                break;
            }
        }

        return list;
    }

    /// <summary>
    ///     An item blessed by a god gives its global bonus only to the faith of "the player": another player's item
    ///     was judged by the local player's god (in the host's game, the guest's blessed gear counted if the host
    ///     prayed to its god, not if the guest did). The faith of the player who wears it, or whose companion does
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ElementContainerFaction), nameof(ElementContainerFaction.IsEffective))]
    internal static bool OnIsEffective(Thing t, ref bool __result)
    {
        if (!IsMultiplayer || t.c_idDeity.IsEmpty() || t.parent is not Chara wearer ||
            CompanionHelper.OwnerOf(wearer) is not { } owner || owner == EClass.pc) {
            return true;
        }

        __result = t.c_idDeity == owner.idFaith;
        return false;
    }

    /// <summary>
    ///     An area spell spares friends by the caster's control magic (302) and its feat (1214); a companion of "the
    ///     player" uses the player's too, and learns as a companion (five times the player's experience). Cast by another
    ///     player, the local player's were added and it learnt as a companion; cast by another player's companion, the
    ///     local player's were used. The caster's own as a player, and its owner's for a companion
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(ActEffect), nameof(ActEffect.DamageEle))]
    internal static IEnumerable<CodeInstruction> OnDamageEle(IEnumerable<CodeInstruction> instructions)
    {
        var getPc = AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.pc));
        var isPc = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC));
        var list = instructions.ToList();
        // the friendly fire block ends with the feat read on "the player"; the "is the player" after it are crimes
        var last = list.FindLastIndex(c => c.LoadsConstant(1214));
        for (var i = 0; i <= last; i++) {
            if (list[i].Calls(getPc) && (list[i + 1].LoadsConstant(302) || list[i + 1].LoadsConstant(1214))) {
                list[i] = new CodeInstruction(OpCodes.Ldarg_0) { labels = list[i].labels };
                list.Insert(i + 1, CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(MasteryOf)));
                last++;
            } else if (list[i].Calls(isPc)) {
                list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CompanionAllyLimitPatch), nameof(CompanionAllyLimitPatch.IsAnyPlayer))) {
                    labels = list[i].labels,
                };
            }
        }

        return list;
    }

    // whose control magic a companion uses: its player's
    internal static Chara MasteryOf(Card caster)
    {
        return IsMultiplayer && caster is Chara chara && CompanionHelper.OwnerOf(chara) is { } owner ? owner : EClass.pc;
    }

    /// <summary>
    ///     The fate feat (1220) saves from death a character with enough stamina: half for "the player", two thirds for
    ///     anyone else. In the game that decides a guest's death, the guest had the stricter one. A player's own half
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static IEnumerable<CodeInstruction> OnDamageHP(IEnumerable<CodeInstruction> instructions)
    {
        var isPc = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC));
        var list = instructions.ToList();
        var fate = list.FindIndex(c => c.LoadsConstant(1220));
        var at = fate < 0 ? -1 : list.FindIndex(fate, c => c.Calls(isPc));
        if (at >= 0 && at - fate < 20) {
            list[at] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CompanionAllyLimitPatch), nameof(CompanionAllyLimitPatch.IsAnyPlayer))) {
                labels = list[at].labels,
            };
        }

        return list;
    }

    /// <summary>
    ///     A weak spot read in the codex adds up to 10% to the blows of "the player" only. The codex is shared
    ///     (<see cref="CodexPatch" />): another player's blows get it too
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static IEnumerable<CodeInstruction> OnDamageHPWeakspot(IEnumerable<CodeInstruction> instructions)
    {
        var isPc = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC));
        var codex = AccessTools.Field(typeof(Player), nameof(Player.codex));
        var list = instructions.ToList();
        var first = list.FindIndex(c => c.LoadsField(codex));
        var at = first < 0 ? -1 : list.FindLastIndex(first, c => c.Calls(isPc));
        if (at >= 0 && first - at < 6) {
            list[at] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CompanionAllyLimitPatch), nameof(CompanionAllyLimitPatch.IsAnyPlayer))) {
                labels = list[at].labels,
            };
        }

        // teams learning on their own: the weak spot is read in the striker's team's codex, not the local team's
        // (the codex reads before the kill's AddKill, which CodexPatch counts for the killer's team)
        var getPlayer = AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.player));
        var addKill = AccessTools.Method(typeof(CodexManager), nameof(CodexManager.AddKill));
        var kill = list.FindIndex(c => c.Calls(addKill));
        var origin = AccessTools.Method(typeof(Card), nameof(Card.DamageHP), [typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
            typeof(bool), typeof(Thing), typeof(Chara), typeof(int)]).GetParameters().First(p => p.Name == "origin").Position + 1;
        for (var i = 1; i < (kill < 0 ? list.Count : kill); i++) {
            if (list[i].LoadsField(codex) && list[i - 1].Calls(getPlayer)) {
                list[i - 1] = new CodeInstruction(OpCodes.Ldarg_S, (byte)origin).MoveLabelsFrom(list[i - 1]);
                list[i] = CodeInstruction.Call(typeof(TeamKnowledge), nameof(TeamKnowledge.CodexOf)).MoveLabelsFrom(list[i]);
            }
        }

        return list;
    }

    /// <summary>
    ///     A plant that dominates an enemy of a player's party becomes a minion of "the player": the host's. The
    ///     player the enemy belongs to
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GoalCombat), nameof(GoalCombat.Run), MethodType.Enumerator)]
    internal static IEnumerable<CodeInstruction> OnDominatePlant(IEnumerable<CodeInstruction> instructions)
    {
        var makeMinion = AccessTools.Method(typeof(Chara), nameof(Chara.MakeMinion));
        var list = instructions.ToList();
        for (var i = 1; i < list.Count; i++) {
            // the dominating plant is the only one made a friend, with the master picked just before
            if (list[i].Calls(makeMinion) && list[i - 1].LoadsConstant((int)MinionType.Friend)) {
                list[i] = CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(MakeMinionOf));
            }
        }

        return list;
    }

    /// <summary>
    ///     "The player" does not die while a dialog is open, nor anyone in a bout while one is. Another player's
    ///     dialog is open in its own game, where its death is not decided: its game reports it
    ///     (<see cref="NetPeerState.Talking" />)
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static IEnumerable<CodeInstruction> OnDamageHPDialog(IEnumerable<CodeInstruction> instructions)
    {
        var isPc = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC));
        var isActive = AccessTools.Method(typeof(LayerDrama), nameof(LayerDrama.IsActive));
        var instance = AccessTools.Field(typeof(LayerDrama), nameof(LayerDrama.Instance));
        var list = instructions.ToList();
        var active = list.FindIndex(c => c.Calls(isActive));
        if (active < 0 || active + 3 >= list.Count || !list[active + 3].Calls(isPc)) {
            return list;
        }

        // LayerDrama.IsActive() && IsPC -> IsTalking(this) && IsAnyPlayer(this)
        list[active + 3] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CompanionAllyLimitPatch), nameof(CompanionAllyLimitPatch.IsAnyPlayer))) {
            labels = list[active + 3].labels,
        };
        list[active] = new CodeInstruction(OpCodes.Ldarg_0) { labels = list[active].labels };
        list.Insert(active + 1, CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(IsTalking)));

        // the bout's (bool)LayerDrama.Instance, the last one before: or the victim talks in its own game
        var bout = list.FindLastIndex(active, c => c.LoadsField(instance));
        if (bout >= 0 && active - bout < 6 && list[bout + 1].opcode == OpCodes.Call) {
            list.Insert(bout + 2, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(bout + 3, CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(OrTalking)));
        }

        return list;
    }

    // in a dialog of its own game: the local player by its open dialog, another player as its game reported
    internal static bool IsTalking(Card card)
    {
        return card.IsPC ? LayerDrama.IsActive() : IsRemoteTalking(card);
    }

    internal static bool OrTalking(bool open, Card card)
    {
        return open || IsRemoteTalking(card);
    }

    private static bool IsRemoteTalking(Card card)
    {
        return IsMultiplayer && card is Chara { IsRemotePlayer: true, RemoteState: { Talking: true } };
    }

    /// <summary>
    ///     A god's element on an item works only for "the player's" faith: another player's weapon was judged by the
    ///     local player's god. The faith of the player who holds it (<see cref="Models.CharaFaithDelta" />)
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Element), nameof(Element.IsActive))]
    internal static IEnumerable<CodeInstruction> OnElementIsActive(IEnumerable<CodeInstruction> instructions)
    {
        var getPc = AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.pc));
        var list = instructions.ToList();
        var at = list.FindIndex(c => c.Calls(getPc));
        if (at >= 0) {
            list[at] = new CodeInstruction(OpCodes.Ldarg_1) { labels = list[at].labels };
            list.Insert(at + 1, CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(PlayerOf)));
        }

        return list;
    }

    /// <summary>
    ///     A summon of anyone but "the player" is at least half its caster's level (two thirds outside the party):
    ///     another player's weak summon came out stronger than the same summon of the local player. A player's floor
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(ActEffect), nameof(ActEffect.ProcAt))]
    internal static IEnumerable<CodeInstruction> OnProcAt(IEnumerable<CodeInstruction> instructions)
    {
        var isPc = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC));
        var isFaction = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPCFactionOrMinion));
        var list = instructions.ToList();
        var faction = list.FindIndex(c => c.Calls(isFaction));
        var at = faction < 0 ? -1 : list.FindLastIndex(faction, c => c.Calls(isPc));
        if (at >= 0 && faction - at < 8) {
            list[at] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CompanionAllyLimitPatch), nameof(CompanionAllyLimitPatch.IsAnyPlayer))) {
                labels = list[at].labels,
            };
        }

        return list;
    }

    /// <summary>
    ///     A finishing blow by a planter (665) grows a plant that becomes a minion of "the player" when the striker is
    ///     of the party: the host's. The player behind the striker
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static IEnumerable<CodeInstruction> OnPlanterFinish(IEnumerable<CodeInstruction> instructions)
    {
        var makeMinion = AccessTools.Method(typeof(Chara), nameof(Chara.MakeMinion));
        var list = instructions.ToList();
        var planter = list.FindIndex(c => c.LoadsConstant(665));
        for (var i = System.Math.Max(planter, 1); planter >= 0 && i < list.Count; i++) {
            if (list[i].Calls(makeMinion) && list[i - 1].LoadsConstant((int)MinionType.Friend)) {
                // (origin, the second argument)
                list.Insert(i, new CodeInstruction(OpCodes.Ldarg_2));
                list[i + 1] = CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(MakePlantOf)).MoveLabelsFrom(list[i + 1]);
                break;
            }
        }

        return list;
    }

    internal static void MakePlantOf(Chara plant, Chara master, MinionType type, Card? origin)
    {
        if (IsMultiplayer && master == EClass.pc && PlayerKarma.PlayerBehind(origin) is { } player) {
            master = player;
        }

        plant.MakeMinion(master, type);
    }

    internal static void MakeMinionOf(Chara plant, Chara master, MinionType type)
    {
        if (IsMultiplayer && master == EClass.pc && plant.ai is GoalCombat { tc: { } enemy } &&
            CompanionHelper.OwnerOf(enemy) is { } owner) {
            master = owner;
        }

        plant.MakeMinion(master, type);
    }

    /// <summary>
    ///     Luck (1660) makes the equipment a monster drops legendary: the game reads the local player's. Only the host
    ///     makes the loot: a guest's kill dropped by the host's luck. The killer's, or its owner's for a companion
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Card), nameof(Card.SpawnLoot))]
    internal static IEnumerable<CodeInstruction> OnSpawnLoot(IEnumerable<CodeInstruction> instructions)
    {
        return ReplacePcBefore(instructions, 1660, OpCodes.Ldarg_1);
    }

    /// <summary>
    ///     With the feat 1325, food that rots in a bag leaves seeds: the game reads the local player's. A guest's bag
    ///     rots in the host's game: the host's feat. The one who carries it
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Card), nameof(Card.Decay))]
    internal static IEnumerable<CodeInstruction> OnDecay(IEnumerable<CodeInstruction> instructions)
    {
        return ReplacePcBefore(instructions, 1325, OpCodes.Ldarg_0);
    }

    // "EClass.pc" read right before the element id becomes the player behind the card loaded by `load`
    private static IEnumerable<CodeInstruction> ReplacePcBefore(IEnumerable<CodeInstruction> instructions, int element, OpCode load)
    {
        var getPc = AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.pc));
        var list = instructions.ToList();
        for (var i = 0; i < list.Count - 1; i++) {
            if (list[i].Calls(getPc) && list[i + 1].LoadsConstant(element)) {
                list[i] = new CodeInstruction(load) { labels = list[i].labels };
                list.Insert(i + 1, CodeInstruction.Call(typeof(OtherPlayerPatch), nameof(PlayerOf)));
                break;
            }
        }

        return list;
    }

    // the player a card stands for: a player itself, a companion's owner, what carries an item; the local player else
    internal static Chara PlayerOf(Card? card)
    {
        var chara = card as Chara ?? card?.GetRootCard() as Chara;
        return IsMultiplayer && chara is not null && CompanionHelper.OwnerOf(chara) is { } owner ? owner : EClass.pc;
    }

    private static Chara? _gateCaster;

    /// <summary>
    ///     The gate spell takes the caster away and brings "the player's party" along: cast by the local player, also the
    ///     other players and their companions (the host's gate stole a guest's dog). Only the caster's own companions; a
    ///     guest asks the host to bring them on a map the host runs (<see cref="Models.GateFollowDelta" />)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActEffect), nameof(ActEffect.Proc), typeof(EffectId), typeof(int), typeof(BlessedState), typeof(Card), typeof(Card), typeof(ActRef))]
    internal static void OnProc(EffectId id, Card cc, out bool __state)
    {
        __state = IsMultiplayer && id == EffectId.Gate && cc is Chara { IsPC: true } && _gateCaster is null;
        if (__state) {
            _gateCaster = (Chara)cc;
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ActEffect), nameof(ActEffect.Proc), typeof(EffectId), typeof(int), typeof(BlessedState), typeof(Card), typeof(Card), typeof(ActRef))]
    internal static void OnProcEnd(bool __state)
    {
        if (__state) {
            _gateCaster = null;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.Teleport))]
    internal static bool OnTeleport(Card __instance, Point point)
    {
        if (_gateCaster is not { } caster || __instance == caster || __instance is not Chara chara) {
            return true;
        }

        if (CompanionHelper.OwnerOf(chara) != caster) {
            return false;
        }

        if (NetSession.Instance.Connection is ElinNetClient client && point.IsValid) {
            client.Delta.AddRemote(new Models.GateFollowDelta { Chara = chara, X = point.x, Z = point.z });
        }

        return true;
    }

    /// <summary>
    ///     Another player dies in this game: the game counts it as one of "the player's" pets that died (a statistic,
    ///     and once the tutorial about dead pets). Not a pet: the count stays as it was. First: on the host,
    ///     CharaDieEvent's prefix (patched before this class) plays a death caused by a guest's delta right there
    ///     (Stub_Die), a later prefix came after the death. A guest's replay calls it itself (CharaDieDelta)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static void OnDie(Chara __instance, out int __state)
    {
        __state = -1;
        if (!IsMultiplayer || !__instance.IsRemotePlayer || EClass.player?.stats is not { } stats) {
            return;
        }

        __state = stats.allyDeath;
        // (above 0 the game does not reserve the tutorial)
        if (stats.allyDeath == 0) {
            stats.allyDeath = 1;
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static void OnDieEnd(int __state)
    {
        if (__state >= 0 && EClass.player?.stats is { } stats) {
            stats.allyDeath = __state;
        }
    }

    /// <summary>
    ///     "Disband the party" sends away every member but "the player": the other players and their companions
    ///     too. Only the local player's own companions
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Party), nameof(Party.Disband))]
    internal static bool OnDisband(Party __instance)
    {
        if (!IsMultiplayer || EClass.pc is not { } pc || __instance != pc.party) {
            return true;
        }

        foreach (var item in CompanionHelper.CompanionsOf(pc)) {
            __instance.RemoveMember(item);
            if (item.homeZone != EClass._zone) {
                item.MoveZone(item.homeZone);
            }
        }

        return false;
    }

    /// <summary>
    ///     Walking the world map with "eat automatically", the game feeds every member of the party (no food given:
    ///     the best of the eater's bag). The other players and their companions eat in their own game: fed here too,
    ///     they ate twice, from the bag they carry
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.InstantEat))]
    internal static bool OnInstantEat(Chara __instance, Thing? t)
    {
        return t is not null || !IsMultiplayer || EClass.pc is not { } pc || CompanionHelper.OwnerOf(__instance) is not { } owner || owner == pc;
    }
}
