using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;

namespace ElinTogether.API.SourceValidation;

/// <summary>
///     Validates Chara Element Race Job Hobby Zone Quest Religion rows
/// </summary>
internal class SourceDataValidator : ISourceValidator
{
    public static SourceDataValidator Default => field ??= new();

    public string Category => "sources";

    public bool TryValidate(
        Dictionary<string, string> clientChecksums,
        out Dictionary<string, SourceValidationMismatch> mismatches)
    {
        mismatches = [];
        var hostChecksums = GetValidation();
        var hasMismatch = false;

        foreach (var (sourceName, hostSha) in hostChecksums) {
            if (!clientChecksums.TryGetValue(sourceName, out var clientSha)) {
                mismatches[sourceName] = new() {
                    Entry = sourceName,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.MissingOnClient,
                };
                hasMismatch = true;
            } else if (clientSha != hostSha) {
                mismatches[sourceName] = new() {
                    Entry = sourceName,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.HashChanged,
                };
                hasMismatch = true;
            }
        }

        return !hasMismatch;
    }

    public Dictionary<string, string> GetValidation()
    {
        return GetValidation(ValidationConfig.DefaultSources);
    }

    // each table's checksum, made once per run (every row exported to JSON and hashed, on the main thread: it was
    // made again at every join, on both sides; efficiency audit 2026-11-03). Forgotten when the game imports its
    // tables again (a language change, a reload) and at each session start (CreateValidation)
    private readonly Dictionary<string, string> _checksums = [];

    public Dictionary<string, string> GetValidation(IEnumerable<string> sourceNames)
    {
        var result = new Dictionary<string, string>();
        foreach (var name in sourceNames.Distinct()) {
            result[name] = Checksum(name);
        }

        return result;
    }

    private string Checksum(string sourceType)
    {
        lock (_checksums) {
            if (!_checksums.TryGetValue(sourceType, out var sha)) {
                _checksums[sourceType] = sha = GenerateSourceChecksum(sourceType);
            }

            return sha;
        }
    }

    internal void Invalidate()
    {
        lock (_checksums) {
            _checksums.Clear();
        }
    }

    /// <summary>
    ///     The game imported its tables again: the checksums are made again at the next ask
    /// </summary>
    internal static void SubscribeReloads()
    {
        BaseModManager.SubscribeEvent("elin.source.imported", (object _) => Default.Invalidate());
        BaseModManager.SubscribeEvent("elin.source.lang_set", (object _) => Default.Invalidate());
    }

    public static string GenerateSourceChecksum(string sourceType)
    {
        return ModUtil
            .FindSourceByName(sourceType)
            .ExportRows()
            .ToCompactJson()
            .GetSha256Code();
    }
}