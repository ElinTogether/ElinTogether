using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaEquipEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharaBody), nameof(CharaBody.Equip))]
    internal static void OnEquip(CharaBody __instance, Thing thing, bool __result)
    {
        // toggle 卸装分支返回 false，由 Unequip postfix 负责上报
        if (!__result) {
            return;
        }

        // c_equippedSlot 在成功路径刚被赋值
        var slotIndex = thing.c_equippedSlot - 1;
        if (slotIndex < 0 || slotIndex >= __instance.slots.Count) {
            return;
        }

        Report(__instance.owner, thing, __instance.slots[slotIndex], true);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharaBody), nameof(CharaBody.Unequip), typeof(BodySlot), typeof(bool))]
    internal static void OnUnequipCapture(BodySlot slot, ref Thing? __state)
    {
        __state = slot.thing;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharaBody), nameof(CharaBody.Unequip), typeof(BodySlot), typeof(bool))]
    internal static void OnUnequip(CharaBody __instance, BodySlot slot, Thing? __state)
    {
        if (__state is null) {
            return;
        }

        Report(__instance.owner, __state, slot, false);
    }

    private static void Report(Chara? owner, Thing thing, BodySlot slot, bool equip)
    {
        if (NetSession.Instance.Connection is not { } connection || owner is null) {
            return;
        }

        // delta 重演（含 OnApply 内嵌套的换装 Unequip）不回环
        if (ElinDelta.IsApplying) {
            return;
        }

        // 建号期装备流水不上报
        if (owner.GetBool("emp_creating")) {
            return;
        }

        if (connection.IsClient) {
            // 客机只报自己，影子 uid 不出机
            if (!owner.IsPC || PendingUid.IsPending(thing.uid) || PendingUid.IsPending(owner.uid)) {
                return;
            }

            // AI_Equip 路径已由 CharaTaskDelta 白名单在主机重演，双通道会命中
            // vanilla Equip 的 toggle 分支反向脱装
            for (var act = owner.ai; act is not null; act = act.child) {
                if (act is AI_Equip) {
                    return;
                }
            }
        } else if (!CardCache.Contains(owner)) {
            return;
        }

        connection.Delta.AddRemote(new CharaEquipDelta {
            Owner = owner,
            Thing = thing,
            SlotIndex = slot.index,
            SlotElementId = slot.elementId,
            Equip = equip,
        });
    }
}