using System.Collections.Generic;
using System.Linq;
using ElinTogether.API.SourceValidation;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    public void SetValidationFlags(ValidationFlags flags)
    {
        ValidFlags = flags;
    }

    /// <summary>
    ///     Net event: Client proceeds to connection with mismatches
    /// </summary>
    private void OnSourceValidationContinue(SourceValidationContinue response, ISteamNetPeer peer)
    {
        if (EmpConfig.Server.StrictValidationMode.Value) {
            RejectHandshake(peer, NetIntegrityRejected.NetIntegrityRejectReason.IntegrityMismatch);
            return;
        }

        EmpLog.Information("Client {@Peer} chose to continue with mismatches.",
            peer);

        AcceptHandshake(peer);
    }

    /// <summary>
    ///     Net event: Client reports validation results
    /// </summary>
    private void OnSourceValidationResponse(SourceValidationResponse response, ISteamNetPeer peer)
    {
        EmpLog.Debug("Received source validation response from {@Peer}",
            peer);

        // acts, man what can I say
        if (!ActMappingValidator.Default.TryValidate(response.ActMapping, out var actMismatches)) {
            foreach (var (actType, m) in actMismatches) {
                EmpLog.Debug("Peer {@Peer} has act mismatch: {ActType} [{MismatchType}]",
                    peer, actType, m.MismatchType);
            }

            RejectHandshake(peer, NetIntegrityRejected.NetIntegrityRejectReason.ActMappingMismatch,
                actMismatches.Values.Select(m => m.Entry));
            return;
        }

        var mismatchCount = 0;

        // sources
        var sourceMismatches = new List<SourceValidationMismatch>();
        if (ValidFlags.HasFlag(ValidationFlags.Sources)) {
            if (!SourceDataValidator.Default.TryValidate(response.SourceHashes, out var mismatches)) {
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
            if (!PluginDataValidator.Default.TryValidate(response.PluginHashes, out var mismatches)) {
                mismatchCount += mismatches.Count;
                foreach (var (modId, m) in mismatches) {
                    pluginMismatches.Add(m);
                    EmpLog.Verbose("Peer {@Peer} has plugin mismatch: {ModId} [{MismatchType}]",
                        peer, modId, m.MismatchType);
                }
            }
        }

        // files
        var fileMismatches = new List<SourceValidationMismatch>();
        if (ValidFlags.HasFlag(ValidationFlags.Files)) {
            var fileValidator = new FileDataValidator(ValidationFilePaths);
            if (!fileValidator.TryValidate(response.FileHashes, out var mismatches)) {
                mismatchCount += mismatches.Count;
                foreach (var (path, m) in mismatches) {
                    fileMismatches.Add(m);
                    EmpLog.Verbose("Peer {@Peer} has file mismatch: {FilePath} [{MismatchType}]",
                        peer, path, m.MismatchType);
                }
            }
        }

        if (mismatchCount == 0) {
            EmpLog.Information("Source validation passed for {@Peer}",
                peer);
            AcceptHandshake(peer);
            return;
        }

        if (EmpConfig.Server.StrictValidationMode.Value) {
            var entries = sourceMismatches
                .Concat(pluginMismatches)
                .Concat(fileMismatches)
                .Select(m => m.Entry);
            RejectHandshake(peer, NetIntegrityRejected.NetIntegrityRejectReason.IntegrityMismatch, entries);
            return;
        }

        peer.Send(new SourceValidationFailed {
            SourceMismatches = sourceMismatches,
            PluginMismatches = pluginMismatches,
            FileMismatches = fileMismatches,
        });
    }
}