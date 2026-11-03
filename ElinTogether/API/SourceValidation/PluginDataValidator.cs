using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Models;
using EModding.Helper.Runtime;

namespace ElinTogether.API.SourceValidation;

internal class PluginDataValidator : ISourceValidator
{
    public static PluginDataValidator Default => field ??= new();

    public string Category => "plugins";

    public bool TryValidate(
        Dictionary<string, string> clientHashes,
        out Dictionary<string, SourceValidationMismatch> mismatches)
    {
        mismatches = [];
        // the mods the host accepts on a player's side only: neither side's DLL of them counts. A guest of an older
        // version reports them all, matched here by their plugin GUID when the list names it
        var list = Helper.ClientOnlyMods.Hosted;
        var hostHashes = GetValidation(list);
        var skipped = list.Count > 0 ? Helper.ClientOnlyMods.PluginGuids(list) : [];
        clientHashes = clientHashes.Where(kv => !list.Contains(kv.Key.ToLowerInvariant()) && !skipped.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
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

    public Dictionary<string, string> GetValidation() => GetValidation(null);

    /// <param name="clientOnly">the host's mods a player may have or not (ClientOnlyMods): their plugins are left out</param>
    public Dictionary<string, string> GetValidation(HashSet<string>? clientOnly)
    {
        var result = new Dictionary<string, string>();
        var skipped = clientOnly is { Count: > 0 } ? Helper.ClientOnlyMods.PluginGuids(clientOnly) : [];
        if (skipped.Count > 0) {
            EmpLog.Debug("Plugins of client-side mods left out of the plugin check: {Plugins}", skipped.ToList());
        }

        foreach (var (modId, hash) in AllHashes()) {
            if (!skipped.Contains(modId)) {
                result[modId] = hash;
            }
        }

        return result;
    }

    // every loaded plugin's DLL hashed once per run (read from the disk at each join before, on the main thread;
    // efficiency audit 2026-11-03), whatever the client-side list: that list only leaves some out. Made again at
    // each session start (CreateValidation)
    private Dictionary<string, string>? _hashes;

    internal void Invalidate()
    {
        _hashes = null;
    }

    private Dictionary<string, string> AllHashes()
    {
        if (_hashes is { } known) {
            return known;
        }

        var result = new Dictionary<string, string>();
        var failed = false;
        foreach (var plugin in TypeQualifier.Plugins) {
            var modId = plugin.Info.Metadata.GUID;
            if (ValidationConfig.ExcludedPlugins.Contains(modId)) {
                continue;
            }

            try {
                var asmPath = plugin.GetType().Assembly.Location;
                if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath)) {
                    continue;
                }

                using var fs = File.Open(asmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                result[modId] = fs.GetSha256Code();
            } catch (Exception ex) {
                EmpLog.Warning("Failed to hash plugin {ModId}: {Error}", modId, ex.Message);
                result[modId] = $"error:{ex.Message}";
                failed = true;
            }
        }

        // (a file that could not be read is tried again at the next ask, not kept as "error" for the whole run)
        if (!failed) {
            _hashes = result;
        }

        return result;
    }
}