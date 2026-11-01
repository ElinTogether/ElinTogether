using System.Linq;
using System;
using System.Collections.Generic;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Models;
using UnityEngine.Events;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    /// <summary>
    ///     Net event: Local character creation requested
    /// </summary>
    /// <summary>
    ///     Net event: this player already has characters in that world, which one to play?
    /// </summary>
    private void OnSessionCharaSelectRequest(SessionCharaSelectRequest request)
    {
        EmpLog.Information("Received character choice, {Count} character(s)", request.Charas.Count);

        // the host is back and waits for the player's pick: joining again has done its job. Left running, its hard end
        // (45 s after a host that left) reset the session under the choice screen (hostdrop_away_check H2, 2026-10-29)
        NetReconnect.Stop();

        // PreparePlayerJoin
        AdvanceHandshake(NetHandshakePhase.Joined);

        ShowCharaChoice(request, request.Notice);
    }

    /// <summary>
    ///     A row of <see cref="ShowRowList" />: a character (or save) to pick, an action under them, or a gap between the two
    /// </summary>
    private record ChoiceRow(string Text, Action? Click, bool IsAction = false, string? Portrait = null);

    /// <summary>
    ///     The game's picture of that portrait id, when this game has it (a portrait from a mod the player lacks: none)
    /// </summary>
    private static UnityEngine.Sprite? PortraitSprite(string? id)
    {
        if (id.IsEmpty()) {
            return null;
        }

        try {
            return Portrait.modPortraits.GetItem(id, true)?.GetObject();
        } catch (Exception ex) {
            EmpLog.Debug(ex, "No picture for portrait {Id}", id);
            return null;
        }
    }

    /// <summary>
    ///     The game's own list window (like the barter or the moongate list), one button per row.
    ///     Closing it without having clicked a row runs <paramref name="onCancel" />
    /// </summary>
    private void ShowRowList(string title, List<ChoiceRow> rows, Action onCancel)
    {
        var chosen = false;
        var layer = ui.AddLayer<LayerList>().SetSize(520f).ManualList((list, l) => {
            list.callbacks = new UIList.Callback<ChoiceRow, ItemGeneral>
            {
                onInstantiate = (row, item) => {
                    // a character's row shows its portrait beside its name, as the game's lists of people do
                    if (PortraitSprite(row.Portrait) is { } face) {
                        item.button1.icon.sprite = face;
                        item.button1.icon.preserveAspect = true;
                        item.button1.icon.rectTransform.sizeDelta = new(40f, 40f);
                        item.DisableMask();
                    } else {
                        item.DisableIcon();
                    }

                    // actions sit under the characters, in another colour
                    item.button1.mainText.SetText(row.Text, row.IsAction ? FontColor.Topic : FontColor.Default);
                    item.Build();
                    if (row.Click is not { } click) {
                        // the gap between the characters and the actions
                        item.button1.interactable = false;
                        return;
                    }

                    item.button1.SetOnClick(() => {
                        chosen = true;
                        l.Close();
                        click();
                    });
                },
                onList = _ => {
                    foreach (var row in rows) {
                        list.Add(row);
                    }
                },
            };
        }).SetHeader(title);
        layer.SetOnKill(() => {
            if (!chosen) {
                onCancel();
            }
        });
    }

    private void ShowCharaChoice(SessionCharaSelectRequest request, string? notice = null)
    {
        var rows = request.Charas
            // (dead in a permadeath world: shown, waiting for a revive at a bartender, not playable; in a solo save:
            // shown, not playable until it comes back)
            .Select(c => c.Away is { } away
                ? new ChoiceRow(c.Label + " " + "emp_ui_chara_away_tag".lang(away), null, Portrait: c.Portrait)
                : c.Dead
                    ? new ChoiceRow(c.Label + " " + "emp_ui_chara_dead_tag".lang(), null, Portrait: c.Portrait)
                    : new ChoiceRow(c.Label, () => Host.Send(new SessionCharaSelectResponse { Uid = c.Uid }), Portrait: c.Portrait))
            .ToList();

        // then, in this order: a character from one of my saves (if the host allows it), a new character (while
        // under the host's limit of characters). A first-time player's list starts here, with no gap above
        if (rows.Count > 0) {
            rows.Add(new ChoiceRow("", null));
        }

        if (request.AllowImport && !request.AtLimit) {
            rows.Add(new ChoiceRow("emp_ui_chara_import".lang(), () => ShowSaveChoice(request), true));
        }

        if (!request.AtLimit) {
            rows.Add(new ChoiceRow("emp_ui_chara_new".lang(), () => Host.Send(new SessionCharaSelectResponse { Uid = 0 }), true));
        }

        // then deleting one of them, when the host allows it
        if (request.AllowDelete && request.Charas.Any(c => c.Away is null)) {
            rows.Add(new ChoiceRow("emp_ui_chara_delete".lang(), () => ShowDeleteChoice(request), true));
        }

        // taking one to a solo save, when the host allows it; bringing back one that is there, always
        if (request.AllowExport && request.Charas.Any(c => c.Away is null && !c.Dead)) {
            rows.Add(new ChoiceRow("emp_ui_chara_export".lang(), () => ShowExportChoice(request), true));
        }

        if (request.Charas.Any(c => c.Away is not null)) {
            rows.Add(new ChoiceRow("emp_ui_chara_recall".lang(), () => ShowRecallChoice(request), true));
        }

        var title = "emp_ui_chara_pick".lang();
        if (!notice.IsEmpty()) {
            title = notice.lang() + " " + title;
        }

        ShowRowList(title, rows, () => Socket.Disconnect(Host, EmpDisconnectInfo.ClientCancel));
    }

    /// <summary>
    ///     Which of its own saves to bring the character of. Closing the list goes back to the first choice
    /// </summary>
    /// <summary>
    ///     Which character to delete, then "really?": back to the list for anything but yes
    /// </summary>
    private void ShowDeleteChoice(SessionCharaSelectRequest request)
    {
        var rows = request.Charas.Where(c => c.Away is null).Select(target => new ChoiceRow(target.Label, () =>
            Dialog.YesNo("emp_ui_chara_delete_confirm".lang(target.Label), () => {
                Host.Send(new SessionCharaSelectResponse {
                    Uid = target.Uid,
                    Delete = true,
                });
            }, () => ShowCharaChoice(request, request.Notice)))).ToList();
        ShowRowList("emp_ui_chara_delete".lang(), rows, () => ShowCharaChoice(request, request.Notice));
    }

    /// <summary>
    ///     Which character goes to a solo save, then which save, then "really?" (the save's own character is
    ///     replaced, a copy of the save is kept): back to the list for anything but yes
    /// </summary>
    private void ShowExportChoice(SessionCharaSelectRequest request)
    {
        var rows = request.Charas.Where(c => c.Away is null && !c.Dead)
            .Select(target => new ChoiceRow(target.Label, () => ShowExportSaveChoice(request, target)))
            .ToList();
        rows.Add(new ChoiceRow("", null));
        rows.Add(new ChoiceRow("emp_ui_chara_import_back".lang(), () => ShowCharaChoice(request), true));
        ShowRowList("emp_ui_chara_export_pick".lang(), rows, () => ShowCharaChoice(request));
    }

    // the character and the save the player chose, until the host's copy comes (OnSessionCharaExportData)
    private (int Uid, GameIndex Save)? _pendingExport;

    private void ShowExportSaveChoice(SessionCharaSelectRequest request, SessionCharaEntry target)
    {
        var saves = CharaImport.Saves();
        if (saves.Count == 0) {
            ShowCharaChoice(request, "emp_ui_chara_import_none");
            return;
        }

        var rows = saves.Select(save => new ChoiceRow(CharaImport.Label(save), () =>
            Dialog.YesNo("emp_ui_chara_export_confirm".lang(target.Label, CharaImport.Label(save)), () => {
                _pendingExport = (target.Uid, save);
                EmpLog.Information("Taking chara {Uid} to save {Id}", target.Uid, save.id);
                Host.Send(new SessionCharaExportRequest {
                    Uid = target.Uid,
                    Save = CharaImport.Label(save),
                });
            }, () => ShowCharaChoice(request, request.Notice)))).ToList();
        rows.Add(new ChoiceRow("", null));
        rows.Add(new ChoiceRow("emp_ui_chara_import_back".lang(), () => ShowCharaChoice(request), true));
        ShowRowList("emp_ui_chara_export_save".lang(), rows, () => ShowCharaChoice(request));
    }

    /// <summary>
    ///     Which character in a solo save to bring back as it left, then "really?"
    /// </summary>
    private void ShowRecallChoice(SessionCharaSelectRequest request)
    {
        var rows = request.Charas.Where(c => c.Away is not null).Select(target => new ChoiceRow(target.Label, () =>
            Dialog.YesNo("emp_ui_chara_recall_confirm".lang(target.Label), () => {
                // the host frees the character: its copy waiting beside a solo save must not come in again
                CharaExport.DropIncoming(target.Uid);
                Host.Send(new SessionCharaExportRequest {
                    Uid = target.Uid,
                    Recall = true,
                });
            }, () => ShowCharaChoice(request, request.Notice)))).ToList();
        rows.Add(new ChoiceRow("", null));
        rows.Add(new ChoiceRow("emp_ui_chara_import_back".lang(), () => ShowCharaChoice(request), true));
        ShowRowList("emp_ui_chara_recall".lang(), rows, () => ShowCharaChoice(request));
    }

    /// <summary>
    ///     Net event: the host's copy of the character that leaves. A copy of the solo save first, then the character
    ///     beside it; the host is told either way (not written: it never left)
    /// </summary>
    private void OnSessionCharaExportData(SessionCharaExportData data)
    {
        var asked = _pendingExport is { } pending && pending.Uid == data.Uid ? pending.Save : null;
        _pendingExport = null;
        if (asked is null) {
            EmpLog.Warning("Received chara {Uid} to take to a solo save, which this player did not ask for", data.Uid);
        }

        var written = asked is not null && CharaExport.Write(asked, data);
        Host.Send(new SessionCharaExportResult {
            Uid = data.Uid,
            Token = data.Token,
            Written = written,
        });
    }

    private void ShowSaveChoice(SessionCharaSelectRequest request)
    {
        var saves = CharaImport.Saves();
        if (saves.Count == 0) {
            ShowCharaChoice(request, "emp_ui_chara_import_none");
            return;
        }

        var rows = saves.Select(save => new ChoiceRow(CharaImport.Label(save), () => {
            if (CharaImport.Read(save) is not { } imported) {
                ShowCharaChoice(request, "emp_ui_chara_import_fail");
                return;
            }

            EmpLog.Information("Bringing {Name} from save {Source}", imported.Chara.Name, imported.Source);
            Host.Send(new SessionCharaImportResponse {
                Chara = LZ4Bytes.Create(imported.Chara),
                Fame = imported.Fame,
                Karma = imported.Karma,
                Source = imported.Source,
            });
        })).ToList();
        rows.Add(new ChoiceRow("", null));
        rows.Add(new ChoiceRow("emp_ui_chara_import_back".lang(), () => ShowCharaChoice(request), true));
        ShowRowList("emp_ui_chara_import_pick".lang(), rows, () => ShowCharaChoice(request));
    }

    private void OnSessionNewPlayerRequest(SessionNewPlayerRequest request)
    {
        EmpLog.Information("Received new player creation request");

        // PreparePlayerJoin
        AdvanceHandshake(NetHandshakePhase.Joined);

        ui.RemoveLayer<LayerEditBio>();
        var embark = ui.AddLayer<LayerEditBio>();
        var content = embark.GetComponentInChildren<Content>();

        // disable mode selection
        content.transform.Find("Mode").SetActive(false);

        // swap out the click event delegate
        var ready = false;
        var button = content.transform.Find("ButtonEmbark")!.GetComponentInChildren<UIButton>();
        button.onClick.SetPersistentListenerState(0, UnityEventCallState.Off);
        button.onClick.AddListener(() => {
            Host.Send(request.Ready());
            game.Kill();
            ready = true;
            ui.RemoveLayer(embark);
            core.game = null;
        });
        embark.SetOnKill(() => {
            if (!ready) {
                Socket.Disconnect(Host, EmpDisconnectInfo.ClientCancel);
            }
        });
    }

    /// <summary>
    ///     Net event: Save probe received after connection.
    /// </summary>
    private void OnSaveDataProbe(SaveDataProbe probe)
    {
        EmpLog.Information("Received save data from host");

        // the map this game stands on while the world is replaced under it (GitHub issue 46): when the copy places it
        // on that same map, the cover goes away at once instead of the 4 s fade of a first join
        _coverZone = core.IsGameStarted && _zone is { } standing ? standing.uid : 0;

        // on the host link: back from independent travel, the probe rebuilds the whole game
        // on a zone session: joining another player's zone, the probe is its world
        // also when refused as a guest right after leaving the host map: not away yet, but coming back all the same
        var returning = (Session.IsAway || _rejoining) && !IsZoneSession;
        if (returning) {
            ReportReturn(probe.Game.Bytes.Length);

            // guests of our zone fall back to the host, see OnZoneSessionEnded
            Session.RemoveZoneSession();
            Session.IsGuest = false;
            Session.AwayZone = null;
            _pendingTravel = null;
            _pendingGrant = null;
            _rejoining = false;
            _handoffDeadline = 0;
            // the host answered a soft release with the world after all
            _softRejoinDeadline = 0;
            _softPlaced = 0;
            _softReturnZone = 0;
            _worldAskDeadline = 0;
            _localZones.Clear();
            StartWorldStateUpdate();
        }

        // PreparePlayerJoin
        AdvanceHandshake(NetHandshakePhase.Joined);

#if !DEBUG
        // ponytail: safety net, to remove once ElinNetBase.OnDestroy is fixed (dev/PLAN_journal_invite_6_octobre.md).
        // A release build takes every patch of the mod away when any of its components is destroyed, a zone
        // session closing next to this link included: the game then runs blind (nothing sent, nothing applied
        // between two frames, no travel asked). A frame later: a zone session closed above is destroyed at the
        // end of this one
        core.actionsNextFrame.Add(() => {
            if (!HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
                EmpLog.Warning("The patches of the mod were gone when the world arrived, put back");
                EmpMod.PatchAll();
            }
        });
#endif

        var equipped = new List<(int uid, int elementId)>();
        if (Session.Player is { } previous && core.game is not null) {
            foreach (var slot in previous.body.slots) {
                if (slot.thing is { } worn && !PendingUid.IsPending(worn.uid)) {
                    equipped.Add((worn.uid, slot.elementId));
                }
            }
        }

        // the open windows and the settings of this player go with the game: noted, see OwnSettings
        OwnSettings.Keep();

        // what this player's team knows: from the host of the world with its copy, kept from the game going away
        // when the copy is another player's zone (that player's team's)
        var lore = probe.Lore ?? (IsZoneSession ? TeamKnowledge.Carry() : null);
        // and where its team stands in the story, the same way
        var story = probe.Story ?? (IsZoneSession ? TeamOwnStory.Carry() : null);

        if (returning || (IsZoneSession && core.IsGameStarted)) {
            // the cards of the game going away are no host cards: destroying them must not reach the host
            // (our companions travelled with us, their copies here would destroy the host ones)
            CardCache.Reset();

            // tear down the away game and its scene like the title screen does before a first join,
            // widgets and actors would otherwise keep reading a game without active zone
            scene.Init(Scene.Mode.None);
        }

        var probeGame = probe.MakeGameSave();

        core.game = probeGame;
        Game.id = ResourceFetch.EmpSaveId;

        // what the others do from now on is not in this copy: kept until the map is loaded. The characters of
        // the players and what they carry are in the world, not in the map: nothing else brings them up to date
        Delta.HoldForIncomingMap(true);
        // a map received for the game that just went away is no reason to refuse the one of this world
        _awaitingActivation = 0;

        // the away zone of a guest now lives in the world of the zone owner
        if (IsZoneSession && Session.AwayZone is { } away) {
            Session.AwayZone = game.spatials.Find(away.uid) ?? away;
        }

        var remoteChara = Session.Player = game.cards.globalCharas.Find(probe.RemoteCharaUid);

        player.uidChara = remoteChara.uid;
        player.chara = remoteChara;
        player.RefreshDomain();

        // before the game reads them (widgets, window places): this player's own settings, not the host's
        OwnSettings.Carry(remoteChara);
        PlayerTitleDelta.Carry(remoteChara);

        probeGame.isCloud = false;
        probeGame.isLoading = true;
        probeGame.OnGameInstantiated();
        probeGame.OnLoad();

        // (after the game's own load fixes, which wrote in the copy's lists)
        if (lore is not null) {
            TeamKnowledge.Put(player, lore);
        }

        if (story is not null) {
            TeamOwnStory.Put(story);
        }

        // this player's own random quests and standing, in place of the host's
        PersonalQuests.OnWorldLoaded();

        // ability fake card
        try {
            foreach (var (uid, elementId) in equipped) {
                if (remoteChara.things.Find(uid) is { isDestroyed: false } worn &&
                    worn.c_equippedSlot == 0 &&
                    remoteChara.body.slots.Find(s => s.elementId == elementId && s.thing is null) is { } slot) {
                    remoteChara.body.Equip(worn, slot, false);
                }
            }

            foreach (var chara in game.cards.globalCharas.Values) {
                if (chara != remoteChara) {
                    InvPlaceAbilityDelta.InvalidateFakeAbilityCard(chara);
                }
            }

            foreach (var slot in InvPlaceAbilityDelta.Parse(remoteChara.GetStr(InvPlaceAbilityDelta.LayoutKey))) {
                CardBlueprint.SetNormalRarity();
                var ab = ThingGen.Create("ability");
                CardCache.UndoDestroy(ab);
                remoteChara.AddThing(ab, false, slot.InvX, slot.InvY);
                ab.c_idAbility = slot.Alias;
                ab.invX = slot.InvX;
                ab.invY = slot.InvY;
            }

            WidgetCurrentTool.dirty = true;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Failed to restore equipment or ability layout after save probe");
        }

        ui.RemoveLayer<LayerTitle>();
        ui.ShowCover();
        //scene.Init(Scene.Mode.StartGame);
        player.zone = null;
        core.actionsNextFrame.Add(LayerTitle.KillActor);

        // no zone request: the host sent its map right behind the world, taken at the same moment, so that
        // nothing happens between the two copies (see ElinNetHost.SendSaveProbe). Should that map never come, it
        // is asked for once
        StartCoroutine(AskMissingMap(++_mapAwaited));

        EmpPop.Debug("emp_wait_zone".lang());

        probeGame.isLoading = false;
    }

    /// <summary>
    ///     Net event: Join steam lobby if not already in it
    /// </summary>
    private void OnSteamLobbyRequest(SteamLobbyRequest request)
    {
        if (IsLocalConnection || IsDirectConnection) {
            // joining the lobby would reconnect through steam and drop the local connection
            EmpLog.Debug("Skipping steam lobby {LobbyId} on local connection",
                request.LobbyId);
            return;
        }

        EmpLog.Information("Connecting to steam lobby {LobbyId}",
            request.LobbyId);

        if (Session.Lobby.Current != request.LobbyId) {
            Session.Lobby.ConnectLobby(request.LobbyId);
        }
    }

    /// <summary>
    ///     Net event: Reconnect to host for full synchronization
    /// </summary>
    private void OnSessionReconnectRequest(SessionReconnectRequest request)
    {
        EmpLog.Information("Reconnecting to steam lobby {LobbyId}",
            request.LobbyId);

        // Disconnect triggers OnPeerDisconnected → RemoveComponent → LeaveLobby
        Socket.Disconnect(Host, EmpDisconnectInfo.JoinWhileConnected);
        CoroutineHelper.Deferred(() => Session.Lobby.ConnectLobby(request.LobbyId));
    }

    public void ReconnectSelf()
    {
        var lobby = Session.Lobby.Current;
        if (!lobby.IsValid) {
            EmpLog.Warning("Cannot reconnect: not in a lobby");
            return;
        }

        EmpLog.Information("Manual reconnect to steam lobby {LobbyId}",
            lobby);

        // Disconnect triggers OnPeerDisconnected → RemoveComponent → LeaveLobby
        Socket.Disconnect(Host, EmpDisconnectInfo.HostReconnectRequest);
        CoroutineHelper.Deferred(() => Session.Lobby.ConnectLobby(lobby));
    }

    public void TryJoinCurrentLobbyGame()
    {
        var lobby = Session.Lobby.Current;
        if (IsConnected) {
            OnSessionReconnectRequest(new() { LobbyId = lobby });
            return;
        }

        EmpLog.Information("Joining game on steam lobby {LobbyId}",
            lobby);

        _lastTimeout = DateTime.Now;
        // the lobby is entered: the normal no-answer timer takes over
        ClearLobbyJoinRequested();
        IsJoiningLobby = true;
    }
}