using System.Collections.Generic;
using ElinTogether.API.SourceValidation;
using ElinTogether.Common;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    public void SetValidationFlags(ValidationFlags flags)
    {
        ValidFlags = flags;
    }

    private void RequestSourceValidation(ISteamNetPeer peer)
    {
        if (ValidFlags == ValidationFlags.None) {
            EmpLog.Debug("Source validation disabled (flags=None), skipping for {@Peer}",
                peer);
            PreparePlayerJoin(peer);
            return;
        }

        EmpLog.Debug("Requesting source validation from {@Peer} (flags={Flags})",
            peer, ValidFlags);

        var filePaths = GetValidationFilePaths();

        var request = new SourceValidationRequest {
            SourceNames = GetValidationSourceNames(),
            FilePaths = filePaths,
            ValidationFlags = (int)ValidFlags,
        };

        peer.Send(request);
    }

    /// <summary>
    ///     Net event: Client proceeds to connection with mismatches
    /// </summary>
    private void OnSourceValidationContinue(SourceValidationContinue response, ISteamNetPeer peer)
    {
        EmpLog.Information("Client {@Peer} chose to continue with mismatches.",
            peer);

        if (EmpConfig.Server.StrictValidationMode.Value) {
            Socket.Disconnect(peer, EmpDisconnectInfo.InvalidSource);
        } else {
            PreparePlayerJoin(peer);
        }
    }

    /// <summary>
    ///     Net event: Client reports validation results
    /// </summary>
    private void OnSourceValidationResponse(SourceValidationResponse response, ISteamNetPeer peer)
    {
        EmpLog.Debug("Received source validation response from {@Peer}",
            peer);

        var mismatchCount = 0;

        // acts
        // this is mandatory, syncs will not work properly without mapped acts
        var actMismatches = new List<SourceValidationMismatch>();
        if (!ActMappingValidator.Default.TryValidate(response.ActMapping, out var mismatches)) {
            mismatchCount += mismatches.Count;
            foreach (var (actName, m) in mismatches) {
                actMismatches.Add(m);
                EmpLog.Debug("Peer {@Peer} has act mismatch: {ActName} [{MismatchType}]",
                    peer, actName, m.MismatchType);
            }
        }

        // sources
        var sourceMismatches = new List<SourceValidationMismatch>();
        if (ValidFlags.HasFlag(ValidationFlags.Sources)) {
            if (!SourceDataValidator.Default.TryValidate(response.SourceHashes, out mismatches)) {
                mismatchCount += mismatches.Count;
                foreach (var (source, m) in mismatches) {
                    sourceMismatches.Add(m);
                    EmpLog.Verbose("Peer {@Peer} has source mismatch: {SourceName} [{MismatchType}]",
                        peer, source, m.MismatchType);
                }
            }
        }

        // plugins
        var pluginMismatches = new List<SourceValidationMismatch>();
        if (ValidFlags.HasFlag(ValidationFlags.Plugins)) {
            if (!PluginDataValidator.Default.TryValidate(response.PluginHashes, out mismatches)) {
                mismatchCount += mismatches.Count;
                foreach (var (modId, m) in mismatches) {
                    pluginMismatches.Add(m);
                    EmpLog.Verbose("Peer {@peer} has plugin mismatch: {PluginName} [{MismatchType}]",
                        peer, modId, m.MismatchType);
                }
            }
        }

        // files
        var fileMismatches = new List<SourceValidationMismatch>();
        if (ValidFlags.HasFlag(ValidationFlags.Files)) {
            var fileValidator = new FileDataValidator(ValidationFilePaths);
            if (!fileValidator.TryValidate(response.FileHashes, out mismatches)) {
                mismatchCount += mismatches.Count;
                foreach (var (path, m) in mismatches) {
                    fileMismatches.Add(m);
                    EmpLog.Verbose("Peer {@peer} has file mismatch: {PluginName}[{MismatchType}]",
                        peer, path, m.MismatchType);
                }
            }
        }

        if (mismatchCount == 0) {
            EmpLog.Information("Source validation passed for {@Peer}",
                peer);
            PreparePlayerJoin(peer);
            return;
        }

        peer.Send(new SourceValidationFailed {
            SourceMismatches = sourceMismatches,
            PluginMismatches = pluginMismatches,
            FileMismatches = fileMismatches,
            ActMismatches = actMismatches,
        });
    }
}