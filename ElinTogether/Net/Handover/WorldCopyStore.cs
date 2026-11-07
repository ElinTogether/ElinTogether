using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ElinTogether.Models;
using Newtonsoft.Json;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Where a guest keeps the copies of the worlds it plays in as a guest: its own folder next to the mod's
///     journals, never the game's save folders. One folder per world, in it "incoming" (the copy being received)
///     and the whole copies, each with its copy.json written last. Nothing of Unity nor of the game past
///     <see cref="Root" />: the rest runs off the game's thread
/// </summary>
internal static class WorldCopyStore
{
    internal const string ManifestName = "copy.json";
    internal const string Incoming = "incoming";
    private const string CopyPrefix = "copy-";

    // the previous whole copy stays until the next one is whole, and one more: a copy is never the only one
    // (council 2026-11-07: three, a takeover still has an older one if the newest is bad)
    private const int CopiesKept = 3;

    // what a host may ask this disk to hold
    internal const int MaxFiles = 20000;
    internal const long MaxBytes = 1L << 30;

    /// <summary>
    ///     To read on the game's thread first
    /// </summary>
    internal static string Root => field ??= Path.Combine(Application.persistentDataPath, "ElinMP",
#if DEBUG
        // test instances on one machine share this folder, each keeps its own copies
        EmpConfig.Dev.Identity.Value > 0 ? $"WorldCopy_{EmpConfig.Dev.Identity.Value}" :
#endif
        "WorldCopy");

    internal static string Folder(WorldCopyManifest manifest)
    {
        var world = new string(manifest.World.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return Path.Combine(Root, $"{manifest.Host}_{(world.Length > 64 ? world.Substring(0, 64) : world)}");
    }

    internal static string ToLocal(string dir, string path)
    {
        return Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    ///     What comes from the host names files: nothing that could land outside the folder of the copy
    /// </summary>
    internal static bool IsSafe(WorldCopyManifest manifest)
    {
        // a date that DateTime cannot hold would throw wherever the copy is told
        if (manifest.World is null || manifest.Saved < 0 || manifest.Saved > DateTime.MaxValue.Ticks ||
            manifest.Files is not { Length: > 0 and <= MaxFiles } files) {
            return false;
        }

        var total = 0L;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            if (file is null || !IsSafePath(file.Path) || file.Hash is not { Length: 64 } ||
                file.Size < 0 || (total += file.Size) > MaxBytes || !seen.Add(file.Path)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The host leaves out of its manifest what a guest would refuse
    /// </summary>
    internal static bool IsSafePath(string? path)
    {
        if (path is not { Length: > 0 and < 200 } || path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        foreach (var part in path.Split('/')) {
            if (part.Length == 0 || part is "." or ".." || part[^1] is '.' or ' ' ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.IndexOfAny(['\\', ':']) >= 0 ||
                IsReservedName(part)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Windows device names, with or without an extension ("nul", "NUL.txt", "com1.save"): a file of that
    ///     name cannot be created there
    /// </summary>
    private static bool IsReservedName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ');
        if (stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") {
            return true;
        }

        return stem.Length == 4 && stem[3] is >= '0' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The whole copies of a world, the one that counts first: the highest handover number, then the latest save
    /// </summary>
    internal static List<(string Dir, WorldCopyManifest Manifest)> Copies(string folder)
    {
        var copies = new List<(string Dir, WorldCopyManifest Manifest)>();
        if (!Directory.Exists(folder)) {
            return copies;
        }

        foreach (var dir in Directory.GetDirectories(folder, CopyPrefix + "*")) {
            try {
                // written last, once every file was checked: a folder without it is not a copy
                var manifest = JsonConvert.DeserializeObject<WorldCopyManifest>(File.ReadAllText(Path.Combine(dir, ManifestName)));
                if (manifest is not null && IsSafe(manifest)) {
                    copies.Add((dir, manifest));
                }
            } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
                // noexcept
            }
        }

        copies.Sort((a, b) => a.Manifest.Handover != b.Manifest.Handover
            ? b.Manifest.Handover.CompareTo(a.Manifest.Handover)
            : b.Manifest.Saved.CompareTo(a.Manifest.Saved));
        return copies;
    }

    /// <summary>
    ///     Every file of the copy is there, with its size and its hash. Reads them all: not on the game's thread
    /// </summary>
    internal static bool Verify(string dir, WorldCopyManifest manifest)
    {
        try {
            return manifest.Files.All(f => Matches(ToLocal(dir, f.Path), f));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    internal static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    private static string Hex(byte[] hash)
    {
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static bool Matches(string path, WorldCopyFile file)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Size) {
            return false;
        }

        using var sha = SHA256.Create();
        using var stream = info.OpenRead();
        return Hex(sha.ComputeHash(stream)) == file.Hash;
    }

    /// <summary>
    ///     The folder a whole copy of that save gets when it is kept
    /// </summary>
    internal static string DirOf(string folder, WorldCopyManifest manifest)
    {
        return Path.Combine(folder, $"{CopyPrefix}{manifest.Handover:D4}-{manifest.Saved}");
    }

    internal enum Closed
    {
        Kept,
        Failed,

        /// <summary>
        ///     A file taken from the previous copy is not what that copy says: nothing is taken from it again
        /// </summary>
        BadPrevious,
    }

    /// <summary>
    ///     Makes a whole copy of what was received: the files that did not change come from the previous copy,
    ///     every file is checked, and only then the folder gets its name, in one rename. Until then, and if
    ///     anything fails, the previous copy is the copy. Not on the game's thread
    /// </summary>
    /// <param name="received">Paths already in the incoming folder</param>
    /// <param name="previous">Folder of the copy the other files are taken from</param>
    internal static Closed Close(string folder, WorldCopyManifest manifest, HashSet<string> received, string? previous)
    {
        var incoming = Path.Combine(folder, Incoming);
        var result = Closed.Failed;

        try {
            foreach (var file in manifest.Files) {
                var path = ToLocal(incoming, file.Path);
                var own = received.Contains(file.Path);
                if (!own) {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (file.Size == 0) {
                        File.WriteAllBytes(path, []);
                    } else if (previous is not null && File.Exists(ToLocal(previous, file.Path))) {
                        File.Copy(ToLocal(previous, file.Path), path, true);
                    }
                }

                if (!Matches(path, file)) {
                    result = own ? Closed.Failed : Closed.BadPrevious;
                    throw new InvalidDataException($"{file.Path} is not the file of the host");
                }
            }

            // what an earlier copy left unfinished in there
            var wanted = new HashSet<string>(manifest.Files.Select(f => Path.GetFullPath(ToLocal(incoming, f.Path))),
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(incoming, "*", SearchOption.AllDirectories)) {
                if (!wanted.Contains(Path.GetFullPath(path))) {
                    File.Delete(path);
                }
            }

            File.WriteAllText(Path.Combine(incoming, ManifestName), JsonConvert.SerializeObject(manifest, Formatting.Indented));

            var target = DirOf(folder, manifest);
            if (Directory.Exists(target)) {
                Directory.Delete(target, true);
            }

            Directory.Move(incoming, target);
            result = Closed.Kept;

            // (Saved is within DateTime's range: IsSafe)
            EmpLog.Information("World copy kept: {Files} files, {Bytes} bytes, handover {Handover}, saved {Saved:u}, in {Dir}",
                manifest.Files.Length, manifest.Files.Sum(f => f.Size), manifest.Handover,
                new DateTime(manifest.Saved, DateTimeKind.Utc), target);

            // never the one just written, whatever its number: a copy of an older handover sorts after the others
            var written = Path.GetFullPath(target);
            foreach (var old in Copies(folder).Skip(CopiesKept)) {
                if (!string.Equals(Path.GetFullPath(old.Dir), written, StringComparison.OrdinalIgnoreCase)) {
                    Directory.Delete(old.Dir, true);
                }
            }
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
            if (result != Closed.Kept) {
                EmpLog.Warning("World copy not kept, the previous one stays: {Why}", ex.Message);
            }
        }

        if (result == Closed.Kept) {
            return result;
        }

        try {
            if (Directory.Exists(incoming)) {
                Directory.Delete(incoming, true);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // noexcept
        }

        return result;
    }
}
