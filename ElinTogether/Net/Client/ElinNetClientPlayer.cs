using System;
using ElinTogether.Common;
using ElinTogether.Models;
using UnityEngine.Events;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    /// <summary>
    ///     Net event: Local character creation requested
    /// </summary>
    private void OnSessionNewPlayerRequest(SessionNewPlayerRequest request)
    {
        EmpLog.Information("Received new player creation request");

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

        var probeGame = probe.MakeGameSave();

        core.game = probeGame;
        Game.id = "world_emp";

        var remoteChara = Session.Player = game.cards.globalCharas.Find(probe.RemoteCharaUid);

        player.uidChara = remoteChara.uid;
        player.chara = remoteChara;

        probeGame.isCloud = false;
        probeGame.isLoading = true;
        probeGame.OnGameInstantiated();
        probeGame.OnLoad();

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

        // do an initial zone request to load in
        RequestZoneState(MapDataRequest.CurrentRemoteZone);

        EmpPop.Debug("emp_wait_zone".lang());

        probeGame.isLoading = false;
    }

    /// <summary>
    ///     Net event: Join steam lobby if not already in it
    /// </summary>
    private void OnSteamLobbyRequest(SteamLobbyRequest request)
    {
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
        IsJoiningLobby = true;
    }
}