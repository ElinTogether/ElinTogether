using System.Reflection;
using BepInEx;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.String;
using ElinTogether.LangMod;
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
    internal const string MajorMinor = "1.0";
    internal const string Version = $"{MajorMinor}.{GitVersionInformation.CommitsSinceVersionSource}";

    internal static string BuildVersion => field ??= EmpMod.Assembly.GetName().Version.ToString();
}

[BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
internal sealed class EmpMod : BaseUnityPlugin
{
    internal static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
    internal static readonly Harmony SharedHarmony = new(ModInfo.Guid);

    // (not the id of the session patches: those are all removed when the last session of a release build closes)
    internal static readonly Harmony AlwaysHarmony = new(ModInfo.Guid + ".always");

    internal static EmpMod Instance { get; private set; } = null!;

    /// <summary>
    ///     The patch classes this build of Elin could not take (a target it lacks, e.g. on the stable branch)
    /// </summary>
    internal static readonly System.Collections.Generic.List<string> SkippedPatches = [];

    /// <summary>
    ///     Harmony's PatchAll, one class at a time: Harmony stops at the first class whose target is missing and
    ///     leaves every class after it unpatched. Here such a class is skipped with a log line and the rest load
    /// </summary>
    internal static void PatchAll()
    {
        Patch(SharedHarmony, false);
    }

    /// <summary>
    ///     The classes marked <see cref="AlwaysPatchedAttribute" />, once, when the mod starts (every build): never
    ///     taken away with the session's
    /// </summary>
    private static void PatchAlways()
    {
        Patch(AlwaysHarmony, true);
    }

    /// <summary>
    ///     The patches of a release build are only there while a session is open (ElinNetBase.Awake and OnDestroy),
    ///     those of a debug build from the start. A debug game started with -empreleasepatches patches as a release
    ///     build does, so that the bench meets what the players meet (issue 66)
    /// </summary>
    internal static readonly bool PatchesPerSession =
#if DEBUG
        System.Array.IndexOf(System.Environment.GetCommandLineArgs(), ReleasePatchesArgument) >= 0;
#else
        true;
#endif

#if DEBUG
    internal const string ReleasePatchesArgument = "-empreleasepatches";
#endif

    private static void Patch(Harmony harmony, bool always)
    {
        SnapshotStub.Reset();
        foreach (var type in AccessTools.GetTypesFromAssembly(Assembly)) {
            if (AlwaysPatchedAttribute.On(type) != always) {
                continue;
            }

            try {
                SnapshotStub.Begin(type);
                harmony.CreateClassProcessor(type).Patch();
                SnapshotStub.Note();
            } catch (System.Exception ex) {
                if (!SkippedPatches.Contains(type.FullName)) {
                    SkippedPatches.Add(type.FullName);
                }

                EmpLog.Warning(ex, "Patch {PatchClass} skipped on Elin {GameVersion}: {Reason}",
                    type.FullName, EClass.core?.version.GetText(), ex.InnerException?.Message ?? ex.Message);
            }
        }

        SnapshotStub.ReportMissing();

        // the player is told once, in words, rather than finding out by a sync that does not happen
        if (SkippedPatches.Count > 0 && !_skippedTold && EClass.core is { } core) {
            _skippedTold = true;
            core.actionsNextFrame.Add(() => EmpPop.Information("emp_patches_skipped".Loc(SkippedPatches.Count,
                core.version.GetText(), BuildVersionIntegrity.BuiltForGameText)));
        }
    }

    private static bool _skippedTold;

    private void Awake()
    {
        Instance = this;

        EmpPop.InitLogger();
        EmpConfig.Bind();
        API.SourceValidation.SourceDataValidator.SubscribeReloads();

        // a hidden server lets go of its single-instance mutex, so that the game of the player who started it, or
        // one started again after a crash, is not held up by it (see SingleInstance)
        if (EmpServer.Requested) {
            SingleInstance.Release();
            EmpServer.OnModLoaded();
        }

        // the game has read its mod list: the player's own goes back on the disk if this run is a session's
        ModFetch.Boot();

        NetShutdown.SetupApplicationHook();

        CommandRegistry.assemblies.Add(Assembly);

        // session or not: a world whose mounts stand outside the map could not be loaded at all
        Patches.RideOffMapPatch.Apply();
        PatchAlways();

        if (!PatchesPerSession) {
            PatchAll();
        }

#if DEBUG
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
        // (the visible game waits for its hidden server with no session open: in a release build the Core.Update
        // patch is not there yet, it never joined nor timed out, issue 66)
        Helper.HiddenServer.Update();
        // (the save depot is taken and joined from the title screen: its answers and the join's time-out come with no
        // session open, same as issue 66)
        Always("save depot", Helper.SaveDepot.Update);
        // (the peer-hosted world is opened from the title screen: the store's answers to the title card's WHO/TAKE
        // and the claim of the lease while the hidden server starts come with no session open, same as issue 66)
        Always("peer-hosted world", Helper.PeerKeeper.Update);
        // (a cross-check report answered after the link closed is still read)
        Always("cross-checks", Helper.CrossCheck.Update);
        // (-empnewworld: a new world made at the title, before any session)
        Helper.OfficialWorld.Update();
    }

    // (one of these failing does not stop the others for this frame; logged once a minute at most)
    private static float _nextUpdateWarning;

    private static void Always(string what, System.Action update)
    {
        try {
            update();
        } catch (System.Exception ex) {
            if (UnityEngine.Time.realtimeSinceStartup >= _nextUpdateWarning) {
                _nextUpdateWarning = UnityEngine.Time.realtimeSinceStartup + 60f;
                EmpLog.Warning(ex, "The {What} update failed", what);
            }
        }
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