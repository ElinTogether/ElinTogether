using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using HeathenEngineering.SteamworksIntegration;

namespace ElinTogether.Helper;

/// <summary>
///     Container access log (council 2026-10-28, host box "Container access log", off by default): the host records,
///     per container of a player's or team's land (and the bank and delivery boxes), who put or took what and when,
///     the last <see cref="Keep" /> entries, in its save. Every put or take, a guest's replayed on the host or the
///     host's own player's, goes through Card.AddThing there, so that is the one place it is seen. The land's team and
///     the admins read it from the container's right-click menu; the admins also from the Elin Together menu
/// </summary>
[HarmonyPatch]
internal static class ContainerLog
{
    private const int Keep = 50;

    // containers remembered at most: the oldest goes first
    private const int KeepContainers = 500;

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules
    /// </summary>
    internal static bool On => NetSession.Instance.Transport is ElinNetHost ? EmpConfig.Server.ContainerAccessLog.Value
        : NetSession.Instance.Rules.ContainerLog;

    // a land: a map a player claimed, or a team holds
    private static bool IsLand(Zone zone) => zone.IsPCFaction || Teams.TeamOfZone(zone) != Teams.Wanderers;

    /// <summary>
    ///     The container this card lies in or is, when its log is kept: placed on a land, or one of the boxes of the bank
    ///     and the delivery. Null for anything a character carries
    /// </summary>
    private static Thing? Kept(Card? card)
    {
        if (card?.GetRootCard() is not Thing { } root) {
            return null;
        }

        return root.parent is Zone zone ? IsLand(zone) ? root : null : ShippingHelper.OtherWorldBox(root) != 0 ? root : null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.AddThing), typeof(Thing), typeof(bool), typeof(int), typeof(int))]
    internal static void BeforeAdd(Card __instance, Thing t, out (Thing? From, Chara? Who, int Num)? __state)
    {
        __state = null;
        // (the cheap tests first: every inventory gesture of the game passes here)
        if (NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } || !EmpConfig.Server.ContainerAccessLog.Value ||
            t.parent == __instance) {
            return;
        }

        var before = t.GetRootCard();
        __state = (Kept(t.parent as Card), before as Chara, t.Num);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.AddThing), typeof(Thing), typeof(bool), typeof(int), typeof(int))]
    internal static void AfterAdd(Card __instance, Thing t, (Thing? From, Chara? Who, int Num)? __state)
    {
        if (__state is not { } was || (t.parent != __instance && !t.isDestroyed)) {
            return;
        }

        // taken: out of a kept container into a player's bag
        if (was.From is { } from && __instance.GetRootCard() is Chara { IsPlayer: true } taker && Kept(__instance) is null) {
            Record(from, taker, -was.Num, t);
        } else if (was.Who is { IsPlayer: true } giver && Kept(__instance) is { } into) {
            // put: from a player's bag into a kept container
            Record(into, giver, was.Num, t);
        }
    }

    private static void Record(Thing box, Chara who, int signed, Thing item)
    {
        var date = EClass.world.date;
        var line = $"{date.year}-{date.month:00}-{date.day:00} {date.hour:00}h\t{Clean(who.NameSimple)}\t{signed}\t{Clean(item.NameSimple)}";
        var lines = ElinNetHost.ContainerLogs.TryGetValue(box.uid, out var old) ? old.Split('\n').ToList() : [];
        lines.Add(line);
        if (lines.Count > Keep) {
            lines.RemoveRange(0, lines.Count - Keep);
        }

        var logs = ElinNetHost.ContainerLogs;
        if (logs.Count >= KeepContainers && !logs.ContainsKey(box.uid) && logs.Keys.First() is var oldest) {
            logs.Remove(oldest);
            ElinNetHost.ContainerLogNames.Remove(oldest);
        }

        logs[box.uid] = string.Join("\n", lines);
        var where = box.parent is Zone zone ? zone.ZoneFullName : "";
        ElinNetHost.ContainerLogNames[box.uid] = Clean(where.Length > 0 ? $"{box.NameSimple} ({where})" : box.NameSimple);
    }

    // (one line, one field: a name with a tab or a line break would split an entry)
    private static string Clean(string text)
    {
        return new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }

    /// <summary>
    ///     On the host: this player reads the log of that container (0: the list of containers). Admins read every
    ///     one; the members of the land's team read those of their land
    /// </summary>
    internal static bool MayRead(Chara? reader, ulong user, int uid)
    {
        if (!EmpConfig.Server.ContainerAccessLog.Value) {
            return false;
        }

        if (user == ServerAvatar.HostUser || ElinNetHost.Admins.ContainsKey(user)) {
            return true;
        }

        if (uid == 0 || reader is null || CardCache.Find(uid) is not Thing box || box.GetRootCard() is not Thing { parent: Zone zone }) {
            return false;
        }

        // (the land's team, or the player in no team whose own land it is)
        var owner = Teams.LandOwnerOf(zone);
        return owner != 0 && owner == StoryOwner.Of(reader.uid);
    }

    internal static ContainerLogAnswer Answer(int uid)
    {
        if (uid == 0) {
            var ids = ElinNetHost.ContainerLogNames.Keys.ToArray();
            return new() {
                Uid = 0,
                Title = "",
                Lines = ids.Select(i => ElinNetHost.ContainerLogNames[i]).ToArray(),
                Ids = ids,
            };
        }

        return new() {
            Uid = uid,
            Title = ElinNetHost.ContainerLogNames.TryGetValue(uid, out var name) ? name : "",
            Lines = ElinNetHost.ContainerLogs.TryGetValue(uid, out var text) ? text.Split('\n') : [],
        };
    }

    /// <summary>
    ///     Ask for the log of that container (0: the list). The host reads its own; a guest asks the host
    /// </summary>
    internal static void Ask(int uid)
    {
        switch (NetSession.Instance.Transport) {
            case ElinNetHost host:
                if (MayRead(EClass.pc, ServerAvatar.HostUser, uid)) {
                    Display(Answer(uid));
                }

                break;
            case ElinNetClient client:
                client.Delta.AddRemote(new ContainerLogRequest { Uid = uid });
                break;
        }
    }

    internal static void Display(ContainerLogAnswer answer)
    {
        var lines = answer.Uid == 0
            ? answer.Lines.ToList()
            : answer.Lines.Select(Format).ToList();
        if (lines.Count == 0) {
            lines.Add("emp_container_log_empty".lang());
        }

        var list = EClass.ui.AddLayer<LayerList>();
        list.SetList2(Enumerable.Range(0, lines.Count).ToList(), i => lines[i], (i, _) => {
            if (answer.Uid == 0 && i < answer.Ids.Length) {
                Ask(answer.Ids[i]);
            }
        }, (_, _) => { }, answer.Uid == 0);
        list.SetSize(520f);
        list.SetHeader(answer.Uid == 0 ? "emp_container_logs_title" : answer.Title);
    }

    // date, who, signed count, item -> a line in the player's language
    private static string Format(string stored)
    {
        var part = stored.Split('\t');
        if (part.Length < 4 || !int.TryParse(part[2], out var count)) {
            return stored;
        }

        return (count < 0 ? "emp_container_log_took" : "emp_container_log_put").Loc(part[0], part[1], System.Math.Abs(count).ToString(), part[3]);
    }

    /// <summary>
    ///     The right-click menu of a container of a land: "Access log", for those who may read it (the host checks again)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TraitContainer), nameof(TraitContainer.TrySetAct))]
    internal static void OnContainerAct(TraitContainer __instance, ActPlan p)
    {
        AddEntry(__instance.owner, p);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TraitDeliveryChest), nameof(TraitDeliveryChest.TrySetAct))]
    internal static void OnDeliveryChestAct(TraitDeliveryChest __instance, ActPlan p)
    {
        AddEntry(__instance.owner, p);
    }

    private static void AddEntry(Card owner, ActPlan p)
    {
        if (!On || NetSession.Instance.Transport is null || owner.parent is not Zone zone || !IsLand(zone) || !MayAsk(zone)) {
            return;
        }

        p.TrySetAct("emp_container_log".lang(), () => {
            Ask(owner.uid);
            return false;
        }, owner, CursorSystem.Container);
    }

    // the menu shows the line to those the host would answer: admins, the land's team (the host checks again)
    private static bool MayAsk(Zone zone)
    {
        if (PeerHosting.IAmAdmin()) {
            return true;
        }

        var owner = Teams.LandOwnerOf(zone);
        return owner != 0 && owner == StoryOwner.Of(EClass.pc.uid);
    }
}
