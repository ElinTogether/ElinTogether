using System;
using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.DamageHP),
    typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card), typeof(bool), typeof(Thing), typeof(Chara),
    typeof(int))]
internal static class CardDamageHpEvent
{
    [HarmonyPrefix]
    internal static bool OnCardDamageHP(Card __instance,
                                        long dmg,
                                        int ele,
                                        int eleP,
                                        AttackSource attackSource,
                                        Card origin,
                                        bool showEffect,
                                        Thing weapon,
                                        Chara originalTarget,
                                        int resistPenetrationLevel,
                                        bool __runOriginal,
                                        out CardDamageHpDelta? __state)
    {
        __state = null;
        Depth++;

        // Harmony runs this prefix even after another one skipped the original (the server's stand-in cannot be
        // hurt): no blow to tell then, or the other games played it on the stand-in (Stub_DamageHP has no prefix)
        if (!__runOriginal) {
            return false;
        }

        if (PlayerDuel.JustEnded(__instance, origin)) {
            return false;
        }

        if (SleepSynchronizationContext.Sheltered(__instance)) {
            return false;
        }

        // simply drop the update as clients and wait for delta
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        // when clients took damage, let host know
        // we don't execute on client side
        if (connection.IsHost || __instance.IsPC) {
            __state = new() {
                Owner = __instance,
                Dmg = dmg,
                Ele = ele,
                EleP = eleP,
                AttackSource = attackSource,
                Origin = origin,
                ShowEffect = showEffect,
                Weapon = weapon,
                OriginalTarget = originalTarget,
                ResistPenetrationLevel = resistPenetrationLevel,
            };
            connection.Delta.DeferRemote(__state);
        }

        return connection.IsHost;
    }

    /// <summary>
    ///     Inside the game's damage code when above 0, see CharaAddConditionEvent. Also raised around the replay
    ///     of a blow (CardDamageHpDelta), which runs the game's code without this prefix
    /// </summary>
    internal static int Depth { get; set; }

    [HarmonyFinalizer]
    internal static void OnCardDamageHPEnd()
    {
        Depth--;
    }

    [HarmonyPostfix]
    internal static void CaptureResolvedHp(Card __instance, CardDamageHpDelta? __state)
    {
        // deferred
        if (__state is not null && NetSession.Instance.Connection is ElinNetHost) {
            __state.HpAfter = __instance.hp;
        }
    }

    extension(Card card)
    {
        /// <summary>
        ///     The blow a guest's game reports on its own character runs here on the host, and every blow the host
        ///     settled runs here on the guests. The snapshot lacks the transpilers of classes patched after this one
        ///     (see SnapshotStub): the guest's fate (1220) was judged by the stricter rule of a non-player and its
        ///     dialog did not save it, and the strife kill went to the host. They are applied to this copy too
        /// </summary>
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal void Stub_DamageHP(long dmg,
                                    int ele,
                                    int eleP,
                                    AttackSource attackSource,
                                    Card origin,
                                    bool showEffect,
                                    Thing weapon,
                                    Chara originalTarget,
                                    int resistPenetrationLevel)
        {
            IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return SnapshotStub.With(instructions, OtherPlayerPatch.OnDamageHP, OtherPlayerPatch.OnDamageHPWeakspot,
                    OtherPlayerPatch.OnDamageHPDialog, StrifeKillDelta.Count.OnDamageHP);
            }

            _ = Transpiler([]);
            throw new NotImplementedException("Chara.DamageHP");
        }
    }
}