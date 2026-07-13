using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine.Events;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class InvRefreshMenuEvent
{
    private static bool OpeningSettings = false;
    private static Window Window = null!;
    private static Card Owner = null!;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIInventory), nameof(UIInventory.RefreshMenu))]
    internal static void OnRefreshMenu(UIInventory __instance)
    {
        Window = __instance.window;
        Owner = __instance.owner.owner;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UnityEvent), nameof(UnityEvent.AddListener))]
    public static void OnAddListener(UnityEvent __instance, ref UnityAction call)
    {
        if (Window is null) {
            return;
        }

        if (__instance == Window.buttonSort.onClick) {
            var original = call;
            call = () => {
                OpeningSettings = true;
                original();
                OpeningSettings = false;
            };
        } else if (__instance == Window.buttonShared.onClick || OpeningSettings) {
            var original = call;
            call = () => {
                original();
                OnChangeSettings();
            };
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UnityEvent<bool>), nameof(UnityEvent.AddListener))]
    public static void OnAddListener(ref UnityAction<bool> call)
    {
        if (Window is null) {
            return;
        }

        if (OpeningSettings) {
            var original = call;
            call = b => {
                original(b);
                OnChangeSettings();
            };
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UnityEvent<float>), nameof(UnityEvent.AddListener))]
    public static void OnAddListener(ref UnityAction<float> call)
    {
        if (Window is null) {
            return;
        }

        if (OpeningSettings) {
            var original = call;
            call = f => {
                original(f);
                OnChangeSettings();
            };
        }
    }

    internal static void OnChangeSettings()
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        connection.Delta.AddRemote(new InvSaveDataDelta {
            WindowId = Window.idWindow,
            Data = LZ4Bytes.Create(Window.saveData),
        });
    }
}