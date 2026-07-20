using BepInEx.Configuration;
using ReflexCLI.Attributes;
using UnityEngine;

namespace ElinTogether;

[ConsoleCommandClassCustomizer("emp")]
internal partial class EmpConfig
{
    internal static void Bind()
    {
        var config = EmpMod.Instance.Config;

        Policy.Verbose = config.Bind(
            "RuntimePolicy",
            "Verbose",
#if DEBUG || true
            // enabled for beta builds
            true,
#else
            false,
#endif
            "Verbose information that may be helpful(spamming) for debugging\n" +
            "Enabled for beta testing by default\n" +
            "Debug用的信息输出\n" +
            "Beta测试版默认启用\n" +
            "デバッグ用の詳細情報を出力(大量ログ発生の可能性あり)");

        Policy.Timeout = config.Bind(
            "RuntimePolicy",
            "Timeout",
            15f,
            new ConfigDescription(
                "Timeout in seconds for any requests\n" +
                "Retry attempts will not be made after timeout\n" +
                "网络请求的最大超时\n" +
                "超时后，将不会重新请求",
                new AcceptableValueRange<float>(1f, 60f)));

        Policy.Retries = config.Bind(
            "RuntimePolicy",
            "Retries",
            1,
            new ConfigDescription(
                "Retries attempts after a failed request\n" +
                "请求失败后的重试次数",
                new AcceptableValueRange<int>(0, 5)));

        Client.PingKeybind = config.Bind(
            "Client",
            "PingKeybind",
            KeyCode.P,
            "Keybind for pinging map\n" +
            "键盘键位用于标记一处地点");

        Server.SourceValidationSet = config.Bind(
            "Server",
            "SourceValidation",
            "",
            "Source validation sets.\n" +
            "  none (or empty): skip all validation\n" +
            "  sources: validate Elin source table checksums\n" +
            "  plugins: validate plugin DLL hashes\n" +
            "  files: validate configured file hashes\n" +
            "  all: enable all checks\n" +
            "Combinations: \"source,plugin\" \"plugin,\" etc.\n" +
            "File paths: append \":path1,path2\" after flags, e.g. \"file:Data/xxx.json\"\n" +
            "File must be the last set\n" +
            "源表校验类型组合：none=跳过 source=源表 plugin=插件DLL file=文件\n" +
            "file必须放置于最后");

        Server.StrictValidationMode = config.Bind(
            "Server",
            "StrictValidationMode",
            false,
            "Server & Client integrity validation mode.\n" +
            "Set to true to block any mismatching clients from joining\n" +
            "双端数据完整性校验模式\n" +
            "开启时禁止任何不匹配的客机加入");

        Server.SharedAverageSpeed = config.Bind(
            "Server",
            "SharedAverageSpeed",
            false,
            "Share an averaged speed for all players\n" +
            "Otherwise each player will have their own speed\n" +
            "所有玩家共享平均速度\n" +
            "否则所有人按各自速度行动");

        Server.TurnBasedCombat = config.Bind(
            "Server",
            "TurnBasedCombatMode",
            true,
            "Players take turns in combat\n" +
            "战斗中玩家轮流行动");

        Reload();
    }

    internal static class Policy
    {
        internal static ConfigEntry<float> Timeout { get; set; } = null!;
        internal static ConfigEntry<int> Retries { get; set; } = null!;
        internal static ConfigEntry<bool> Verbose { get; set; } = null!;
    }

    internal static class Client
    {
        internal static ConfigEntry<KeyCode> PingKeybind { get; set; } = null!;
    }

    internal static class Server
    {
        internal static ConfigEntry<string> SourceValidationSet { get; set; } = null!;
        internal static ConfigEntry<bool> StrictValidationMode { get; set; } = null!;
        internal static ConfigEntry<bool> SharedAverageSpeed { get; set; } = null!;
        internal static ConfigEntry<bool> TurnBasedCombat { get; set; } = null!;
    }
}