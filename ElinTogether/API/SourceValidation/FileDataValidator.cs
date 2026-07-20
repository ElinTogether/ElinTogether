using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;

namespace ElinTogether.API.SourceValidation;

public class FileDataValidator(IEnumerable<string> filePaths) : ISourceValidator
{
    public string Category => "files";

    public bool TryValidate(
        Dictionary<string, string> clientHashes,
        out Dictionary<string, SourceValidationMismatch> mismatches)
    {
        mismatches = [];
        var hostHashes = GetValidation();
        var hasMismatch = false;

        foreach (var (path, hostHash) in hostHashes) {
            if (!clientHashes.TryGetValue(path, out var clientHash)) {
                mismatches[path] = new() {
                    Entry = path,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.MissingOnClient,
                };
                hasMismatch = true;
            } else if (hostHash != clientHash) {
                mismatches[path] = new() {
                    Entry = path,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.HashChanged,
                };
                hasMismatch = true;
            }
        }

        return !hasMismatch;
    }

    public Dictionary<string, string> GetValidation()
    {
        var result = new Dictionary<string, string>();

        foreach (var path in filePaths) {
            var fullPath = PackageIterator.GetFiles(path).LastOrDefault();
            if (fullPath is null) {
                result[path] = "missing";
                continue;
            }

            try {
                result[path] = fullPath.FullName.GetSha256Code();
            } catch (Exception ex) {
                EmpLog.Warning("Failed to hash file {FilePath}: {Error}", path, ex.Message);
                result[path] = $"error:{ex.Message}";
            }
        }

        return result;
    }
}