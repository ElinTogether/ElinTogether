using System.Collections.Generic;
using System.Text;
using ElinTogether.API.SourceValidation;
using ElinTogether.Common;
using ElinTogether.LangMod;
using ElinTogether.Models;
using UnityEngine;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    private static readonly Color _colorMissing = new(0.55f, 0.05f, 0.05f);
    private static readonly Color _colorExtra = new(0.05f, 0.35f, 0.45f);
    private static readonly Color _colorChanged = new(0.45f, 0.05f, 0.40f);

    public SourceValidationFailed? PendingMismatch { get; private set; }

    private void ContinueJoin()
    {
        if (PendingMismatch is null) {
            return;
        }

        EmpLog.Information("Client chose to continue despite validation mismatches.");
        Host.Send(new SourceValidationContinue());
        PendingMismatch = null;
    }

    private void DisconnectDueToMismatch()
    {
        if (PendingMismatch is null) {
            return;
        }

        EmpLog.Information("Client chose to disconnect due to validation mismatches.");
        PendingMismatch = null;

        Socket.Disconnect(Host, EmpDisconnectInfo.InvalidSource);
    }

    /// <summary>
    ///     Net event: Host requested source checksums
    /// </summary>
    private void OnSourceValidationRequest(SourceValidationRequest request)
    {
        EmpLog.Debug("Received source validation request: {Count} sources, {FileCount} files, flags={Flags}",
            request.SourceNames.Count, request.FilePaths.Count, (ValidationFlags)request.ValidationFlags);

        Host.Send(SourceValidationResponse.Create(request.SourceNames, request.FilePaths));
    }

    /// <summary>
    ///     Net event: Host reports validation mismatches
    /// </summary>
    private void OnSourceValidationFailed(SourceValidationFailed failed)
    {
        PendingMismatch = failed;

        EmpLog.Warning(BuildMismatchLogText(failed));

        var text = BuildMismatchDialogText(failed);
        Dialog.YesNo(text, ContinueJoin, DisconnectDueToMismatch);
    }

    private static string BuildMismatchLogText(SourceValidationFailed failed)
    {
        var total = failed.ActMismatches.Count + failed.SourceMismatches.Count +
                    failed.PluginMismatches.Count + failed.FileMismatches.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"Source validation failed — {total} mismatches");

        AppendAll("Acts", failed.ActMismatches);
        AppendAll("Sources", failed.SourceMismatches);
        AppendAll("Plugins", failed.PluginMismatches);
        AppendAll("Files", failed.FileMismatches);

        return sb.ToString();

        void AppendAll(string category, List<SourceValidationMismatch> list)
        {
            if (list.Count == 0) {
                return;
            }

            sb.AppendLine($"  [{category}] {list.Count} mismatch(es):");
            foreach (var m in list) {
                sb.Append("    ");
                sb.Append($"{MismatchTypeToLogPrefix(m.MismatchType),-10}");
                sb.Append(' ');
                sb.AppendLine(m.Entry);
            }
        }

        string MismatchTypeToLogPrefix(SourceValidationMismatch.SourceMismatchType type)
        {
            return type switch {
                SourceValidationMismatch.SourceMismatchType.MissingOnClient => "[MISSING]",
                SourceValidationMismatch.SourceMismatchType.ExtraOnClient => "[EXTRA]",
                SourceValidationMismatch.SourceMismatchType.HashChanged => "[DIFF]",
                _ => "[?]",
            };
        }
    }

    private static string BuildMismatchDialogText(SourceValidationFailed failed)
    {
        var total = failed.ActMismatches.Count + failed.SourceMismatches.Count +
                    failed.PluginMismatches.Count + failed.FileMismatches.Count;

        var sb = new StringBuilder();
        sb.AppendLine("emp_ui_source_mismatch_header".Loc(total));

        AppendCategory("emp_ui_source_mismatch_acts".lang(), failed.ActMismatches);
        AppendCategory("emp_ui_source_mismatch_sources".lang(), failed.SourceMismatches);
        AppendCategory("emp_ui_source_mismatch_plugins".lang(), failed.PluginMismatches);
        AppendCategory("emp_ui_source_mismatch_files".lang(), failed.FileMismatches);

        sb.AppendLine();
        sb.Append("emp_ui_source_mismatch_continue".lang());

        return sb.ToString();

        void AppendCategory(string header, List<SourceValidationMismatch> mismatches)
        {
            if (mismatches.Count == 0) {
                return;
            }

            sb.AppendLine();
            sb.AppendLine(header);

            var visible = mismatches.Count <= 5
                ? mismatches
                : mismatches.GetRange(0, 5);

            foreach (var m in visible) {
                sb.Append("  ");
                sb.AppendLine(MismatchLine(m));
            }

            if (mismatches.Count > 5) {
                var fold = "emp_ui_source_mismatch_more".Loc(mismatches.Count - 5);
                sb.Append("  ... ");
                sb.AppendLine(fold.TagColor(Color.gray));
            }
        }

        string MismatchLine(SourceValidationMismatch m)
        {
            var icon = MismatchTypeToIcon(m.MismatchType);
            var color = MismatchTypeToColor(m.MismatchType);
            return $"{icon} {m.Entry}".TagColor(color);
        }

        string MismatchTypeToIcon(SourceValidationMismatch.SourceMismatchType type)
        {
            return type switch {
                SourceValidationMismatch.SourceMismatchType.MissingOnClient => "[-]",
                SourceValidationMismatch.SourceMismatchType.ExtraOnClient => "[+]",
                SourceValidationMismatch.SourceMismatchType.HashChanged => "[~]",
                _ => "[?]",
            };
        }

        Color MismatchTypeToColor(SourceValidationMismatch.SourceMismatchType type)
        {
            return type switch {
                SourceValidationMismatch.SourceMismatchType.MissingOnClient => _colorMissing,
                SourceValidationMismatch.SourceMismatchType.ExtraOnClient => _colorExtra,
                SourceValidationMismatch.SourceMismatchType.HashChanged => _colorChanged,
                _ => Color.gray,
            };
        }
    }
}