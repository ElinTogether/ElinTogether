using System;
using System.Collections.Generic;
using System.IO;
using ElinTogether.Models;
using EModding.Helper.Runtime;

namespace ElinTogether.API.SourceValidation;

internal class PluginDataValidator : ISourceValidator
{
    public static PluginDataValidator Default => field ??= new();

    public string Category => "plugin";

    public bool TryValidate(
        Dictionary<string, string> clientHashes,
        out Dictionary<string, SourceValidationMismatch> mismatches)
    {
        mismatches = [];
        var hostHashes = GetValidation();
        var hasMismatch = false;

        foreach (var (modId, hostHash) in hostHashes) {
            if (!clientHashes.TryGetValue(modId, out var clientHash)) {
                mismatches[modId] = new() {
                    Entry = modId,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.MissingOnClient,
                };
                hasMismatch = true;
            } else if (hostHash != clientHash) {
                mismatches[modId] = new() {
                    Entry = modId,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.HashChanged,
                };
                hasMismatch = true;
            }
        }

        foreach (var (modId, _) in clientHashes) {
            if (!hostHashes.ContainsKey(modId)) {
                mismatches[modId] = new() {
                    Entry = modId,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.ExtraOnClient,
                };
                hasMismatch = true;
            }
        }

        return !hasMismatch;
    }

    public Dictionary<string, string> GetValidation()
    {
        var result = new Dictionary<string, string>();

        foreach (var plugin in TypeQualifier.Plugins) {
            var modId = plugin.Info.Metadata.GUID;
            if (ValidationConfig.ExcludedPlugins.Contains(modId)) {
                continue;
            }

            try {
                var asmPath = plugin.GetType().Assembly.Location;
                if (asmPath.IsEmpty() || !File.Exists(asmPath)) {
                    continue;
                }

                result[modId] = asmPath.GetSha256Code();
            } catch (Exception ex) {
                EmpLog.Warning("Failed to hash plugin {ModId}: {Error}", modId, ex.Message);
                result[modId] = $"error:{ex.Message}";
            }
        }

        return result;
    }
}