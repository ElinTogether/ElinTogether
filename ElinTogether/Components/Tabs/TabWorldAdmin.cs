using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using Newtonsoft.Json.Linq;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The admin pages of the peer-hosted public world (PLAN_peer_hosted_server step P6), two sub-tabs of World rules
///     shown only to an admin with the admin key on this PC (WorldAdmin.Shown), in a game and at the title: "World
///     versions" (the versions newest first, roll back to one, ban who kept one, the bans from keeping) and "Signed
///     lists" (this game's World rules signed as the world's, the admins and the bans). Nothing is sent without a
///     confirmation that says what will happen
/// </summary>
internal static class TabWorldAdmin
{
    private static readonly string[] Icons = ["icon_book", "icon_policy"];

    internal static List<(string, Action<YKVertical>)> Pages()
    {
        if (!WorldAdmin.Shown) {
            return [];
        }

        return [
            ("emp_wa_tab_versions".lang(), BuildVersions),
            ("emp_wa_tab_signed".lang(), BuildSigned),
        ];
    }

    /// <summary>
    ///     The state line every admin card starts with: no service, working, the last refusal, the last thing done
    /// </summary>
    private static bool Status(YKVertical card)
    {
        if (WorldAdmin.Address is null) {
            TabTeam.EmptyPage(card, Icons, "emp_wa_no_service".lang());
            return false;
        }

        // (read once when first shown; then on Refresh and after each change)
        if (!WorldAdmin.Loaded && !WorldAdmin.Busy && WorldAdmin.Problem.Length == 0) {
            WorldAdmin.Refresh();
        }

        if (WorldAdmin.Busy) {
            card.TextSmall("emp_wa_busy".lang(), FontColor.Gray);
        } else if (WorldAdmin.Problem.Length > 0) {
            card.TextSmall(WorldAdmin.Problem, FontColor.Bad);
        } else if (WorldAdmin.Done.Length > 0) {
            card.TextSmall(WorldAdmin.Done, FontColor.Good);
        }

        return true;
    }

    // ------------------------------------------------------------------------------------- versions

    private static void BuildVersions(YKVertical page)
    {
        var card = page.Card("emp_wa_versions".lang());
        if (!Status(card)) {
            return;
        }

        card.KeyValue("emp_wa_keeper".lang(), WorldAdmin.Keeper.Length > 0 ? WorldAdmin.Keeper : "emp_wa_keeper_none".lang());
        card.KeyValue("emp_wa_head".lang(), WorldAdmin.Head > 0 ? "#" + WorldAdmin.Head : "-");
        if (WorldAdmin.Flags.Count > 0) {
            var strong = WorldAdmin.Flags.Values.Count(f => f.Strong);
            card.TextSmall("emp_wa_flags_summary".Loc(WorldAdmin.Flags.Count, strong), strong > 0 ? FontColor.Bad : FontColor.Warning);
        }

        card.ButtonRow().RowButton("emp_wa_refresh".lang(), WorldAdmin.Refresh);
        card.Divider();

        var versions = WorldAdmin.Versions;
        if (versions.Count == 0) {
            card.TextSmall(WorldAdmin.Loaded ? "emp_wa_versions_none".lang() : "emp_wa_busy".lang(), FontColor.Gray);
        } else {
            // (a flagged version has one line more)
            var list = card.ListArea(versions.Count, 8, WorldAdmin.Flags.Count > 0 ? 72f : 56f);
            foreach (var v in versions) {
                VersionLine(list, v);
            }
        }

        BuildHostBans(page);
    }

    private static void VersionLine(YKLayout list, WorldAdmin.VersionRow v)
    {
        var row = list.Row();
        var reason = ("emp_wa_reason_" + v.Reason.Replace('-', '_')).lang();
        var title = $"#{v.N} · {When(v.Time)} · {reason}" + (v.RollbackOf is { } of ? " " + "emp_wa_of".Loc(of) : "") +
                    (v.N == WorldAdmin.Head ? " · " + "emp_wa_current".lang() : "");
        var who = v.Name.Length > 0 ? v.Name : v.Steam.Length > 0 ? v.Steam : "?";
        var detail = "emp_wa_version_detail".Loc(who, Size(v.Size), v.Count, v.GameDate.Length > 0 ? v.GameDate : "-");
        var column = row.TextColumn(title, detail);

        // guests' cross-check reports on it (plan 4.5): one player amber, two or more independent players red
        if (WorldAdmin.Flags.TryGetValue(v.N, out var flag)) {
            var text = (flag.Strong ? "emp_wa_flag_strong" : "emp_wa_flag").Loc(flag.Count, flag.Reporters, flag.Kinds) +
                       (flag.Detail.Length > 0 ? " · " + flag.Detail : "");
            column.TextSmall(text, flag.Strong ? FontColor.Bad : FontColor.Warning);
        }

        if (v.N != WorldAdmin.Head) {
            row.RowButton("emp_wa_rollback".lang(), () => AskRollback(v));
        }

        if (Bannable(v)) {
            row.RowButton("emp_wa_ban_keeping".lang(), () => AskBan(KeeperTargets(v), who));
        }
    }

    /// <summary>
    ///     A version a player kept (not one the service made for an admin), and not this admin's own
    /// </summary>
    private static bool Bannable(WorldAdmin.VersionRow v)
    {
        return v.Reason != "rollback" && v.Key.Length == 64 && v.Steam != WorldAdmin.MySteam.ToString() &&
               !WorldAdmin.HostBans.Any(b => b.Target == v.Key);
    }

    private static List<string> KeeperTargets(WorldAdmin.VersionRow v)
    {
        // (bans follow the key and the Steam id, plan 4.4)
        var targets = new List<string> { v.Key };
        if (v.Steam.Length > 0) {
            targets.Add("steam:" + v.Steam);
        }

        return targets;
    }

    /// <summary>
    ///     What a rollback does, said before it is done; who kept the versions it undoes may be banned in the same go
    /// </summary>
    private static void AskRollback(WorldAdmin.VersionRow to)
    {
        var undone = WorldAdmin.Versions.Where(v => v.N > to.N && Bannable(v)).ToList();
        var names = undone.Select(v => v.Name.Length > 0 ? v.Name : v.Steam).Distinct().ToList();
        var text = "emp_wa_rollback_confirm".Loc(to.N, When(to.Time), WorldAdmin.Head);
        Dialog.Choice(text, d => {
            d.AddButton("emp_wa_rollback_go".lang(), () => WorldAdmin.Rollback(to.N, []));
            if (names.Count > 0) {
                var shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? " …" : "");
                var targets = undone.SelectMany(KeeperTargets).Distinct().ToList();
                d.AddButton("emp_wa_rollback_ban".Loc(shown), () => WorldAdmin.Rollback(to.N, targets));
            }

            d.AddButton("cancel".lang());
        });
    }

    /// <summary>
    ///     A ban from keeping the world, confirmed with what it does
    /// </summary>
    internal static void AskBan(List<string> targets, string name)
    {
        Dialog.YesNo("emp_wa_ban_confirm".Loc(name), () => WorldAdmin.Ban(targets, true));
    }

    private static void BuildHostBans(YKVertical page)
    {
        var card = page.Card("emp_wa_hostbans".lang());
        var bans = WorldAdmin.HostBans;
        if (bans.Count == 0) {
            card.TextSmall("emp_wa_hostbans_none".lang(), FontColor.Gray);
        } else {
            var list = card.ListArea(bans.Count, 6, 48f);
            foreach (var ban in bans) {
                var row = list.Row();
                var target = ban.Target;
                row.TextColumn(NameOf(target), $"{When(ban.Time)}" + (ban.Reason.Length > 0 ? " · " + ban.Reason : ""));
                row.RowButton("emp_wa_unban".lang(), () => Dialog.YesNo("emp_wa_unban_confirm".Loc(NameOf(target)),
                    () => WorldAdmin.Ban([target], false)));
            }
        }

        card.ButtonRow().RowButton("emp_wa_ban_add".lang(), () => {
            var input = Dialog.InputName("emp_wa_ban_add_hint", "", (cancel, text) => {
                if (cancel || text.Trim().Length == 0) {
                    return;
                }

                if (WorldAdmin.Target(text) is not { } id) {
                    Dialog.Ok("emp_wa_bad_id");
                    return;
                }

                AskBan([id.Length == 64 ? id : "steam:" + id], NameOf(id));
            });
            // (a key is 64 characters)
            input.input.field.characterLimit = 80;
        });
    }

    // ------------------------------------------------------------------------------------- signed lists

    // the build a keeper must run, as the rules document will say it (kept over redraws, never stored)
    private static bool? _pinBuild;

    // the lists as the admin is changing them, by "kind.field", from the signed one of that number
    private static readonly Dictionary<string, List<string>> _draft = [];
    private static readonly Dictionary<string, long> _draftOf = [];
    // the list each draft started from: a draft the admin changed survives a newer signed list (a refusal "seq")
    private static readonly Dictionary<string, List<string>> _draftBase = [];

    private static readonly (string Kind, string Field, string Label)[] Lists = [
        ("admins", "admins", "emp_wa_list_admins"),
        ("admins", "moderators", "emp_wa_list_moderators"),
        ("bans", "host", "emp_wa_list_host"),
        ("bans", "world", "emp_wa_list_world"),
    ];

    private static void BuildSigned(YKVertical page)
    {
        var card = page.Card("emp_wa_rules".lang());
        if (!Status(card)) {
            return;
        }

        card.ButtonRow().RowButton("emp_wa_refresh".lang(), WorldAdmin.Refresh);
        BuildRules(card);
        foreach (var kind in new[] { "admins", "bans" }) {
            BuildDoc(page, kind);
        }
    }

    private static void BuildRules(YKVertical card)
    {
        var signed = WorldAdmin.Docs["rules"];
        var signedBuild = (string?)signed?["build"] ?? "";
        var pin = _pinBuild ?? signedBuild.Length > 0;
        var mine = WorldAdmin.RulesFromThisGame(pin ? ModInfo.BuildVersion : "");
        var differ = WorldAdmin.RulesDiffer(signed, mine);
        card.KeyValue("emp_wa_signed_no".lang(), WorldAdmin.Seq["rules"] > 0 ? "#" + WorldAdmin.Seq["rules"] : "emp_wa_none".lang());
        card.KeyValue("emp_wa_build".lang(), signedBuild.Length > 0 ? signedBuild : "emp_wa_build_any".lang());
        card.TextSmall(differ.Count == 0 ? "emp_wa_rules_same".lang()
            : "emp_wa_rules_differ".Loc(differ.Count, string.Join(", ", differ.Take(6)) + (differ.Count > 6 ? " …" : "")), FontColor.Gray);

        var buttons = card.ButtonRow();
        buttons.RowButton((pin ? "emp_wa_build_pin_on" : "emp_wa_build_pin_off").Loc(ModInfo.BuildVersion), () => {
            _pinBuild = !pin;
            LayerElinTogether.Instance?.Reopen();
        });
        if (differ.Count > 0) {
            buttons.RowButton("emp_wa_rules_sign".lang(), () => Dialog.YesNo("emp_wa_rules_confirm".Loc(differ.Count),
                () => WorldAdmin.PutDoc("rules", mine)));
        }

        card.TextSmall("emp_wa_rules_how".lang(), FontColor.Gray);
    }

    private static List<string> Draft(string kind, string field)
    {
        var key = kind + "." + field;
        var seq = WorldAdmin.Seq[kind];
        if (!_draft.TryGetValue(key, out var list) || _draftOf[key] != seq) {
            // a world without a signed list yet starts from the lists this game plays with now
            var from = WorldAdmin.Docs[kind] is { } doc ? WorldAdmin.Ids(doc, field) : key switch {
                "admins.admins" => [..PeerHosting.Current.AdminIds],
                "bans.world" => [..PeerHosting.Current.BannedIds],
                _ => [],
            };
            var edited = list is not null && _draftBase.TryGetValue(key, out var started) && !list.SequenceEqual(started);
            if (!edited) {
                list = [..from];
            }

            _draft[key] = list!;
            _draftBase[key] = from;
            _draftOf[key] = seq;
        }

        return list!;
    }

    private static bool Changed(string kind)
    {
        var doc = WorldAdmin.Docs[kind];
        return Lists.Where(l => l.Kind == kind).Any(l => doc is null
            ? Draft(kind, l.Field).Count > 0
            : !Draft(kind, l.Field).SequenceEqual(WorldAdmin.Ids(doc, l.Field)));
    }

    private static void BuildDoc(YKVertical page, string kind)
    {
        var card = page.Card(("emp_wa_doc_" + kind).lang());
        card.KeyValue("emp_wa_signed_no".lang(), WorldAdmin.Seq[kind] > 0 ? "#" + WorldAdmin.Seq[kind] : "emp_wa_none".lang());
        foreach (var (_, field, label) in Lists.Where(l => l.Kind == kind)) {
            var ids = Draft(kind, field);
            card.SectionHeader(label.lang());
            if (ids.Count == 0) {
                card.TextSmall("emp_wa_list_empty".lang(), FontColor.Gray);
            } else {
                var list = card.ListArea(ids.Count, 6, 40f);
                foreach (var id in ids.ToList()) {
                    var row = list.Row();
                    row.TextColumn(NameOf(id), id);
                    row.RowButton("emp_wa_remove".lang(), () => {
                        // (an admin never takes himself off the list: nobody could sign the next one from this PC)
                        if (field == "admins" && id == WorldAdmin.MySteam.ToString()) {
                            Dialog.Ok("emp_wa_remove_self");
                            return;
                        }

                        ids.Remove(id);
                        LayerElinTogether.Instance?.Reopen();
                    });
                }
            }

            card.ButtonRow().RowButton("emp_wa_add".lang(), () => {
                var input = Dialog.InputName("emp_wa_add_hint", "", (cancel, text) => {
                    if (cancel || text.Trim().Length == 0) {
                        return;
                    }

                    // (Steam ids only in these lists; a key is banned from keeping on the versions page)
                    if (WorldAdmin.Target(text) is not { Length: < 64 } id) {
                        Dialog.Ok("emp_wa_bad_id");
                        return;
                    }

                    if (!ids.Contains(id)) {
                        ids.Add(id);
                    }

                    LayerElinTogether.Instance?.Reopen();
                });
                input.input.field.characterLimit = 40;
            });
        }

        if (!Changed(kind)) {
            return;
        }

        var buttons = card.ButtonRow();
        buttons.RowButton("emp_wa_doc_sign".lang(), () => Dialog.YesNo(("emp_wa_doc_confirm_" + kind).lang(), () => {
            var doc = WorldAdmin.Docs[kind] is { } old ? (JObject)old.DeepClone() : new JObject();
            foreach (var (_, field, _) in Lists.Where(l => l.Kind == kind)) {
                doc[field] = new JArray(Draft(kind, field).Select(i => (object)i).ToArray());
            }

            WorldAdmin.PutDoc(kind, doc);
        }));
        buttons.RowButton("emp_wa_doc_undo".lang(), () => {
            foreach (var (_, field, _) in Lists.Where(l => l.Kind == kind)) {
                _draft.Remove(kind + "." + field);
                _draftBase.Remove(kind + "." + field);
            }

            LayerElinTogether.Instance?.Reopen();
        });
    }

    // ------------------------------------------------------------------------------------- texts

    /// <summary>
    ///     A Steam id or key as a name when one is known (the versions' keepers, the players here)
    /// </summary>
    private static string NameOf(string target)
    {
        var id = target.StartsWith("steam:") ? target.Substring(6) : target;
        if (WorldAdmin.Versions.FirstOrDefault(v => (v.Steam == id || v.Key == id) && v.Name.Length > 0) is { } v) {
            return $"{v.Name} ({Short(target)})";
        }

        if (ulong.TryParse(id, out var steam) && NetSession.Instance.CurrentPlayers.FirstOrDefault(p => (ulong)p.User == steam) is
                { } p && p.User.Name is { Length: > 0 } name) {
            return $"{name} ({Short(target)})";
        }

        return Short(target);
    }

    private static string Short(string target)
    {
        return target.Length == 64 ? "emp_wa_key".Loc(target.Substring(0, 8)) : target;
    }

    private static string When(long unix)
    {
        return unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";
    }

    private static string Size(long bytes)
    {
        return bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
