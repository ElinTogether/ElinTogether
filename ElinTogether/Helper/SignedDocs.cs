using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElinTogether.Helper;

/// <summary>
///     The signed documents of a peer-hosted world (rules, admins, bans; dev/etworld/API.md 5.1), checked here without
///     trusting the service nor the keeper: each envelope carries the exact body an admin signed and that signature,
///     which must come from a key built into the mod (an admin key, never a player's). Only a higher number replaces
///     a document already held, so a stale list replayed as new is refused. Nothing of the game in here (the offline
///     test, dev/_tools/signed_docs_test.py, compiles this file alone with Ed25519.cs and EtWorldDepot.cs)
/// </summary>
internal sealed class SignedDocs
{
    internal static readonly string[] Kinds = ["rules", "admins", "bans"];

    internal const string SignedPath = "/v1/admin/doc";

    // replaced whole on a change, never changed in place: read from any thread without a lock
    private Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    internal string World { get; }

    internal SignedDocs(string world)
    {
        World = world;
    }

    /// <summary>
    ///     One document as it was checked: its number, its contents, and the envelope it came in (kept for the cache)
    /// </summary>
    internal sealed class Held
    {
        internal long Seq { get; init; }
        internal JObject Doc { get; init; } = new();
        internal JObject Envelope { get; init; } = new();
        internal string Signer { get; init; } = "";
    }

    internal Held? Get(string kind) => _held.TryGetValue(kind, out var held) ? held : null;

    internal long Seq(string kind) => Get(kind)?.Seq ?? 0;

    /// <summary>
    ///     Checks one envelope (GET /v1/w/{w}/doc/{kind}): null and the document when an admin key of
    ///     <paramref name="adminKeys" /> signed exactly this body for this world, kind and number; else why not
    /// </summary>
    internal static string? Verify(JObject envelope, IReadOnlyCollection<string> adminKeys, string world, string kind,
        out Held? held)
    {
        held = null;
        try {
            var auth = envelope["auth"] as JObject;
            if (auth is null) {
                return "no auth";
            }

            var key = ((string?)auth["key"] ?? "").ToLowerInvariant();
            if (!adminKeys.Contains(key)) {
                return "not signed by a built-in admin key";
            }

            if ((string?)auth["method"] != "POST" || (string?)auth["path"] != SignedPath) {
                return "signed for another request";
            }

            var time = (string?)auth["time"] ?? "";
            var nonce = (string?)auth["nonce"] ?? "";
            var sig = (string?)auth["sig"] ?? "";
            var body = Convert.FromBase64String((string?)envelope["body"] ?? "");
            using var sha = SHA256.Create();
            var message = $"ETW1\nPOST\n{SignedPath}\n{Hex(sha.ComputeHash(body))}\n{time}\n{nonce}";
            if (sig.Length != 128 || key.Length != 64 ||
                !Ed25519.Verify(FromHex(key), Encoding.UTF8.GetBytes(message), FromHex(sig))) {
                return "bad signature";
            }

            // the signed body itself says which world, kind and number it is: the envelope's own fields are not read
            var signed = JObject.Parse(Encoding.UTF8.GetString(body));
            if ((string?)signed["world"] != world) {
                return "signed for another world";
            }

            if ((string?)signed["kind"] != kind) {
                return "signed as another kind";
            }

            if (signed["seq"] is not { Type: JTokenType.Integer } seq || (long)seq < 1) {
                return "no number";
            }

            if (signed["doc"] is not JObject doc) {
                return "no document";
            }

            held = new() { Seq = (long)seq, Doc = doc, Envelope = envelope, Signer = key };
            return null;
        } catch (Exception ex) {
            // (anything unreadable fails closed: the document is not taken)
            return "unreadable: " + ex.GetType().Name;
        }
    }

    /// <summary>
    ///     Takes an envelope when it checks and is newer than the one held (the same number again: kept, no change).
    ///     Null when taken or the same, else why it was refused
    /// </summary>
    internal string? Offer(string kind, JObject envelope, IReadOnlyCollection<string> adminKeys)
    {
        if (!Kinds.Contains(kind)) {
            return "unknown kind";
        }

        if (Verify(envelope, adminKeys, World, kind, out var held) is { } why) {
            return why;
        }

        var current = Get(kind);
        if (current is not null && held!.Seq < current.Seq) {
            return $"older than the one held ({held.Seq} < {current.Seq})";
        }

        if (current is not null && held!.Seq == current.Seq) {
            // (two different bodies under one number cannot both come from the service, which takes seq + 1 only;
            // the first one checked stays)
            return null;
        }

        _held = new(_held, StringComparer.Ordinal) { [kind] = held! };
        return null;
    }

    /// <summary>
    ///     What to keep on the disk: the envelopes only, checked again when read back
    /// </summary>
    internal string ToCache()
    {
        var root = new JObject { ["world"] = World };
        foreach (var (kind, held) in _held) {
            root[kind] = held.Envelope;
        }

        return root.ToString(Formatting.None);
    }

    /// <summary>
    ///     Reads a cache made by ToCache, each envelope checked again (a cache edited on the disk counts for nothing).
    ///     The reasons of the envelopes refused
    /// </summary>
    internal List<string> FromCache(string text, IReadOnlyCollection<string> adminKeys)
    {
        var refused = new List<string>();
        try {
            var root = JObject.Parse(text);
            if ((string?)root["world"] != World) {
                refused.Add("cache of another world");
                return refused;
            }

            foreach (var kind in Kinds) {
                if (root[kind] is JObject envelope && Offer(kind, envelope, adminKeys) is { } why) {
                    refused.Add(kind + ": " + why);
                }
            }
        } catch (JsonException ex) {
            refused.Add("unreadable cache: " + ex.Message);
        }

        return refused;
    }

    // ------------------------------------------------------------------------------------- the documents' contents

    /// <summary>
    ///     A list of ids of a document ("admins", "moderators", "world", "host"), as texts (empty when missing)
    /// </summary>
    internal static string[] List(JObject? doc, string field)
    {
        return doc?[field] is JArray list ? [..list.Select(t => t.ToString().Trim()).Where(t => t.Length > 0).Distinct()] : [];
    }

    /// <summary>
    ///     Steam ids (decimal) of a list, "steam:" ids read as their number, keys of 64 hex left out
    /// </summary>
    internal static string[] SteamIds(JObject? doc, string field)
    {
        return [
            ..List(doc, field)
                .Select(t => t.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? t.Substring(6) : t)
                .Where(t => t.Length < 64 && ulong.TryParse(t, out var id) && id != 0)
                .Distinct(),
        ];
    }

    /// <summary>
    ///     The rules document as the admin page writes it (WorldAdmin.RulesFromThisGame):
    ///     {"rules": {"Server.Key": "value"}, "motd", "text", "build", "hash"}
    /// </summary>
    internal static (Dictionary<string, string> Settings, string Motd, string Text, string Build) Rules(JObject doc)
    {
        var settings = (doc["rules"] as JObject)?.Properties()
            .Where(p => p.Value.Type is not (JTokenType.Object or JTokenType.Array))
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        return (settings, (string?)doc["motd"] ?? "", (string?)doc["text"] ?? "", (string?)doc["build"] ?? "");
    }

    internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    internal static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0) {
            throw new FormatException("odd hex");
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++) {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }
}
