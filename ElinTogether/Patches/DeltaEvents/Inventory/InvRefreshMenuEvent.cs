using System.Collections.Generic;
using ElinTogether.Models;
using ElinTogether.Net;

namespace ElinTogether.Patches;

/// <summary>
///     The settings of a container of the map are changed from many places of its window (the menu of the sort
///     button and its sub menus, the dialogs they open once the menu is gone: filter, name, colour, icon, paste;
///     the shared button). Instead of following each of them, the settings of every container whose window is open
///     are looked at each frame, and what changed is told to the other games (InvSaveDataDelta) <br />
///     Before, only the storage rules were told, and only from the menu: a name, an icon, a size or a sort set by
///     a guest lived in its copy of the world and were gone with it
/// </summary>
internal static class InvSettingsWatch
{
    // container uid -> its settings as last seen here or received
    private static readonly Dictionary<int, string> _seen = [];

    // container uid -> the raw values its State was last made from: while none moved, the State is the same and is
    // not made again (it is a text of some twenty values, made every frame for every open container: perf batch
    // 2026-11-03). The State text still decides what is a change
    private static readonly Dictionary<int, Probe> _probes = [];

    /// <summary>For the bench: how many times a State text was made</summary>
    internal static int StatesMade;

    private sealed class Probe
    {
        private readonly int[] _ints = new int[20];
        private readonly HashSet<int> _cats = [];
        private uint _bits;
        private string? _filter;
        private string? _altName;
        private int _icon;
        private bool _set;

        /// <summary>
        ///     Whether every value the State reads is as last seen (<see cref="Take" />)
        /// </summary>
        internal bool Same(Card container, Window.SaveData d)
        {
            return _set && _bits == d.b1.Bits && _filter == d.filter && _icon == container.c_indexContainerIcon &&
                   _altName == container.c_altName && SameInts(d.ints) && (d.cats is { } cats ? _cats.SetEquals(cats) : _cats.Count == 0);
        }

        /// <summary>
        ///     The values as last seen, once their State was made
        /// </summary>
        internal void Take(Card container, Window.SaveData d)
        {
            _set = true;
            _bits = d.b1.Bits;
            _filter = d.filter;
            _icon = container.c_indexContainerIcon;
            _altName = container.c_altName;
            for (var i = 0; i < _ints.Length; i++) {
                _ints[i] = d.ints is { } ints && i < ints.Length ? ints[i] : 0;
            }

            _cats.Clear();
            if (d.cats is { } now) {
                _cats.UnionWith(now);
            }
        }

        private bool SameInts(int[]? ints)
        {
            if (ints is null || ints.Length > _ints.Length) {
                return false;
            }

            for (var i = 0; i < _ints.Length; i++) {
                if (_ints[i] != (i < ints.Length ? ints[i] : 0)) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    ///     Called every frame while a session is on
    /// </summary>
    internal static void Tick()
    {
        // alone on a map of its own, the whole map goes back to the host with its containers
        if (NetSession.Instance.Connection is not { } connection || !EClass.core.IsGameStarted) {
            _seen.Clear();
            _probes.Clear();
            return;
        }

        var open = LayerInventory.listInv;
        if (open.Count == 0) {
            return;
        }

        // (containers seen long ago and destroyed since)
        if (_seen.Count > 256) {
            _seen.Clear();
            _probes.Clear();
        }

        for (var i = 0; i < open.Count; i++) {
            var layer = open[i];
            if (layer == null || layer.invs.Count == 0 || layer.invs[0] is not { } inv || inv.window == null ||
                inv.window.saveData is not { } data || inv.owner?.Container is not { } container ||
                !InvSaveDataDelta.IsOfTheMap(container)) {
                continue;
            }

            if (!_probes.TryGetValue(container.uid, out var probe)) {
                _probes[container.uid] = probe = new();
            }

            if (probe.Same(container, data) && _seen.ContainsKey(container.uid)) {
                continue;
            }

            StatesMade++;
            var now = InvSaveDataDelta.State(container, data);
            probe.Take(container, data);
            if (!_seen.TryGetValue(container.uid, out var was)) {
                _seen[container.uid] = now;
                continue;
            }

            if (was == now) {
                continue;
            }

            _seen[container.uid] = now;
            connection.Delta.AddRemote(InvSaveDataDelta.Of(container, data));
        }
    }

    /// <summary>
    ///     What another game changed is not a change of this one
    /// </summary>
    internal static void Received(Card container, Window.SaveData data)
    {
        _seen[container.uid] = InvSaveDataDelta.State(container, data);
        // (made again at the next look: the window may show these settings or not yet)
        _probes.Remove(container.uid);
    }
}
