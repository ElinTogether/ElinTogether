using System.Reflection;
using BepInEx;
using ElinTogether.Helper;
using ElinTogether.Helper.String;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;
using ReflexCLI;
using Steamworks;

namespace ElinTogether;

internal static class ModInfo
{
    internal const string Guid = "dk.elinplugins.elintogether";
    internal const string Name = "Elin Together";
    internal const string MajorMinor = "0.26";
    internal const string Version = $"{MajorMinor}.{GitVersionInformation.CommitsSinceVersionSource}";

    internal static string BuildVersion => field ??= EmpMod.Assembly.GetName().Version.ToString();
}

[BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
internal sealed class EmpMod : BaseUnityPlugin
{
    internal static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
    internal static readonly Harmony SharedHarmony = new(ModInfo.Guid);

    internal static EmpMod Instance { get; private set; } = null!;

    private void Awake()
    {
        Instance = this;

        EmpPop.InitLogger();
        EmpConfig.Bind();

        // a hidden server lets go of its single-instance mutex, so that the game of the player who started it, or
        // one started again after a crash, is not held up by it (see SingleInstance)
        if (EmpServer.Requested) {
            SingleInstance.Release();
        }

        // the game has read its mod list: the player's own goes back on the disk if this run is a session's
        ModFetch.Boot();

        NetShutdown.SetupApplicationHook();

        CommandRegistry.assemblies.Add(Assembly);

        // session or not: a world whose mounts stand outside the map could not be loaded at all
        Patches.RideOffMapPatch.Apply();

#if DEBUG
        SharedHarmony.PatchAll(Assembly);

        if (EmpConfig.Dev.Listener.Value) {
            gameObject.AddComponent<EmpDebugListener>();
        }

        if (EmpBot.Requested) {
            gameObject.AddComponent<EmpBot>();
        }
#endif
    }

    private void Start()
    {
#if !DEBUG
        EModding.Helper.Runtime.Exceptions.MonoFrame.AddVendorExclusion("MessagePack.");
#endif

        ResourceFetch.InvalidateTemp();
        EmpConfig.InvalidateConfigs();
        EmpConfig.EnableReloadWatcher();
        ModConfigPage.Register();

        SteamNetworkingUtils.InitRelayNetworkAccess();

        NetSession.Instance.Lobby.TryParseLobbyCommand();

        TitleButtonPatch.RegisterTitleButton(Scene.Mode.Title);
        // (in the background, once per game start: a newer release shows on that button)
        UpdateCheck.Begin();

        StartCoroutine(Helper.ReplayBuffer.Run());
    }

    // (the patches of a release build are only there while a session is: this runs on the title screen too)
    private void Update()
    {
        ModFetch.Update();
        EmpConfig.UpdateReload();
        Components.SettingsTable.Update();
        Helper.HubDot.Update();
        // (from the patches during a session; a server has to load its world before any session)
        EmpServer.Update();
        // (-empnewworld: a new world made at the title, before any session)
        Helper.OfficialWorld.Update();
    }

    private void OnDestroy()
    {
        if (!NetShutdown.IsQuitting) {
            NetSession.Instance.ResetSession();
            NetSession.Instance.Lobby.Shutdown();
        }

        StringAllocator.UnpinSharedStringHandles();
    }
}