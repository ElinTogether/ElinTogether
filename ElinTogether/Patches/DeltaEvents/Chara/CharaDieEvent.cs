using System;
using System.Collections.Generic;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
internal static class CharaDieEvent
{
    [HarmonyPrefix]
    internal static bool OnCharaDie(Chara __instance, Element? e, Card? origin, AttackSource attackSource, Chara? originalTarget,
        bool __runOriginal)
    {
        // Harmony runs this prefix even after another one skipped the original (the server's stand-in cannot die): no
        // death to tell then
        if (!__runOriginal) {
            return false;
        }

        switch (NetSession.Instance.Connection) {
            case ElinNetHost host:
                CharaMoveDelta.ClearRecentMove(__instance.uid);

                host.Delta.DeferRemote(new CharaDieDelta {
                    Owner = __instance,
                    ElementId = e?.id,
                    Origin = origin,
                    AttackSource = attackSource,
                    OriginalTarget = originalTarget,
                });

                if (ElinDelta.IsApplying) {
                    using var _ = ElinDelta.Simulate();
                    __instance.Stub_Die(e, origin, attackSource, originalTarget);
                    return false;
                }

                return true;
            case ElinNetClient:
                // we are clients, drop the update and wait for delta
                return false;
            default:
                return true;
        }
    }

    extension(Chara chara)
    {
        /// <summary>
        ///     The death a guest's act causes on the host runs here. A snapshot holds only the transpilers patched in
        ///     before this class (types are patched in assembly order): OtherPlayerPatch comes later, and a guest's
        ///     planter finish grew the host's plant. Its transpiler is applied to this copy too (a no-op when the
        ///     snapshot already has it). A new transpiler of Chara.Die must be added here as well (SnapshotStub warns at start)
        /// </summary>
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal void Stub_Die(Element? e, Card? origin, AttackSource attackSource, Chara? originalTarget)
        {
            IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return SnapshotStub.With(instructions, OtherPlayerPatch.OnPlanterFinish);
            }

            _ = Transpiler([]);
            throw new NotImplementedException("Chara.Die");
        }
    }
}