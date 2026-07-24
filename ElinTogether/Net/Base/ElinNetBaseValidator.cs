using System.Collections.Generic;
using ElinTogether.API.SourceValidation;

namespace ElinTogether.Net;

public partial class ElinNetBase
{
    protected ValidationFlags ValidFlags { get; set; } = ValidationFlags.All;
    protected Dictionary<string, string> SourceList { get; private set; } = [];
    protected Dictionary<string, string> PluginHashList { get; private set; } = [];
    protected List<string> ValidationFilePaths { get; set; } = [];

    public void CreateValidation()
    {
        ValidFlags = ValidationConfig.GetConfiguredFlags();
        ValidationFilePaths = ValidationConfig.GetConfiguredFilePaths();

        var oldList = SourceList;
        SourceList = SourceDataValidator.Default.GetValidation();
        PluginHashList = PluginDataValidator.Default.GetValidation();
        ActMappingValidator.Default.BuildActMapping();

        EmpLog.Debug("Created source validation rules: flags={Flags}, {Count} sources, {PluginCount} plugins, {FileCount} files",
            ValidFlags, SourceList.Count, PluginHashList.Count, ValidationFilePaths.Count);

        if (ValidFlags.HasFlag(ValidationFlags.Sources)) {
            foreach (var (sourceName, sha) in SourceList) {
                var oldSha = oldList.GetValueOrDefault(sourceName);
                var newSha = (oldSha == sha && oldSha is not null) ? "unchanged" : sha;
                EmpLog.Debug("{SourceData,-16}[{OldSourceDataSha}] -> [{NewSourceDataSha}]",
                    sourceName, oldSha, newSha);
            }
        }

        if (ValidFlags.HasFlag(ValidationFlags.Plugins)) {
            foreach (var (modId, hash) in PluginHashList) {
                EmpLog.Debug("Plugin {ModId,-40}[{ModPluginSha}]",
                    modId, hash);
            }
        }
    }

    protected List<string> GetValidationSourceNames()
    {
        if (!ValidFlags.HasFlag(ValidationFlags.Sources)) {
            return [];
        }

        return [..SourceList.Keys];
    }

    protected List<string> GetValidationFilePaths()
    {
        if (!ValidFlags.HasFlag(ValidationFlags.Files)) {
            return [];
        }

        return ValidationFilePaths;
    }
}