using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;

namespace ElinTogether.API.SourceValidation;

/// <summary>
///     Validates Elin source table checksums (source name → SHA256 of exported rows).
/// </summary>
internal class SourceDataValidator : ISourceValidator
{
    public static SourceDataValidator Default => field ??= new();

    public string Category => "source";

    public bool TryValidate(
        Dictionary<string, string> clientChecksums,
        out Dictionary<string, SourceValidationMismatch> mismatches)
    {
        mismatches = [];
        var hostChecksums = GetValidation();
        var hasMismatch = false;

        foreach (var (sourceName, hostSha) in hostChecksums) {
            var clientSha = clientChecksums.GetValueOrDefault(sourceName);
            if (clientSha == hostSha || clientSha is null) {
                continue;
            }

            mismatches[sourceName] = new() {
                Entry = sourceName,
                MismatchType = SourceValidationMismatch.SourceMismatchType.HashChanged,
            };
            hasMismatch = true;
        }

        return !hasMismatch;
    }

    public Dictionary<string, string> GetValidation()
    {
        return GetValidation(ValidationConfig.DefaultSources);
    }

    public Dictionary<string, string> GetValidation(IEnumerable<string> sourceNames)
    {
        return sourceNames
            .Distinct()
            .ToDictionary(s => s, GenerateSourceChecksum);
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