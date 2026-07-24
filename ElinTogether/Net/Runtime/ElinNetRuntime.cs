using System.Collections.Generic;
using System.Text;
using ElinTogether.Helper;
using UnityEngine;

namespace ElinTogether.Net;

public partial class ElinNetBase
{
    public readonly Dictionary<string, int> DesyncInfos = [];

    private EGui? _debugGui;

    public int Desyncs { get; private set; }
    public bool IsDebugGuiActive => _debugGui is { IsKilled: false };

    public string GetDeltaStatsDump()
    {
        var sb = new StringBuilder();
        Delta.UpdateAverages();

        sb.AppendLine("Delta Stats");
        sb.AppendLine($"Batches: {Delta.BatchCount}");
        sb.AppendLine($"Avg Out: {Delta.AverageOut:F2}");
        sb.AppendLine($"Avg In: {Delta.AverageIn:F2}");

        var (outBuf, outDef, inBuf, inDef) = Delta.GetCounts();
        sb.AppendLine($"Out: {outBuf} (+{outDef} deferred)");
        sb.AppendLine($"In: {inBuf} (+{inDef} deferred)");
        sb.AppendLine($"Idle: {Delta.IsIdle}");
        sb.AppendLine($"Capturing: {Delta.IsCapturing} ({Delta.Snapshots.Count} snapshots)");
        sb.AppendLine($"Desyncs: {Desyncs}");

        if (Desyncs > 0) {
            foreach (var (info, count) in DesyncInfos) {
                sb.AppendLine($"+[{count}x] {info.Split('\n')[0]}");
            }
        }

        sb.Append(Delta.GetSnapshotSummary());

        return sb.ToString();
    }

    internal void ReportDesync(string desyncInfo)
    {
        Desyncs++;
        if (!DesyncInfos.TryAdd(desyncInfo, 1)) {
            DesyncInfos[desyncInfo]++;
        }
    }

    public void StartDebugGui()
    {
        StopDebugGui();

        _debugGui = EGui
            .CreatePopup(() => new(BuildDebugInfo()), _ => false, 1f)
            .OnAfterGUI(DrawDebugButtons);
    }

    public void StopDebugGui()
    {
        _debugGui?.Kill();
        _debugGui = null;
    }

    private string BuildDebugInfo()
    {
        var sb = new StringBuilder();

        if (!Socket.IsConnected) {
            sb.AppendLine("no connection");
        } else {
            foreach (var peer in Socket.Peers) {
                sb.AppendLine(peer.Colorize(peer.User.Name));
                sb.AppendLine(peer.Stat.ToStringSimplified());
            }
        }

        Delta.UpdateAverages();
        sb.Append($"Delta B=#{Delta.BatchCount} Out={Delta.AverageOut:F1} In={Delta.AverageIn:F1}");

        if (Desyncs > 0) {
            sb.Append($"Desync=<color=red>{Desyncs}</color>");
        }

        if (Delta.IsCapturing) {
            sb.Append($"Capturing last {Delta.Snapshots.Count} batches");
        }

        return sb.ToString();
    }

    private void DrawDebugButtons(EGui gui)
    {
        GUILayout.Space(2f);

        GUILayout.BeginHorizontal();
        {
            if (GUILayout.Button("Snap", gui.GUIStyle)) {
                if (!Delta.IsCapturing) {
                    Delta.StartCapture();
                }
                EmpLog.Debug("[Debug] Snapshot capture requested. Latest: {@Snapshot}",
                    Delta.GetLatestSnapshot());
            }

            if (GUILayout.Button("Dump", gui.GUIStyle)) {
                var stats = GetDeltaStatsDump();
                EmpLog.Information("[Debug] Delta stats dump:\n{Stats}", stats);
                EGui.CreatePopup(stats, 15f);
            }

            if (GUILayout.Button($"Capture: {(Delta.IsCapturing ? "On" : "Off")}", gui.GUIStyle)) {
                if (Delta.IsCapturing) {
                    Delta.StopCapture();
                } else {
                    Delta.StartCapture();
                }
            }

            if (GUILayout.Button("Summary", gui.GUIStyle)) {
                var summary = Delta.GetSnapshotSummary();
                EGui.CreatePopup(summary, 20f);
            }
        }
        GUILayout.EndHorizontal();

        if (Delta is { IsCapturing: true, Snapshots.Count: > 0 }) {
            var last = Delta.Snapshots[^1];
            var typeList = string.Join(", ", last.DeltaTypes);
            var info = $"Last #{last.Batch,-6} {last.DeltaTypes.Count,3} {last.Timestamp:HH:mm:ss.fff} [{typeList}]";
            GUILayout.Label(info, gui.GUIStyle);
        }
    }
}