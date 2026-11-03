using System;
using System.Collections.Generic;
using System.IO;
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

        // security: the host sends these paths. The game joins them to each mod folder, so a full path ("C:\...")
        // or ".." would hash any file of this PC (a test of what is on it): only paths inside a mod, capped
        foreach (var path in filePaths.Take(MaxPaths)) {
            if (!IsModRelative(path)) {
                EmpLog.Warning("Refused to hash the file {FilePath}: not a path inside a mod", path);
                result[path] = "refused";
                continue;
            }

            var fullPath = PackageIterator.GetFiles(path).LastOrDefault();
            if (fullPath is { Length: > MaxBytes }) {
                result[path] = "too large";
                continue;
            }
            if (fullPath is null) {
                result[path] = "missing";
                continue;
            }

            lock (Known) {
                if (Known.TryGetValue(fullPath.FullName, out var known)) {
                    result[path] = known;
                    continue;
                }
            }

            try {
                using var fs = File.Open(fullPath.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var hash = fs.GetSha256Code();
                result[path] = hash;
                lock (Known) {
                    if (Known.Count >= MaxKnown) {
                        Known.Clear();
                    }

                    Known[fullPath.FullName] = hash;
                }
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Failed to hash file {FilePath}", path);
                result[path] = "error";
            }
        }

        return result;
    }

    // each file read and hashed once per run (at every join before, on the main thread; efficiency audit
    // 2026-11-03), by its full path: a missing or unreadable one is looked for again at the next ask. Forgotten at
    // each session start (CreateValidation), bounded (the paths come from the host on a guest's side)
    private static readonly Dictionary<string, string> Known = [];
    private const int MaxKnown = 2048;

    internal static void Invalidate()
    {
        lock (Known) {
            Known.Clear();
        }
    }

    private const int MaxPaths = 512;
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>
    ///     A path relative to a mod folder that cannot leave it: not rooted, no drive or stream (':'), no "..",
    ///     no character a path may not have, one short line
    /// </summary>
    internal static bool IsModRelative(string? path)
    {
        if (path is not { Length: > 0 and <= 260 } || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.IndexOfAny([':', '*', '?']) >= 0 || Path.IsPathRooted(path) || path[0] is '\\' or '/') {
            return false;
        }

        // ".", "..", "..." or blanks: Windows reads them as a step up or as nothing
        return path.Split('\\', '/').All(part => part.Length == 0 || part.Trim().TrimEnd('.').Length > 0);
    }
}