using System;
using System.IO;
using System.Threading;
using ElinTogether.LangMod;
using ElinTogether.Net;
using ReflexCLI.Attributes;

namespace ElinTogether;

internal partial class EmpConfig
{
    private const EmpConfigVersion CurrentVersion = EmpConfigVersion.V1;

    [ConsoleCommand("reload_cfg")]
    internal static void Reload()
    {
        EmpMod.Instance.Config.Reload();
    }

    [ConsoleCommand("reset_cfg")]
    internal static void Reset()
    {
        var config = EmpMod.Instance.Config;
        // where the world is kept and how to reach it are no settings: a reset of the options keeps them (the game's
        // mod page "Reset" cleared the depot's key, review 2026-10-22)
        var kept = (Client.DepotPath.Value, Client.DepotPassword.Value, Client.ServerAddress.Value);
        File.WriteAllText(config.ConfigFilePath, "");

        config.SaveOnConfigSet = false;
        config.Clear();

        Bind();
        Reload();
        (Client.DepotPath.Value, Client.DepotPassword.Value, Client.ServerAddress.Value) = kept;

        config.Save();
        config.SaveOnConfigSet = true;

        EmpPop.PopupInternal("emp_ui_config_reset".Loc(CurrentVersion));
    }

    internal static void InvalidateConfigs()
    {
        var context = GameIOContext.GetPersistentModContext("ElinMP")!;
        if (context.Load<EmpConfigVersion>("config_version", out var version) &&
            version >= CurrentVersion) {
            return;
        }

        context.SaveUncompressed("config_version", CurrentVersion);
        Reset();
    }

    internal static void EnableReloadWatcher()
    {
        var config = EmpMod.Instance.Config;

        FileWatcherHelper.Register(
            "emp_config",
            Path.GetDirectoryName(config.ConfigFilePath)!,
            $"{ModInfo.Guid}.cfg",
            args => {
                if (args.ChangeType != WatcherChangeTypes.Changed) {
                    return;
                }

                // the watcher calls from its own thread: reloading there raced the game's own saves (a box set from a
                // page read back its old value, settings_suite LIVE 2026-10-25). Reloaded on the game's thread, once
                // the file has been quiet for a moment
                Interlocked.Exchange(ref _reloadAt, DateTime.UtcNow.AddMilliseconds(500).Ticks);
            });
    }

    // when the file changed on disk is read again (UTC ticks), 0: nothing to read
    private static long _reloadAt;

    /// <summary>
    ///     Each frame, on the game's thread
    /// </summary>
    internal static void UpdateReload()
    {
        var at = Interlocked.Read(ref _reloadAt);
        if (at == 0 || DateTime.UtcNow.Ticks < at || Interlocked.CompareExchange(ref _reloadAt, 0, at) != at) {
            return;
        }

        // no popup here: every save of the file from a page came back as "settings changed (xN)". A friend is told
        // a real change of a rule it plays by (SettingsTable.NoticeChanges, emp_ui_rules_changed)
        var config = EmpMod.Instance.Config;

        config.SaveOnConfigSet = false;
        config.Reload();
        config.SaveOnConfigSet = true;

        // a peer-hosted world's hidden server plays its signed rules: put back before anyone is told the reloaded
        // ones (the keeper's own game saving the shared file reloaded it here; a guest joining in between read the
        // unsigned values and left, "does not play its signed rules")
        Helper.PeerRules.Apply();

        if (NetSession.Instance.Transport is ElinNetHost host) {
            host.UpdateRemoteSessionRules();
        }
    }

    private enum EmpConfigVersion
    {
        V1,
    }
}