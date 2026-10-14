using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(UIInventory), nameof(UIInventory.RefreshMenu))]
internal class InvRefreshMenuEvent
{
    [HarmonyPostfix]
    internal static void OnRefreshMenu(UIInventory __instance)
    {
        var container = __instance.owner.Container;
        if (!ReferenceEquals(__instance.window.saveData, container.c_windowSaveData)) {
            return;
        }

        __instance.window.buttonSort.onClick.AddListener(() => {
            CoroutineHelper.Deferred(PropagateSaveData,
                () => (!EMono.ui.contextMenu || !EMono.ui.contextMenu.currentMenu) && !EMono.ui.GetLayer<Dialog>());
        });

        __instance.window.buttonShared.onClick.AddListener(PropagateSaveData);

        return;

        void PropagateSaveData()
        {
            if (NetSession.Instance.Connection is not { } connection || PendingUid.IsPending(container.uid)) {
                return;
            }

            connection.Delta.AddRemote(new InvSaveDataDelta {
                Container = container,
                Data = LZ4Bytes.Create(container.c_windowSaveData),
            });
        }
    }
}