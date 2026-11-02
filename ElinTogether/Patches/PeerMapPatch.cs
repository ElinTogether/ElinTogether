using System;
using System.Collections.Generic;
using System.IO;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ElinTogether.Patches;

/// <summary>
///     In a session a map may have come from another game (a guest hands back the map it held, the host sends its own):
///     the game reads its "map" file with any type the text names (GameIO.jsReadGame), so a crafted one ran code in
///     the game that loaded it (security audit 2026-10-27). While a session runs, maps are read with the types of the
///     game and its mods only, as everything else a peer sends (NetTypeBinder), and a map is never bigger than the game
///     makes them. <br />
///     The same goes for a whole world that came from another game, loaded outside any session (security audit
///     2026-10-29: the copy a host leaves, taken over by Game.Load once the session is reset; the depot's world; the
///     replicated world of a guest), and for a save of the player's own that keeps a map a peer handed back: its
///     game.txt and its maps are read the same way, whenever they are loaded
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class PeerMapPatch
{
    // the game's biggest maps are 200-odd cells a side
    private const int MaxSize = 1024;

    // a map layer is at most MaxSize² cells of 4 bytes: what the game unpacks (IO.ReadLZ4) never says more than this
    private const int MaxUnwrapped = 64 * 1024 * 1024;

    // a game.txt of another game once unpacked (the game's own saves are a few dozen MB)
    internal const long MaxText = 512L * 1024 * 1024;

    // the saves of the mod's own names that hold another game's world (SaveDepot, PeerKeeper, OfficialWorld). Not
    // "world_": the game's own saves are world_1, world_2... (GameIO.GetNewId), and a solo game with other mods
    // was read with the strict types (review 2026-11-02); a world taken over (WorldTakeover, "world_N" too) carries
    // the marker below instead
    private static readonly string[] PeerSaveIds = [SaveDepot.WorldId, PeerKeeper.SaveId, "world_official"];

    // the guest's copy of the host's world ("world_emp", "world_emp_<identity>" on the bench, ResourceFetch)
    private const string GuestCopyPrefix = "world_emp";

    // written in a save of the player's own once a map from another game is in it (ZoneLeaseState)
    internal const string PeerDataMarker = "emp_peer_data";

    /// <summary>
    ///     What a peer may have written is in that save folder
    /// </summary>
    internal static bool IsPeerSave(string? id, string? root)
    {
        if (id is not null && (Array.IndexOf(PeerSaveIds, id) >= 0 || id.StartsWith(GuestCopyPrefix, StringComparison.Ordinal))) {
            return true;
        }

        try {
            return root is not null && File.Exists(Path.Combine(root, PeerDataMarker));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return true;
        }
    }

    private static bool IsBound()
    {
        if (NetSession.Instance.Connection is not null) {
            return true;
        }

        try {
            return EClass.core?.game is not null && IsPeerSave(Game.id, GameIO.pathCurrentSave);
        } catch (Exception) {
            return true;
        }
    }

    /// <summary>
    ///     The save of the current game keeps a map from another game: it is read as such from then on
    /// </summary>
    internal static void MarkCurrentSave()
    {
        try {
            var root = GameIO.pathCurrentSave;
            if (!IsPeerSave(Game.id, root)) {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, PeerDataMarker), "maps from other games were saved here\n");
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not mark the save as holding maps from other games");
        }
    }

    private static ISerializationBinder? Bind()
    {
        var before = GameIO.jsReadGame.SerializationBinder;
        GameIO.jsReadGame.SerializationBinder = NetTypeBinder.Lenient;
        return before;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    internal static void OnActivate(out ISerializationBinder? __state)
    {
        __state = IsBound() ? Bind() : null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    internal static void OnActivateEnd(ISerializationBinder? __state)
    {
        if (__state is not null) {
            GameIO.jsReadGame.SerializationBinder = __state;
        }
    }

    internal sealed record LoadState(ISerializationBinder? Game, ISerializationBinder? General, Dictionary<string, string> Fallbacks);

    /// <summary>
    ///     A world of another game: its game.txt and index.txt with the types of the game and its mods only, and the
    ///     fallback types its index names (ModUtil.fallbackTypes, which the game's binder resolves to any type) are
    ///     forgotten once it is read
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameIO), nameof(GameIO.LoadGame))]
    internal static void OnLoadGame(string id, string root, out LoadState? __state)
    {
        __state = null;
        if (NetSession.Instance.Connection is null && !IsPeerSave(id, root)) {
            return;
        }

        __state = new(Bind(), IO.jsReadGeneral.SerializationBinder, new(ModUtil.fallbackTypes));
        IO.jsReadGeneral.SerializationBinder = NetTypeBinder.Instance;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GameIO), nameof(GameIO.LoadGame))]
    internal static void OnLoadGameEnd(LoadState? __state)
    {
        if (__state is null) {
            return;
        }

        GameIO.jsReadGame.SerializationBinder = __state.Game;
        IO.jsReadGeneral.SerializationBinder = __state.General;
        ModUtil.fallbackTypes.Clear();
        foreach (var (type, other) in __state.Fallbacks) {
            ModUtil.fallbackTypes[type] = other;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Map), nameof(Map.Load))]
    internal static void OnMapLoad(Map __instance)
    {
        if (IsBound() && __instance.Size is < 0 or > MaxSize) {
            throw new InvalidDataException($"Map of size {__instance.Size} refused (at most {MaxSize})");
        }
    }

    /// <summary>
    ///     LZ4Codec.Unwrap makes an array of the size the file's first 4 bytes say (up to 2 GB) before anything else:
    ///     a map file of another game saying more than a map holds is not read (the map loads empty there)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(IO), nameof(IO.ReadLZ4), [typeof(byte[])])]
    internal static bool OnReadLZ4(byte[] bytes, ref byte[] __result)
    {
        if (bytes is not { Length: >= 8 }) {
            return true;
        }

        var size = BitConverter.ToInt32(bytes, 0);
        if (size is >= 0 and <= MaxUnwrapped || !IsBound()) {
            return true;
        }

        EmpLog.Warning("A map file saying {Size} bytes once unpacked was not read (at most {Max})", size, MaxUnwrapped);
        __result = [];
        return false;
    }

    /// <summary>
    ///     IO.Decompress reads a whole game.txt with no limit: a world of another game says how much it unpacks to
    ///     only when it is read. Past MaxText it is refused (the load fails, nothing is kept)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(IO), nameof(IO.Decompress), [typeof(string)])]
    internal static bool OnDecompress(string path, ref string __result)
    {
        if (!IsBound()) {
            return true;
        }

        try {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var lz4 = new LZ4Bytes.CappedStream(new LZ4.LZ4Stream(file, LZ4.LZ4StreamMode.Decompress), MaxText);
            using var reader = new StreamReader(lz4);
            __result = reader.ReadToEnd();
            return false;
        } catch (InvalidDataException ex) when (ex.Message.StartsWith("Decompressed data over", StringComparison.Ordinal)) {
            EmpLog.Warning("A save of another game unpacking to more than {Max} bytes was refused: {Path}", MaxText, path);
            __result = "";
            return false;
        } catch (Exception) {
            // not LZ4 (plain text) or unreadable: the game's own way, which falls back to the text
            return true;
        }
    }
}
