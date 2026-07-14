using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(UIInventory), nameof(UIInventory.RefreshMenu))]
internal class InvRefreshMenuEvent
{
    [HarmonyPrefix]
    internal static void LoadSaveData(UIInventory __instance)
    {
        if (Window.dictData.TryGetValue(__instance.window.idWindow, out var data)) {
            __instance.window.saveData = data;
        }
    }

    [HarmonyPostfix]
    internal static void OnRefreshMenu(UIInventory __instance)
    {
        __instance.window.buttonSort.onClick.AddListener(() => {
            CoroutineHelper.Deferred(PropagateSaveData,
                () => !EMono.ui.contextMenu || !EMono.ui.contextMenu.currentMenu);
        });

        __instance.window.buttonShared.onClick.AddListener(PropagateSaveData);

        return;

        void PropagateSaveData()
        {
            if (NetSession.Instance.Connection is not { } connection) {
                return;
            }

            connection.Delta.AddRemote(new InvSaveDataDelta {
                WindowId = __instance.window.idWindow,
                Data = LZ4Bytes.Create(__instance.window.saveData),
                IsShop = __instance.IsShop,
            });
        }
    }
}