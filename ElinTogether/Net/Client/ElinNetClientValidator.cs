using System.Collections.Generic;
using System.Text;
using ElinTogether.API.SourceValidation;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using UnityEngine;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    private static readonly Color _colorMissing = new(0.55f, 0.05f, 0.05f);
    private static readonly Color _colorExtra = new(0.05f, 0.35f, 0.45f);
    private static readonly Color _colorChanged = new(0.45f, 0.05f, 0.40f);

    public SourceValidationFailed? PendingMismatch { get; private set; }

    // the mods the host published at this join, and how they differ from ours
    private ModList.Diff? _mods;
    private string _modsText = "";

    private void ContinueJoin()
    {
        if (PendingMismatch is null) {
            return;
        }

        EmpLog.Information("Client chose to continue despite validation mismatches");
        Host.Send(new SourceValidationContinue());
        PendingMismatch = null;
    }

    private void DisconnectDueToMismatch()
    {
        if (PendingMismatch is null) {
            return;
        }

        EmpLog.Information("Client chose to disconnect due to validation mismatches");
        PendingMismatch = null;

        Socket.Disconnect(Host, EmpDisconnectInfo.InvalidSource);
    }

    /// <summary>
    ///     Net event: Host checks game version
    /// </summary>
    private void OnNetHandshakeRequest(NetIntegrityRequest request)
    {
        if (!BuildVersionIntegrity.Ok(request.HostModVersion, request.HostGameVersion, request.APIVersion)) {
            EmpLog.Warning(
                "Version mismatch with host: mod {ClientModVersion} -> {HostModVersion}, " +
                "game {ClientGameVersion} -> {HostGameVersion}, api {ClientProtocolVersion} -> {HostProtocolVersion}",
                ModInfo.BuildVersion, request.HostModVersion,
                BuildVersionIntegrity.GameVersion, request.HostGameVersion,
                BuildVersionIntegrity.APIVersionLatest, request.APIVersion);

            Dialog.Ok(VersionMismatchText(request.HostModVersion, request.HostGameVersion));

            Socket.Disconnect(Host, EmpDisconnectInfo.VersionMismatch);
            return;
        }

        EmpLog.Debug("Host version verified: mod {HostModVersion}, game {HostGameVersion}",
            request.HostModVersion, request.HostGameVersion);

        // a peer-hosted world: whoever keeps it runs the build its signed rules allow (PLAN_peer_hosted_server row 12)
        if (PeerHosting.Active && PeerRules.CheckBuild(request.HostModVersion) is { } build) {
            LeaveKeeper(build);
            return;
        }

        // not the same version of Elin: the host decides whether that is allowed, this player is told
        if (!BuildVersionIntegrity.SameGame(request.HostGameVersion)) {
            EmpPop.Information("emp_game_version_differs".Loc(Host.User.Name,
                BuildVersionIntegrity.Display(request.HostGameVersion),
                BuildVersionIntegrity.Display(BuildVersionIntegrity.GameVersion)));
        }

        AdvanceHandshake(NetHandshakePhase.AwaitingIntegrity);

        // (only to a server of this machine: the token is not for anyone else)
        Host.Send(NetIntegrityResponse.Create(IsLocalConnection ? Helper.HiddenServer.OwnerToken() : null));
    }

    private void OnNetHandshakeRejected(NetIntegrityRejected rejected)
    {
        EmpLog.Warning("Host rejected the connection: {RejectReason}, {MismatchCount} entries: {MismatchEntries}",
            rejected.Reason, rejected.Details.Count, rejected.Details);

        string detail;
        if (rejected.Reason == NetIntegrityRejected.NetIntegrityRejectReason.VersionMismatch) {
            detail = VersionMismatchText(rejected.HostModVersion, rejected.HostGameVersion);
        } else {
            var sb = new StringBuilder();

            sb.AppendLine(rejected.Reason == NetIntegrityRejected.NetIntegrityRejectReason.ActMappingMismatch
                ? "emp_ui_reject_acts".Loc(rejected.Details.Count)
                : "emp_ui_reject_integrity".Loc(rejected.Details.Count));

            AppendDetails(sb, rejected.Details);

            // which mods differ, by name (the log has their Workshop pages)
            if (_mods is { } mods) {
                sb.AppendLine();
                sb.Append(ModList.Lines(mods));
            }

            detail = sb.ToString();
        }

        var reason = rejected.Reason switch {
            NetIntegrityRejected.NetIntegrityRejectReason.ActMappingMismatch => EmpDisconnectInfo.ActMappingMismatch,
            NetIntegrityRejected.NetIntegrityRejectReason.IntegrityMismatch => EmpDisconnectInfo.InvalidSource,
            _ => EmpDisconnectInfo.VersionMismatch,
        };

        // nothing to download, mods of ours the host does not have (its acts, or a strict host's check of the
        // plugins and the game's data): the player is asked to restart Elin once without them (user 2026-10-30),
        // "no" shows this refusal as before
        if (_mods is { Missing.Count: 0, Extra.Count: > 0 } extra &&
            rejected.Reason is NetIntegrityRejected.NetIntegrityRejectReason.ActMappingMismatch
                or NetIntegrityRejected.NetIntegrityRejectReason.IntegrityMismatch &&
            ModFetch.BeginWithout(_modsText, extra, ReturnTo, detail)) {
            Socket.Disconnect(Host, reason);
            return;
        }

        Dialog.Ok(detail);

        Socket.Disconnect(Host, reason);
    }

    private static string VersionMismatchText(string mod, string version)
    {
        return "emp_version_rejected_client".Loc(BuildVersionIntegrity.Explain(mod, version));
    }

    private static void AppendDetails(StringBuilder sb, List<string> details)
    {
        if (details.Count == 0) {
            return;
        }

        sb.AppendLine();

        var visible = details.Count <= 8
            ? details
            : details.GetRange(0, 8);

        foreach (var entry in visible) {
            sb.Append("  ");
            sb.AppendLine(entry);
        }

        if (details.Count > 8) {
            sb.Append("  ... ");
            sb.AppendLine("emp_ui_source_mismatch_more"
                .Loc(details.Count - 8)
                .TagColor(Color.gray));
        }
    }

    /// <summary>
    ///     Net event: Host requested source checksums
    /// </summary>
    private void OnSourceValidationRequest(SourceValidationRequest request)
    {
        EmpLog.Debug("Received source validation request: {SourceCount} sources, {FileCount} files, flags={Flags}",
            request.SourceNames.Count, request.FilePaths.Count, (ValidationFlags)request.ValidationFlags);

        // the mods of the game against ours, before anything of the world is sent: a refusal can then name them
        _mods = null;
        _modsText = request.Mods ?? "";
        if (_modsText.Length > 0) {
            _mods = ModList.Compare(ModListFile.Parse(_modsText), "the host", _modsText.Contains(ModList.LookOwn),
                ClientOnlyMods.Parse(request.ClientOnlyMods));
            if (_mods.Missing.Count > 0 && ModFetch.Begin(_modsText, _mods, ReturnTo, (ulong)Host.User, Host.User.Name)) {
                // they are fetched, Elin restarts with them and joins again by itself: nothing more is asked here
                Socket.Disconnect(Host, EmpDisconnectInfo.ClientCancel);
                return;
            }
        }

        Host.Send(SourceValidationResponse.Create(request));
    }

    /// <summary>
    ///     Net event: Host reports validation mismatches
    /// </summary>
    private void OnSourceValidationFailed(SourceValidationFailed failed)
    {
        PendingMismatch = failed;

        EmpLog.Warning(BuildMismatchLogText(failed));

        var text = BuildMismatchDialogText(failed);
        Dialog.YesNo(text, ContinueJoin, DisconnectDueToMismatch);
    }

    private static string BuildMismatchLogText(SourceValidationFailed failed)
    {
        var total = failed.SourceMismatches.Count +
                    failed.PluginMismatches.Count + failed.FileMismatches.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"Source validation failed — {total} mismatches");

        AppendAll("Sources", failed.SourceMismatches);
        AppendAll("Plugins", failed.PluginMismatches);
        AppendAll("Files", failed.FileMismatches);

        return sb.ToString();

        void AppendAll(string category, List<SourceValidationMismatch> list)
        {
            if (list.Count == 0) {
                return;
            }

            sb.AppendLine($"  [{category}] {list.Count} mismatch(es):");
            foreach (var m in list) {
                sb.Append("    ");
                sb.Append($"{MismatchTypeToLogPrefix(m.MismatchType),-10}");
                sb.Append(' ');
                sb.AppendLine(m.Entry);
            }
        }

        string MismatchTypeToLogPrefix(SourceValidationMismatch.SourceMismatchType type)
        {
            return type switch {
                SourceValidationMismatch.SourceMismatchType.MissingOnClient => "[MISSING]",
                SourceValidationMismatch.SourceMismatchType.ExtraOnClient => "[EXTRA]",
                SourceValidationMismatch.SourceMismatchType.HashChanged => "[DIFF]",
                _ => "[?]",
            };
        }
    }

    private static string BuildMismatchDialogText(SourceValidationFailed failed)
    {
        var total = failed.SourceMismatches.Count +
                    failed.PluginMismatches.Count + failed.FileMismatches.Count;

        var sb = new StringBuilder();
        sb.AppendLine("emp_ui_source_mismatch_header".Loc(total));

        AppendCategory("emp_ui_source_mismatch_sources".lang(), failed.SourceMismatches);
        AppendCategory("emp_ui_source_mismatch_plugins".lang(), failed.PluginMismatches);
        AppendCategory("emp_ui_source_mismatch_files".lang(), failed.FileMismatches);

        sb.AppendLine();
        sb.Append("emp_ui_source_mismatch_continue".lang());

        return sb.ToString();

        void AppendCategory(string header, List<SourceValidationMismatch> mismatches)
        {
            if (mismatches.Count == 0) {
                return;
            }

            sb.AppendLine();
            sb.AppendLine(header);

            var visible = mismatches.Count <= 5
                ? mismatches
                : mismatches.GetRange(0, 5);

            foreach (var m in visible) {
                sb.Append("  ");
                sb.AppendLine(MismatchLine(m));
            }

            if (mismatches.Count > 5) {
                var fold = "emp_ui_source_mismatch_more".Loc(mismatches.Count - 5);
                sb.Append("  ... ");
                sb.AppendLine(fold.TagColor(Color.gray));
            }
        }

        string MismatchLine(SourceValidationMismatch m)
        {
            var icon = MismatchTypeToIcon(m.MismatchType);
            var color = MismatchTypeToColor(m.MismatchType);
            return $"{icon} {m.Entry}".TagColor(color);
        }

        string MismatchTypeToIcon(SourceValidationMismatch.SourceMismatchType type)
        {
            return type switch {
                SourceValidationMismatch.SourceMismatchType.MissingOnClient => "[-]",
                SourceValidationMismatch.SourceMismatchType.ExtraOnClient => "[+]",
                SourceValidationMismatch.SourceMismatchType.HashChanged => "[~]",
                _ => "[?]",
            };
        }

        Color MismatchTypeToColor(SourceValidationMismatch.SourceMismatchType type)
        {
            return type switch {
                SourceValidationMismatch.SourceMismatchType.MissingOnClient => _colorMissing,
                SourceValidationMismatch.SourceMismatchType.ExtraOnClient => _colorExtra,
                SourceValidationMismatch.SourceMismatchType.HashChanged => _colorChanged,
                _ => Color.gray,
            };
        }
    }
}