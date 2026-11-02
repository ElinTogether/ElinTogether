using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using ElinTogether.Net;
using ReflexCLI.Attributes;
using UnityEngine;

namespace ElinTogether;

[ConsoleCommandClassCustomizer("emp")]
internal partial class EmpConfig
{
    /// <summary>
    ///     A config file of the mod from before the council of 2026-10-23 on world map time has the checkbox
    ///     TimeJumpsTogether and no WorldMapTime: on is Travel Together, off was every step moving the date
    ///     (Shared Clock). A new install has neither and takes the default, Shared Clock
    /// </summary>
    internal static WorldMapTime? OldWorldMapTime(string configText)
    {
        if (Regex.IsMatch(configText, @"^\s*WorldMapTime\s*=", RegexOptions.Multiline)) {
            return null;
        }

        var old = Regex.Match(configText, @"^\s*TimeJumpsTogether\s*=\s*(\w+)", RegexOptions.Multiline);
        if (!old.Success) {
            return null;
        }

        return old.Groups[1].Value.Equals("true", System.StringComparison.OrdinalIgnoreCase)
            ? Net.WorldMapTime.TravelTogether
            : Net.WorldMapTime.SharedClock;
    }

    internal static void Bind()
    {
        var config = EmpMod.Instance.Config;

        // what an older version of the mod left in the file, read before any Bind saves it again
        var before = File.Exists(config.ConfigFilePath) ? File.ReadAllText(config.ConfigFilePath) : "";

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

        Client.DepotPath = config.Bind(
            "Client",
            "DepotPath",
            "",
            "Folder of the save depot: a folder every player of the group can reach (shared or synced), which keeps the world. Empty: no depot\n" +
            "The first player takes the world from it and hosts, every save goes back to it, the next player can take over\n" +
            "Also the address of Elin Together Server (host:55557), or a private GitHub repository: github:owner/repository\n" +
            "存档仓库文件夹：小组所有玩家都能访问的文件夹（共享或同步），用于保存世界。留空：不使用");

        Client.DepotPassword = config.Bind(
            "Client",
            "DepotPassword",
            "",
            "Password of the save depot, when it is Elin Together Server and one was set there\n" +
            "For a GitHub depot: the access key (fine-grained token, this repository only, Contents: read and write). Never share this file");

        Client.ServerAddress = config.Bind(
            "Client",
            "ServerAddress",
            "",
            "Address of the server joined last with \"Join by address\", as host:port (the port is 55556)\n" +
            "上次通过“按地址加入”连接的服务器地址，格式为 主机:端口（端口为 55556）");

        Client.RecordReplay = config.Bind(
            "Client",
            "RecordReplay",
            true,
            "Keep the last 30 seconds of the screen (one small picture a second, in memory only) and send them with a bug report\n" +
            "保留最近 30 秒的画面（每秒一张小图，仅保存在内存中），并随问题报告一起发送");

        Client.FetchMods = config.Bind(
            "Client",
            "FetchMods",
            true,
            "When the game joined (or the world taken from the depot) has Workshop mods that are not loaded here, download them without subscribing,\n" +
            "restart Elin with exactly the mods of that game for one session, and come back to the game by itself\n" +
            "Your own mod list is back at the next start. Untick it to keep your own mods: you are then only told which mods differ\n" +
            "These mods are chosen by the host (or by the modlist.txt of the depot). Only Elin mods of the Workshop are fetched, and mods with code (DLL) only with \"Also code mods (DLL)\"\n" +
            "这些模组由主机（或仓库的 modlist.txt）决定。只下载创意工坊上的 Elin 模组；含代码（DLL）的模组仅在勾选“也下载代码模组（DLL）”时下载\n" +
            "加入的游戏（或从仓库取得的世界）有本机未加载的创意工坊模组时：不订阅直接下载，以该游戏的模组重启 Elin 一次并自动回到游戏；下次启动恢复你自己的模组列表。取消勾选则保留自己的模组，只提示差异");

        // security: a mod with code (a DLL) runs that code on this PC, with all the rights of the player
        Client.FetchCodeMods = config.Bind(
            "Client",
            "FetchCodeMods",
            false,
            "Also fetch the mods of the game that contain code (a DLL file), only for hosts you trust\n" +
            "Code in a mod runs on this PC with all your rights: a host could name any Workshop item, even one it put there itself\n" +
            "Off: a game whose missing mods include code is not fetched; you are told which mod, and nothing on this PC changes\n" +
            "Only with \"Download the game's mods\"\n" +
            "同时下载包含代码（DLL 文件）的游戏模组，仅限你信任的主机。模组中的代码以你的全部权限在本机运行：主机可以指定任何创意工坊物品，包括它自己上传的。关闭时：缺少的模组含有代码时不下载，会告诉你是哪个模组，本机不做任何更改");

        // security: the hosts whose mods are fetched without asking (ModFetch), and the code mods of each
        // the player accepted already. Only on this PC, never in a world
        Client.TrustedHosts = config.Bind(
            "Client",
            "TrustedHosts",
            "",
            "Hosts whose game mods are downloaded without asking (\"Always trust this host\"), as steamid:codemod,codemod;steamid:...\n" +
            "You are still asked when a host names a mod with code (DLL) you have not accepted from it. Empty it to be asked again for every host\n" +
            "无需询问即可下载其游戏模组的主机（“始终信任此主机”），格式为 steamid:代码模组,代码模组;steamid:...。主机指定你尚未从它接受过的含代码（DLL）模组时仍会询问。清空即可对所有主机重新询问");

        Client.KeepMods = config.Bind(
            "Client",
            "KeepMods",
            false,
            "Keep the mods of the game you join: your Steam account subscribes to them on the Workshop and they stay on in your own mod list\n" +
            "Elin still restarts once, the first time. After that, joining the same game again needs no restart, even after closing Elin\n" +
            "Off: they are fetched without subscribing and your own list is back at each start, so Elin restarts at each first join\n" +
            "To undo: unsubscribe from them on the Workshop, or switch them off in the Mod Viewer. Only with \"Fetch the mods of the game by itself\"\n" +
            "保留所加入游戏的模组：你的 Steam 账号会在创意工坊订阅它们，并在你自己的模组列表中保持启用。第一次仍会重启一次，之后再次加入同一游戏无需重启。关闭时：不订阅直接获取，每次启动后第一次加入都会重启");

        // the mod version whose patch notes this player has seen (the Patch notes tab shows a badge until then)
        Client.PatchNotesSeen = config.Bind(
            "Client",
            "PatchNotesSeen",
            "",
            "The Elin Together version whose patch notes you have seen (set by itself when the Patch notes tab is opened)\n" +
            "你已查看过更新说明的 Elin Together 版本（打开“更新说明”标签页时自动设置）");

        // the update check (Helper.UpdateCheck): on by default, the last answer, the version already told about
        Client.CheckUpdates = config.Bind(
            "Client",
            "CheckUpdates",
            true,
            "Check on GitHub, at most every six hours, whether a newer Elin Together was released, and say so\n" +
            "每六小时最多在 GitHub 检查一次是否有更新的 Elin Together 版本，并提示");

        Client.UpdateLatest = config.Bind(
            "Client",
            "UpdateLatest",
            "",
            "The last update check, set by itself: time|version|page\n" +
            "上次检查更新的结果（自动设置）");

        Client.UpdateAnnounced = config.Bind(
            "Client",
            "UpdateAnnounced",
            "",
            "The newer version already announced by a message (set by itself)\n" +
            "已通过消息提示过的新版本（自动设置）");

        Client.ChatGameTime = config.Bind(
            "Client",
            "ChatGameTime",
            false,
            "The Chat tab shows the game's clock beside each line instead of your computer's\n" +
            "聊天标签页在每条消息旁显示游戏内时间，而不是电脑时间");

        Client.HideChatImages = config.Bind(
            "Client",
            "HideChatImages",
            false,
            "Pictures other players send in the chat are not shown (a line says one was sent)\n" +
            "不显示其他玩家在聊天中发送的图片（只显示一行提示）");

        Client.PingKeybind = config.Bind(
            "Client",
            "PingKeybind",
            KeyCode.P,
            "Keybind for pinging map\n" +
            "键盘键位用于标记一处地点");

        // the worlds (host and character) whose first-steps card this player has seen (Helper.FirstSteps)
        Client.FirstStepsSeen = config.Bind(
            "Client",
            "FirstStepsSeen",
            "",
            "The worlds whose first-steps card was shown, as hostSteamId:characterUid;... Empty it to see the card again\n" +
            "已显示过入门卡片的世界，格式为 主机SteamID:角色UID;...。清空即可再次显示");

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

        Server.IndependentTravel = config.Bind(
            "Server",
            "IndependentTravel",
            true,
            "Clients may travel to other zones on their own\n" +
            "The zone is simulated by that client and sent back to the host when leaving\n" +
            "客机可以独自前往其他地图，由该客机模拟，离开时回传给主机");

        Server.SameGameVersion = config.Bind(
            "Server",
            "SameGameVersion",
            false,
            "Refuse players whose version of Elin is not the host's\n" +
            "Off: they are let in and both are told; the same version of the mod is always required\n" +
            "拒绝游戏版本与主机不同的玩家");

        Server.PublicGame = config.Bind(
            "Server",
            "PublicGame",
            false,
            "Anybody who finds the game in the list of games can join it, not only the host's Steam friends and invited players\n" +
            "A game opened by itself (a depot, a shared world) still lets in only the players of its world\n" +
            "任何在游戏列表中找到该游戏的人都可以加入，而不仅是主机的 Steam 好友和受邀玩家；自动开启的游戏（仓库、共享世界）仍只允许其世界的玩家加入");

        Server.LookModsOwn = config.Bind(
            "Server",
            "LookModsOwn",
            false,
            "Mods that only change the look (pictures, portraits, sounds, translations: no code, no items, no maps) are not\n" +
            "asked of the players who join: nobody restarts for them, and every player keeps their own\n" +
            "Off: the players who join need every mod of the host\n" +
            "只改变外观的模组（图片、肖像、声音、翻译：没有代码、物品或地图）不要求加入的玩家安装：不会为此重启，每位玩家保留自己的");

        Server.ClientOnlyMods = config.Bind(
            "Server",
            "ClientOnlyMods",
            "",
            "Mods each player may have or not (comma-separated Elin mod ids, or Workshop numbers), e.g. display mods with code:\n" +
            "they never get a player refused, are never asked of anyone, and their DLLs are left out of the plugin check.\n" +
            "Only for mods that add no game data (items, characters, maps): the game data check still counts what they add\n" +
            "每位玩家可自行决定是否安装的模组（逗号分隔的 Elin 模组 id 或创意工坊编号），例如带代码的显示类模组：不会因此拒绝玩家，也不要求任何人安装，插件校验不计其DLL。仅适用于不添加游戏数据（物品、角色、地图）的模组");

        Server.PublishMods = config.Bind(
            "Server",
            "PublishMods",
            true,
            "The list of the mods of the game is shown to the players before they join (Steam lobby) and sent when they do\n" +
            "It is the list of the world when it came from a depot (modlist.txt), else the mods of the host\n" +
            "A player who is refused, or who takes the world, is told which mods differ. Otherwise nothing is said\n" +
            "游戏的模组列表会在玩家加入前显示（Steam 大厅）并在加入时发送；世界来自仓库时使用仓库的列表（modlist.txt），否则使用主机的模组。被拒绝或取得世界的玩家会被告知哪些模组不同");

        Server.PlayerCombatTime = config.Bind(
            "Server",
            "PlayerCombatTime",
            true,
            "Combat runs on each player's own time: what fights a player only acts when that player takes a turn\n" +
            "Nobody waits for the others. Takes over the turn-based combat mode when both are on\n" +
            "战斗按各玩家自己的时间进行：与某位玩家战斗的单位只在该玩家行动时行动，同时开启时优先于回合制战斗");

        Server.PlayerClock = config.Bind(
            "Server",
            "PlayerClock",
            true,
            "Every player walks and acts on the clock of its own game, as the host does\n" +
            "Otherwise a guest's steps follow the host's game time as it arrives over the network, which makes them uneven\n" +
            "Only with combat on each player's time\n" +
            "每位玩家按自己游戏的时钟移动和行动，与主机一样；否则客机的步伐取决于经网络传来的主机时间，会不均匀。需要开启“战斗按各玩家时间进行”");

        Server.PlayerStepPace = config.Bind(
            "Server",
            "PlayerStepPace",
            true,
            "Every player walks and acts at the pace of a solo game, whatever the speed of the other players\n" +
            "Otherwise the length of a step depends on the gap between the fastest and the slowest player\n" +
            "Speed still counts in a fight. Only with combat on each player's time\n" +
            "每位玩家按单人游戏的节奏移动和行动，不受其他玩家速度影响；否则每一步的时长取决于最快与最慢玩家的差距。战斗中速度仍然有效。需要开启“战斗按各玩家时间进行”");

        Server.SharedWorldTime = config.Bind(
            "Server",
            "SharedWorldTime",
            true,
            "One date for the whole world: time passed by a player alone on another map counts for everyone, the most advanced date is the world's\n" +
            "Otherwise only the host's date counts, and a player coming back from a map of its own jumps to it\n" +
            "整个世界共用一个日期：独自在其他地图的玩家所经过的时间对所有人都有效，最靠前的日期就是世界的日期；否则只以主机的日期为准");

        Server.WorldKeeper = config.Bind(
            "Server",
            "WorldKeeper",
            true,
            "What time does to the world (weather, expired quests, taxes, salaries, letters) is done once, by one game, the same for everyone\n" +
            "Otherwise every player holding a map runs it too in its own copy: a quest expires twice, each copy has its own weather\n" +
            "时间对世界的影响（天气、任务过期、税金、工资、信件）只由一个游戏处理一次，所有人相同；否则每个持有地图的玩家也会在自己的副本中各自处理");

        Server.PlayerKill = config.Bind(
            "Server",
            "PlayerKill",
            false,
            "A player (or its companions) can kill another player's character\n" +
            "Off: a strike that would kill it leaves it at 0 hit points\n" +
            "玩家（及其同伴）可以杀死其他玩家的角色；关闭时，致命一击只会让对方的生命值降为 0");

        Server.Duels = config.Bind(
            "Server",
            "Duels",
            true,
            "A player can challenge another player to a duel from the menu on its character: nobody dies, both are healed at the end, nothing is lost\n" +
            "Potions, arrows and charges used during the duel stay spent\n" +
            "玩家可以通过对方角色的菜单发起决斗：不会有人死亡，结束时双方恢复，没有任何损失；决斗中使用的药水、箭矢和充能不会返还");

        Server.DuelBets = config.Bind(
            "Server",
            "DuelBets",
            false,
            "Bets: the challenger of a duel names a stake in gold (none, 100, 1,000 or as much as the poorer of the two has), shown in the box the other one answers; both put it down, the winner takes both\n" +
            "The stakes go back to each one when the duel ends without a winner (a player leaves or drops out, the game stops); giving up loses them. Otherwise no stakes\n" +
            "赌注：发起决斗的一方指定金币赌注（无、100、1000 或两人中较少一方的全部金币），显示在对方回答的对话框中；双方各自押上，胜者全拿；决斗没有胜者时（玩家离开、掉线、游戏停止）赌注退还给各自，认输则失去赌注。关闭时没有赌注");

        Server.DuelArena = config.Bind(
            "Server",
            "DuelArena",
            false,
            "A duel the host fights is fought in an arena: when the challenge is accepted the host makes a field for it and goes there, the other one follows, and after the duel each one comes back to the tile it left\n" +
            "A duel between two other players is still fought where they stand. Otherwise every duel is fought where they stand\n" +
            "主机参与的决斗在竞技场中进行：接受挑战后主机创建一块场地并前往，另一方随后跟来，决斗结束后双方各自回到原来的位置；其他两名玩家之间的决斗仍在原地进行。关闭时所有决斗都在原地进行");

        Server.GuestBouts = config.Bind(
            "Server",
            "GuestBouts",
            true,
            "A guest can duel an adventurer as the host does: the adventurer leaves town for the bout and comes back healed, its week before the next bout is everyone's, a won bout counts and its reward is the adventurer's own\n" +
            "Otherwise the guest fights a copy of the adventurer in its own game and nothing of it counts in the world\n" +
            "其他玩家可以像主机一样与冒险者决斗：决斗期间冒险者离开城镇，结束后恢复返回，下次决斗前的一周对所有人相同，胜利会被记录，奖励来自冒险者本身；否则该玩家只是在自己的游戏中与冒险者的副本战斗，世界中不会留下任何结果");

        Server.GuestBuild = config.Bind(
            "Server",
            "GuestBuild",
            true,
            "The other players may use the build mode of the base (floors, walls, furniture, mining, cutting): the game of the host builds for them, they pay\n" +
            "Otherwise only the host builds\n" +
            "其他玩家可以使用据点的建造模式（地板、墙、家具、挖掘、砍伐）：由主机的游戏代为建造，费用由该玩家支付；否则只有主机可以建造");

        Server.HostManagesBase = config.Bind(
            "Server",
            "HostManagesBase",
            false,
            "Only the host manages the base: what the other players ask of it (research, hearth skills, policies, names, settings, residents, build mode) is refused\n" +
            "Otherwise every player manages the base as the host does (leaving the base for good stays the host's)\n" +
            "只有主机可以管理据点：其他玩家对据点的操作（研究、炉灶技能、政策、命名、设置、居民、建造模式）会被拒绝；否则每位玩家都可像主机一样管理据点（永久放弃据点仍只有主机可以）");

        Server.PlayerShipping = config.Bind(
            "Server",
            "PlayerShipping",
            true,
            "Every player is paid for the goods it puts in the shipping box, with its own shipping bonus\n" +
            "Otherwise the host gets everything\n" +
            "每位玩家各自获得自己放入出货箱物品的收入与出货奖励，否则全部归主机");

        Server.PersonalQuests = config.Bind(
            "Server",
            "PersonalQuests",
            true,
            "Random quests, fame and karma belong to the player who takes the quest, 5 quests each\n" +
            "Story quests stay shared. Otherwise there is one quest log and one fame for the whole group\n" +
            "随机任务、名声与业力归接任务的玩家所有，每人5个；主线任务仍然共享");

        Server.OwnJournal = config.Bind(
            "Server",
            "OwnJournal",
            true,
            "Each player's journal tells about that player: its own message history and its own records (digging, deaths, nights slept) stay with it from trip to trip\n" +
            "Otherwise a guest's journal shows the host's after each trip. The codex, kills and the world's dates stay shared\n" +
            "每位玩家的日志记录自己的经历：自己的消息记录与个人纪录（挖掘、死亡、睡眠次数）在旅途中保留；图鉴、击杀数与世界日期仍然共享");

        Server.PlayerTrade = config.Bind(
            "Server",
            "PlayerTrade",
            true,
            "Players next to each other can trade items and gold through a window both confirm\n" +
            "Otherwise the game's own menu opens the other player's bag, as for an ally\n" +
            "相邻的玩家可以通过双方确认的窗口交换物品和金币");

        Server.PermadeathRevive = config.Bind(
            "Server",
            "PermadeathRevive",
            true,
            "Permadeath worlds only: a dead player's character stays in the world and any player can revive it at a bartender, for the game's price; nothing is deleted.\n" +
            "Off: the game's own permadeath ending (the host's death deletes the world)\n" +
            "仅限永久死亡世界：死亡玩家的角色留在世界中，任何玩家都可以在酒保处支付游戏价格将其复活，不会删除任何存档。关闭：游戏原本的永久死亡结局（主机死亡会删除世界）");

        // not the "ChooseCharacter" key of earlier versions: every settings file already written holds it as true,
        // and a player who comes back is no longer asked at every connection
        Server.ChooseCharacter = config.Bind(
            "Server",
            "AskCharacter",
            false,
            "Every joining player is asked which of its characters in this world to play, or to make a new one\n" +
            "Otherwise it gets the character it played last, without a question\n" +
            "每位加入的玩家都会被询问使用自己在这个世界的哪个角色或新建角色，否则直接使用上次的角色");

        Server.AutoReconnect = config.Bind(
            "Server",
            "AutoReconnect",
            true,
            "A player who loses the connection joins the same game again by itself, every 5 seconds for 3 minutes\n" +
            "Not after leaving, being kicked or being refused. Otherwise it is left on the title screen\n" +
            "掉线的玩家会自动重新加入同一个游戏（每 5 秒一次，持续 3 分钟）；主动离开、被踢出或被拒绝时不会。关闭时回到标题画面");

        Server.AutoResync = config.Bind(
            "Server",
            "AutoResync",
            true,
            "A player whose copy of the map no longer matches the host's (a missing item or character) loads the map again by itself\n" +
            "At most once every 30 seconds, never during a fight, a task or with a menu open. Otherwise the difference is only written to the log\n" +
            "玩家的地图与主机不一致（缺少物品或角色）时，会自动重新载入地图；最多每 30 秒一次，战斗中、行动中或打开菜单时不会。关闭时只写入日志");

        Server.AutoSave = config.Bind(
            "Server",
            "AutoSave",
            true,
            "The world is saved by itself every 2 minutes while another player is in the game, and when the last one leaves\n" +
            "Every 5 minutes when a save takes more than half a second. Otherwise only when the host saves\n" +
            "有其他玩家在游戏中时，世界每 2 分钟自动保存一次，最后一位玩家离开时也会保存；保存超过半秒时改为每 5 分钟。关闭时只在主机保存时保存");

        Server.SharedTax = config.Bind(
            "Server",
            "SharedTax",
            true,
            "The monthly tax is computed on the highest fame among the connected players, not always on the host's\n" +
            "Otherwise on the host's fame, as the game does\n" +
            "每月税金按在线玩家中最高的名声计算，而不总是按主机的名声。关闭时按主机的名声计算（与原版相同）");

        Server.TownLife = config.Bind(
            "Server",
            "TownLife",
            true,
            "Townspeople chat, greet, gossip and throw stones near every player, not only near the host's screen; the gossip and the stones go by the karma of the player they are about\n" +
            "Otherwise only near the host and about the host's karma, as the game does in the host's game\n" +
            "镇民在每位玩家附近都会闲聊、打招呼、议论和扔石头，而不仅在主机的画面附近；议论和石头按所针对玩家自己的业报决定。关闭时只在主机附近，并按主机的业报（与原版相同）");

        Server.OncePerPlayer = config.Bind(
            "Server",
            "OncePerPlayer",
            true,
            "What the game gives once per world is given once to each player: what a shop sells only once, and the rewards of story quests and dialogs (a share for every player)\n" +
            "Otherwise the first player who buys it or earns it is the only one who has it, as the game does\n" +
            "游戏每个世界只给一次的东西，每位玩家都能得到一次：商店的限量商品，以及剧情任务和对话的奖励（每位玩家一份）。关闭时只有第一个购买或获得的玩家拥有（与原版相同）");

        Server.TownBoards = config.Bind(
            "Server",
            "TownBoards",
            true,
            "When the game draws the offers of a town board again, the highest fame among the players on the map opens the offers that need fame, and the difficulty is that of the players present (a guest counts like the host)\n" +
            "Otherwise the board is drawn for the host's character alone, as the game does\n" +
            "游戏重新生成城镇告示板时，地图上名声最高的玩家决定需要名声的委托是否出现，难度按在场玩家计算（访客与主机同等对待）。关闭时只按主机的角色生成（与原版相同）");
        Server.ShopsFromEveryPlayer = config.Bind(
            "Server",
            "ShopsFromEveryPlayer",
            true,
            "A shop restocks with the stock bonus of the best player on the map, and its special shelves (the marriage and tape shop, Loytel's mart, the exotic merchant) follow the player who opens it\n" +
            "Otherwise the host's feats and progress decide for everyone, as the game does\n" +
            "商店补货时按地图上最强玩家的进货加成计算，特殊货架（婚礼与磁带店、洛伊特商店、异国商人）取决于打开商店的玩家。关闭时一律按主机的天赋和进度（与原版相同）");
        Server.NearestSpawns = config.Bind(
            "Server",
            "NearestSpawns",
            true,
            "The feats that change what spawns on a map (legendary chance, friend or foe of Santa and the like, evolved, big daddy, shrines) are those of the player nearest to the spawn, not always the host's\n" +
            "Otherwise the host's, as the game does\n" +
            "影响地图刷怪的特性（传说怪几率、圣诞老人等特殊怪物的敌友、进化怪、大老爹、神殿）取离刷新点最近的玩家的，而不总是主机的。关闭时按主机的特性计算（与原版相同）");

        Server.AutoCombatPerPlayer = config.Bind(
            "Server",
            "AutoCombatPerPlayer",
            true,
            "Auto combat saves from a deadly hit the player who runs it and its own companions, with its own tactics and stamina: each player's auto combat protects its own, the host's no longer protects the others\n" +
            "Otherwise the host's auto combat protects everyone and a guest's protects no one, as the game does in the host's game\n" +
            "自动战斗会以玩家自己的战术和耐力，保护进行自动战斗的玩家及其同伴免受致命一击：每位玩家的自动战斗保护自己的人，主机的不再保护其他人。关闭时主机的自动战斗保护所有人，访客的不保护任何人");

        Server.SoftRecall = config.Bind(
            "Server",
            "SoftRecall",
            true,
            "When the host walks onto the map a player holds alone, that player stays on its map without loading the whole world again; when that player walks into the map the host is on, it loads that one map only\n" +
            "On since 0.26.690 (off before). Should anything not match, the world is loaded as before. Otherwise the world is loaded at each such meeting\n" +
            "主机走进某位玩家独自所在的地图时，该玩家留在原地图上，不再重新载入整个世界；该玩家走进主机所在的地图时，只载入那一张地图。自 0.26.690 起默认开启；如有任何不一致，仍会像以前一样载入世界。关闭时每次这样相遇都会重新载入世界");

        // (off was the default while it was new: a host who never chose gets it once, then its own choice stays)
        var softOnce = config.Bind("Server", "SoftRecallSwitchedOn", false,
            "Internal: the mod turned SoftRecall on once when it became the default");
        if (!softOnce.Value) {
            softOnce.Value = true;
            Server.SoftRecall.Value = true;
        }

        Server.WorldCopy = config.Bind(
            "Server",
            "WorldCopy",
            false,
            "After each save the game makes by itself, every other player's game keeps a copy of the world on its own disk, outside its saves\n" +
            "Sent in the background, a little at a time, only what changed. Otherwise the world is on the host's PC only\n" +
            "每次自动保存后，其他玩家的游戏会在自己的硬盘上保留一份世界副本（不在存档文件夹内）；在后台一点一点发送，只发送有变化的部分。关闭时世界只存在于主机的电脑上");

        Server.Takeover = config.Bind(
            "Server",
            "Takeover",
            false,
            "When the host leaves, another player opens the world from the copy its game keeps, and the game goes on there\n" +
            "Needs \"The other players keep a copy of the world\". New, off until it has been played more. Otherwise the game ends when its host leaves\n" +
            "主机离开时，由另一位玩家用自己电脑上保留的世界副本重新开放世界，游戏在那里继续。需要开启“其他玩家保留一份世界副本”。新功能，默认关闭；关闭时主机离开游戏即结束");

        Server.AutoHost = config.Bind(
            "Server",
            "AutoHost",
            true,
            "The game opens to the other players by itself when a world is loaded, without \"Start Server\"\n" +
            "Only friends can join, and the world needs a claimed land. Otherwise the host clicks \"Start Server\"\n" +
            "读取世界时游戏自动对其他玩家开放，无需点击“启动服务器”；只有好友可以加入，且世界需要已占领的土地。关闭时由主机点击“启动服务器”");

        Server.OwnSleep = config.Bind(
            "Server",
            "OwnSleep",
            true,
            "A player who goes to bed sleeps at once, for itself, without waiting for the others\n" +
            "The night only passes for the world when every player is asleep at the same time. Otherwise everyone waits for everyone\n" +
            "玩家上床后立刻为自己睡觉，不用等别人；只有所有玩家同时睡着时，世界才会过夜。关闭时所有人互相等待");

        Server.IdleMinutes = config.Bind(
            "Server",
            "IdleMinutes",
            5,
            "Minutes without keyboard, mouse or own act after which a player is away from the keyboard: it no longer holds the night, waits on the world map or moves the shared date. 0: off\n" +
            "玩家在多少分钟内没有键盘、鼠标或自身动作即视为离开：不再阻止夜晚过去、不再让世界地图等待、不再推进共享日期。0：关闭");

        Server.KickKeepOutMinutes = config.Bind(
            "Server",
            "KickKeepOutMinutes",
            5,
            "Minutes a kicked player cannot come back. 0: it may come back at once\n" +
            "被踢出的玩家在多少分钟内不能重新加入。0：可以立即重新加入");

        Server.MaxCharacters = config.Bind(
            "Server",
            "MaxCharacters",
            3,
            "How many characters one player may have in this world: past it, it cannot make or bring another until it deletes one. Characters already made are kept. 0: no limit\n" +
            "每名玩家在这个世界中最多可以拥有多少个角色：达到上限后，在删除一个之前不能再创建或导入角色。已有的角色会保留。0：不限");

        Server.MembersOnly = config.Bind(
            "Server",
            "MembersOnly",
            false,
            "Only the players of this world come in: those who have a character here, its admins, and those let in with allow. Steam friends and a public game's strangers too are refused\n" +
            "只有此世界的玩家可以加入：在这里有角色的玩家、管理员以及用 allow 放行的玩家。Steam 好友和公开游戏的陌生人也会被拒绝");

        Server.ContainerAccessLog = config.Bind(
            "Server",
            "ContainerAccessLog",
            false,
            "The host keeps, per container of a player's or team's land (and the bank and delivery boxes), who put or took what and when (the last 50 entries). The land's team and the admins read it from the container's right-click menu\n" +
            "Unticked, nothing is recorded or shown\n" +
            "主机为玩家或队伍领地中的每个容器（以及银行和快递箱）记录谁在何时放入或取出了什么（最近50条）。该领地的队伍成员和管理员可在容器右键菜单中查看；取消勾选时不记录也不显示");

        Server.AdminExtras = config.Bind(
            "Server",
            "AdminExtras",
            false,
            "Moderators (can only mute and kick), and for admins: freeze (the player cannot act until released) and jail (sent to the zone the admin chose and frozen there). Not the host, not other admins\n" +
            "Unticked, those orders are refused\n" +
            "版主（只能禁言和踢出），以及管理员的冻结（玩家在解除前无法行动）和关押（送到管理员指定的地图并冻结）。不适用于主机和其他管理员；取消勾选时这些命令被拒绝");

        Server.NightPasses = config.Bind(
            "Server",
            "NightPasses",
            NightRule.Half,
            "With OwnSleep, the night of the world passes when this share of the online, active players sleeps. Everyone / Half / One\n" +
            "开启各睡各的时，在线且未离开的玩家中有这么多人睡着，世界的夜晚就会过去。Everyone / Half / One");
        // a config from before this rule keeps its night: everyone (a new install takes Half)
        if (Regex.IsMatch(before, @"^\s*\[Server\]", RegexOptions.Multiline) &&
            !Regex.IsMatch(before, @"^\s*NightPasses\s*=", RegexOptions.Multiline)) {
            Server.NightPasses.Value = NightRule.Everyone;
        }

        Server.TimeStopsWhenEmpty = config.Bind(
            "Server",
            "TimeStopsWhenEmpty",
            true,
            "While no player is connected and active (the host counts unless it is away from the keyboard; a -empserver game never does), the date of the world stands still; so does the map of a player alone there and away from the keyboard. Otherwise time goes on as before\n" +
            "没有在线且未离开的玩家时（房主未离开也算；-empserver 服务器本身不算），世界的日期停止；独自在自己地图上的玩家离开时，那张地图的时间也停止；关闭时时间照常流逝");
        // a config from before this rule keeps its time going (a new install stops it)
        if (Regex.IsMatch(before, @"^\s*\[Server\]", RegexOptions.Multiline) &&
            !Regex.IsMatch(before, @"^\s*TimeStopsWhenEmpty\s*=", RegexOptions.Multiline)) {
            Server.TimeStopsWhenEmpty.Value = false;
        }

        // council 2026-10-26, stage 4: on since 0.26.1082 for every world (nothing punishes being away)
        Server.AbsentQuestsWait = config.Bind(
            "Server",
            "AbsentQuestsWait",
            true,
            "The quests of a player who left the game keep their time left: the days the world went on without it are added to their deadlines when it comes back. Otherwise they ran out as the world went on\n" +
            "离开游戏的玩家的任务保留剩余时间：玩家回来时，世界在其离开期间经过的天数会加到任务期限上；关闭时任务期限随世界流逝");

        // council 2026-10-26, stage 5: a week since 0.26.1082
        Server.CatchUpDays = config.Bind(
            "Server",
            "CatchUpDays",
            7,
            "A game far behind the world's date (a player's long trip, a map left alone) lives at most this many of the last days, a day per frame; the time before them is skipped over without its days. 1, 3 or 7\n" +
            "远远落后于世界日期的游戏（玩家长途旅行、无人的地图）最多经历最后这么多天，每帧一天；更早的时间直接跳过。1、3 或 7");

        // council 2026-10-26, stage 6: off until proven, then the default for new worlds only
        Server.LivingWorldMinutes = config.Bind(
            "Server",
            "LivingWorldMinutes",
            0,
            "Living world: the clock of the world follows real time, one game day per this many real minutes, whether the players act or stand still; it stops while nobody plays (TimeStopsWhenEmpty), a night slept as the night rule allows runs to the morning. 0: off, the date moves with the players' turns as before. 20, 40, 60 or 120\n" +
            "活的世界：世界的时钟跟随现实时间，每这么多现实分钟过一个游戏日，不论玩家行动还是静止；无人游玩时停止（TimeStopsWhenEmpty），按夜晚规则度过的夜晚会一直睡到早晨。0：关闭，日期像以前一样随玩家的回合推进。20、40、60 或 120");

        Server.SummonsFollowMaster = config.Bind(
            "Server",
            "SummonsFollowMaster",
            true,
            "What a player summoned goes where that player goes: along with the host, or away on its own\n" +
            "Otherwise, as in the game for the host alone, the summons of the others vanish when the host leaves the map\n" +
            "玩家召唤的生物跟随召唤者：与主机同行或独自离开时都会一起走；关闭时主机离开地图会让其他玩家的召唤物消失");

        Server.PlayersAtBase = config.Bind(
            "Server",
            "PlayersAtBase",
            true,
            "The base counts every player on its map: raids come while one of them is awake, the lucky month follows the god of any of them, and the days with a player count wherever a player is\n" +
            "Otherwise only the player whose game runs the base map counts (the host, mostly)\n" +
            "据点会计算地图上的所有玩家：只要有一人醒着就会有袭击，幸运月看任一玩家的神，与玩家共处的天数在任何玩家所在处都会计算；关闭时只计算运行据点地图的那位玩家");

        Server.Follow = config.Bind(
            "Server",
            "Follow",
            true,
            "The menu on another player's character offers Follow: your character walks after it, on the same map\n" +
            "Any key, a blow, a monster in sight or a window stops it; so does the other player leaving the map\n" +
            "其他玩家角色的菜单中提供“跟随”：你的角色会在同一地图上跟着对方走；按任意键、受到攻击、看到怪物或打开窗口都会停止，对方离开地图时也会停止");

        Server.PrivateChat = config.Bind(
            "Server",
            "PrivateChat",
            true,
            "Talk on another player's character opens a private chat with that player, wherever they are\n" +
            "Only that player gets the lines, the host passes them on without showing them\n" +
            "对其他玩家角色选择“交谈”会打开与该玩家的私聊，无论对方在哪里；只有该玩家会收到，主机只转发不显示");

        Server.ChatBubbles = config.Bind(
            "Server",
            "ChatBubbles",
            true,
            "What a player writes in the chat (the Chat tab or the game's chat box) also shows as a short bubble over its character, for the players on its map\n" +
            "Off: no bubble over a player for what it writes\n" +
            "玩家在聊天中写的内容（聊天标签页或游戏聊天框）也会以短气泡显示在其角色头上，同一地图的玩家可见；关闭时玩家写的内容不显示气泡");

        Server.ChatImages = config.Bind(
            "Server",
            "ChatImages",
            false,
            "Players can send small pictures (drawings) in the chat; each player can still hide them in My settings\n" +
            "玩家可以在聊天中发送小图片（绘画）；每个玩家仍可在“我的设置”中隐藏它们");

        Server.GuestMoongate = config.Bind(
            "Server",
            "GuestMoongate",
            true,
            "Another player's moongate takes that player alone to a map the host downloaded, the host stays where it is\n" +
            "Otherwise a moongate does nothing for the other players\n" +
            "其他玩家使用月之门时，只有该玩家前往主机已下载的地图，主机留在原地；关闭时月之门对其他玩家无效");

        Server.StrangerVisits = config.Bind(
            "Server",
            "StrangerVisits",
            false,
            "Strangers may visit a player's map: a player holding a map away from the host also takes in players of other teams (land rules are checked first, 10 visitors at most)\n" +
            "Off: only teammates, admins and players let in with \"allow\" share a map on a public server\n" +
            "陌生人可以拜访玩家的地图：在主机之外持有地图的玩家也接待其他队伍的玩家（先检查土地规则，最多10名访客）；关闭时在公开服务器上只有队友、管理员和被“允许”的玩家能共享地图");

        Server.GuestLandingPortal = config.Bind(
            "Server",
            "GuestLandingPortal",
            true,
            "Another player's landing portal takes that player alone to the gate zone of the survival world\n" +
            "Otherwise a landing portal does nothing for the other players\n" +
            "其他玩家使用着陆传送门时，只有该玩家前往生存模式的门所在区域；关闭时对其他玩家无效");

        Server.GuestSkyHelm = config.Bind(
            "Server",
            "GuestSkyHelm",
            true,
            "Another player's sky helm shows the host's destinations on that player's screen, its sky tickets pay, and the gate zone moves for everyone\n" +
            "Otherwise a sky helm does nothing for the other players\n" +
            "其他玩家使用天空舵时，在该玩家的屏幕上显示主机的目的地列表，用该玩家的天空船票支付，所有人的门所在区域一起改变；关闭时对其他玩家无效");
        Server.SharedMarriages = config.Bind(
            "Server",
            "SharedMarriages",
            true,
            "Every player can marry: a resident is the spouse of the character who married it, the first to propose wins, and the host keeps who in its save\n" +
            "Wedding, divorce and the lost ring deed work for that character only. Otherwise, as before, a guest's marriage stays in its own game\n" +
            "每位玩家都可以结婚：居民是与之结婚的角色的配偶，先求婚者优先，主机在存档中记录；婚礼、离婚和遗失戒指证书只对该角色有效。关闭时如以前一样，客人的婚姻只存在于其自己的游戏中");

        Server.AffinityPerPlayer = config.Bind(
            "Server",
            "AffinityPerPlayer",
            false,
            "Each player has its own affinity with each resident: gifts, talks and quests count for the player who made them, and the talk options follow the one who talks\n" +
            "Otherwise one affinity per resident for the whole group, as before\n" +
            "每位玩家与每位居民各自拥有好感度：礼物、交谈和任务只计入做出它们的玩家，对话选项取决于正在交谈的玩家；关闭时如以前一样，每位居民对整个队伍只有一个好感度");

        Server.Teams = config.Bind(
            "Server",
            "Teams",
            false,
            "Players can be in teams: the host keeps who is in which team in its save. A player in no team plays alone and shares nothing\n" +
            "Otherwise there are no teams and everything is as before\n" +
            "玩家可以组队：主机在存档中记录谁属于哪个队伍。不属于任何队伍的玩家是流浪者；关闭时没有队伍，一切如前");

        Server.TeamBases = config.Bind(
            "Server",
            "TeamBases",
            false,
            "A base belongs to the team of the player who claimed it: the players of another team cannot open its chests, pick up its things, build there or manage it\n" +
            "Otherwise every player uses every base as before\n" +
            "据点属于认领它的玩家所在的队伍：其他队伍的玩家不能打开其箱子、拾取物品、建造或管理据点；关闭时所有玩家如前使用所有据点");

        Server.TeamApproval = config.Bind(
            "Server",
            "TeamApproval",
            true,
            "A player who asks to join a team waits for its leader, who accepts or refuses from the list of players\n" +
            "Otherwise a player joins a team at once\n" +
            "申请加入队伍的玩家需等待队长在玩家列表中接受或拒绝；关闭时玩家立即加入队伍");

        Server.TeamPersonal = config.Bind(
            "Server",
            "TeamPersonal",
            false,
            "Inside a team, a chest, door or bed a player installs on the team's land is that player's: teammates open the chest and see inside but cannot take, cannot pass the door; \"Share with team\" in its menu gives it back to the team\n" +
            "Otherwise what is on a team's land is the whole team's\n" +
            "队伍内部，玩家在队伍土地上安装的箱子、门或床归该玩家所有：队友可以打开箱子查看但不能拿取，不能通过该门；在其菜单中选择“与队伍共享”即归还队伍；关闭时队伍土地上的东西属于整个队伍");

        Server.TeamDeed = config.Bind(
            "Server",
            "TeamDeed",
            true,
            "The founder of a new team receives one land deed, once per team and once per player\n" +
            "Otherwise a team buys its deed from the merchant as in the game\n" +
            "新队伍的创建者获得一张土地证书，每个队伍一次、每位玩家一次；关闭时队伍如游戏中一样从商人处购买证书");

        Server.TeamLeaders = config.Bind(
            "Server",
            "TeamLeaders",
            false,
            "The leader of a team manages it from the Team page: rename, put a member out, hand the team over, end it, invite players. Also limits a team's size and how often a player may found a team\n" +
            "Otherwise a team keeps what the player list offers: found, join, leave, and the leader's yes or no\n" +
            "队伍的队长可在队伍页面管理队伍：改名、移出成员、移交队长、解散、邀请玩家；同时限制队伍人数以及玩家创建队伍的频率；关闭时队伍只保留玩家列表中的创建、加入、离开以及队长的同意或拒绝");

        Server.LandRules = config.Bind(
            "Server",
            "LandRules",
            false,
            "The leader of a team sets, for each of its lands, what its own team and everyone else may do there: closed (not even enter), visit, visit and use, or build. Needs \"Team ownership of bases\"\n" +
            "Otherwise a land is as before: its team builds, others visit\n" +
            "队长可为队伍的每块土地设置本队与其他人的权限：封闭（不能进入）、参观、参观并使用、建造；需要开启“据点归队伍所有”；关闭时土地如前：本队可建造，其他人只能参观");

        Server.CommonsStartLand = config.Bind(
            "Server",
            "CommonsStartLand",
            false,
            "The start land (the home meadow) and Vernis belong to everyone, like a town: nobody owns them, no team can take them, they have no tax bill, and every player builds, digs, places and picks up furniture there whatever the other building rules say. " +
            "The meadow stays the world's base (the host's home). In Vernis its people, shops and quests go on, and nobody may build within 5 cells of the way down to the mine\n" +
            "Otherwise the start land is the host's base and Vernis a town, as before\n" +
            "起始土地（家园草原）和维尔尼斯像城镇一样属于所有人：没有主人，任何队伍都不能占有，没有税单，每名玩家都可以在那里建造、挖掘、摆放和拿起家具，不受其他建造规则限制。草原仍是世界的据点（房主的家）。维尔尼斯的居民、商店和任务照常，通往矿坑的入口周围5格内任何人都不能建造。关闭时起始土地如前是房主的据点，维尔尼斯仍是城镇");

        Server.SiblingLands = config.Bind(
            "Server",
            "SiblingLands",
            false,
            "Several lands may stand on one world-map tile, each its own team's: a deed read on a tile that already holds a land makes another plot there, and walking onto such a tile lists the lands to enter. Needs \"Team ownership of bases\"\n" +
            "Otherwise one tile holds one land, as before\n" +
            "同一个世界地图格子上可以有多块土地，各属于不同队伍：在已有土地的格子上使用土地证书会在同一格再开辟一块地，走进这样的格子时会列出可进入的土地；需要开启“据点归队伍所有”；关闭时一个格子只有一块土地");

        Server.LargeCrowds = config.Bind(
            "Server",
            "LargeCrowds",
            false,
            "Large crowds: each player is sent at full rate only what is near its character (24 tiles), its companions, its team and whoever it trades, duels or fights with; the rest of the map once a second, and the whole map checked every 4 seconds. Nothing disappears, far characters move in steps\n" +
            "Unticked, every player gets the whole map at full rate, as before\n" +
            "大量玩家：只以全速向每名玩家发送其角色附近（24格）的内容、其同伴、队伍以及与其交易、决斗或战斗的对象；地图其余部分每秒一次，整张地图每4秒核对一次；不会有东西消失，远处角色的移动会一跳一跳；关闭时每名玩家以全速接收整张地图，如前");

        Server.LandAbandon = config.Bind(
            "Server",
            "LandAbandon",
            false,
            "A team none of whose members came into the game for the days set below loses its claim on its lands: they are nobody's, open to everyone, until claimed again. Needs \"Team ownership of bases\"\n" +
            "Otherwise a team keeps its lands however long it is away\n" +
            "队伍的所有成员在下方设定的天数内都没有进入游戏时，该队伍失去对其土地的认领：土地变为无主、对所有人开放，直到再次被认领；需要开启“据点归队伍所有”；关闭时队伍无论离开多久都保留其土地");

        Server.AbandonDays = config.Bind(
            "Server",
            "AbandonDays",
            30,
            "With \"Abandoned land\": real days without any member of a team in the game before its lands are lost\n" +
            "启用“废弃土地”时：队伍的成员连续多少个现实天数没有进入游戏后失去土地");

        Server.LandsPerTeam = config.Bind(
            "Server",
            "LandsPerTeam",
            0,
            "The most lands one team may claim: past it, a deed is not read. 0: no limit\n" +
            "每个队伍最多可认领多少块土地：达到上限后不能再使用土地证书。0：不限");

        Server.TeamMaxSize = config.Bind(
            "Server",
            "TeamMaxSize",
            10,
            "With \"Team leaders manage their team\": the most players a team holds. 0: no limit\n" +
            "启用“队长管理队伍”时：一个队伍最多容纳多少玩家。0：不限");

        Server.TeamFoundCooldown = config.Bind(
            "Server",
            "TeamFoundCooldown",
            5,
            "With \"Team leaders manage their team\": minutes a player waits before founding another team. 0: no wait\n" +
            "启用“队长管理队伍”时：玩家再次创建队伍前需等待的分钟数。0：无需等待");

        Server.SpawnZone = config.Bind(
            "Server",
            "SpawnZone",
            "start",
            "Where a new player appears, and a player whose team has no base, when teams are on: start (the meadow where a new game starts), host (the host's map, as before), or the id of a zone\n" +
            "新玩家以及其队伍没有据点的玩家（启用队伍时）出现的地方：start（新游戏开始的草原）、host（主机所在地图，如前）或区域 id");

        Server.TeamPvp = config.Bind(
            "Server",
            "TeamPvp",
            false,
            "Two players of different teams are enemies to each other, outside towns and the spawn zone. Players in no team fight nobody\n" +
            "A player killed by another dies only if \"Players can kill each other\" is on. Otherwise players of different teams cannot fight\n" +
            "不同队伍的玩家在城镇和出生区域之外互为敌人，流浪者不参与；只有开启玩家可以互相击杀时才会真正死亡。关闭时不同队伍的玩家不能战斗");

        Server.TeamKnowledge = config.Bind(
            "Server",
            "TeamKnowledge",
            false,
            "Each player has its own recipes, crafted list and codex: teammates share one, a player without a team has its own. The save keeps everything anyone knows. Always on for the Elin Together public server\n" +
            "Otherwise everyone shares one book of recipes and one codex, as before\n" +
            "每个玩家有自己的配方、制作记录和图鉴：队友共享一套，没有队伍的玩家有自己的一套。存档保留所有人知道的一切。Elin Together 公共服务器上始终开启；关闭时所有人如前共用一套配方和图鉴");

        Server.TeamBoxes = config.Bind(
            "Server",
            "TeamBoxes",
            false,
            "Each team (a player without a team: alone) keeps its own bank and its own delivery box. What the old boxes hold stays shared. Needs Teams. Always on for the Elin Together public server\n" +
            "Unticked, every team's box is merged back into the shared one\n" +
            "每个队伍（没有队伍的玩家单独算）拥有自己的银行和快递箱，旧箱子里的东西仍然共享。需要开启队伍；Elin Together 公共服务器上始终开启。取消勾选时，各队的箱子并回共享箱子");

        Server.TeamBills = config.Bind(
            "Server",
            "TeamBills",
            false,
            "Each team gets its own monthly tax bill, for its own lands (lands of a player in no team: that player's own bill), on its members' highest fame. Unpaid bills are counted per team: four or more cost karma to that team's players only\n" +
            "Otherwise one bill for the whole world, as before\n" +
            "每个队伍为自己的土地单独收到每月税单（无队伍的土地：流浪者的税单），按其成员的最高名声计算。未付税单按队伍计数：四张及以上只扣该队伍玩家的善恶值；关闭时整个世界如前只有一张税单");

        Server.TeamStory = config.Bind(
            "Server",
            "TeamStory",
            false,
            "Each team keeps its own story journal: the furthest chapter of the main quest one of its players took part in (the player who made the step and the players on that map), shown on the Teams card. The story itself stays the world's\n" +
            "Otherwise no journal per team\n" +
            "每个队伍保留自己的剧情日志：其玩家参与过的主线最远章节（推进剧情的玩家及同一地图上的玩家），显示在队伍卡片上；剧情本身仍属于整个世界。关闭时没有队伍日志");

        Server.TeamSheets = config.Bind(
            "Server",
            "TeamSheets",
            false,
            "Players can open a read-only character sheet of their teammates, sent by the host (levels, skills, equipment, karma, fame)\nOtherwise no sheets\n玩家可以查看队友的只读角色面板，由房主发送（等级、技能、装备、善恶值、名声）。关闭时没有角色面板");

        Server.ActivityJournal = config.Bind(
            "Server",
            "ActivityJournal",
            false,
            "The host keeps a journal of the world's events (teams founded, duels won, boss kills, deaths, records), shown on the Journal page\nOtherwise no journal\n房主记录世界大事日志（队伍成立、决斗获胜、击败首领、死亡、纪录），显示在日志页面。关闭时没有日志");

        Server.TeamBanner = config.Bind(
            "Server",
            "TeamBanner",
            false,
            "Each team gets a banner (an emblem and a colour) chosen by its leader and shown on the Teams page\nOtherwise no banners\n每个队伍有一面由队长选择的旗帜（徽记和颜色），显示在队伍页面。关闭时没有旗帜");

        Server.Leaderboards = config.Bind(
            "Server",
            "Leaderboards",
            false,
            "The host ranks players and teams (level, deepest floor, duels won, team wealth), shown on the Leaderboards page\nOtherwise no rankings\n房主为玩家和队伍排名（等级、最深楼层、决斗获胜、队伍财富），显示在排行榜页面。关闭时没有排名");

        Server.Market = config.Bind(
            "Server",
            "Market",
            false,
            "Players post classified ads (selling or looking for an item, a quantity, a price, a short note) on the Market page, kept by the host with the world: 5 ads per player, 7 days each\nOtherwise no market\n玩家在市场页面发布分类广告（出售或求购物品、数量、价格、简短备注），由房主随世界保存：每名玩家 5 条，每条 7 天。关闭时没有市场");

        Server.Mailbox = config.Bind(
            "Server",
            "Mailbox",
            false,
            "Players send each other letters (a text, up to 5 stacks of items and gold), also to a player who is offline. The host keeps the letters and what they hold in the world save; a letter left unclaimed for 30 days goes back to its sender\nOtherwise no letters\n玩家可以互相寄信（文字、最多5组物品和金币），也可以寄给不在线的玩家。房主把信件及其中的物品保存在世界存档中；30天无人领取的信件会退回寄件人。关闭时没有信件");

        Server.TeamQuestSharing = config.Bind(
            "Server",
            "TeamQuestSharing",
            false,
            "A player in a team can share its own quests with its team (a box on the Team page): its teammates see them in their journal, any of them can advance and turn them in, and every teammate in the game gets the reward. Needs teams and personal quests\nOtherwise each player's quests stay its own\n队伍中的玩家可以把自己的任务分享给队伍（队伍页面上的勾选框）：队友在日志中看到这些任务，任何队友都可以推进并交付，所有在线队友都能获得奖励。需要开启队伍和个人任务。关闭时每名玩家的任务只属于自己");

        Server.ShareLogForReports = config.Bind(
            "Server",
            "ShareLogForReports",
            false,
            "When a guest sends a bug report, the host's game adds the end of its own logs (the last 10 minutes of the mod's log and the end of Player.log, 300 KB at most, once a minute per guest). " +
            "Steam ids, IP addresses and Windows user names are removed first: the report becomes a public GitHub issue\n" +
            "Otherwise a guest's report holds only its own logs\n" +
            "客机发送问题报告时，房主的游戏会附上自己的日志末尾（模组日志最近 10 分钟和 Player.log 末尾，最多 300 KB，每名客机每分钟一次）。先删除 Steam ID、IP 地址和 Windows 用户名：报告会成为公开的 GitHub 问题。关闭时客机的报告只包含自己的日志");

        Server.TeamOwnStory = config.Bind(
            "Server",
            "TeamOwnStory",
            false,
            "Experimental. Each player lives the story at its own pace (Ash's quest, the exploration of Nymelle, every dialog choice, the key items, Loytel's debt and the extra tax), teammates together: a team shares one story, a player without a team has its own. Everyone starts at the story point the world is at when the box is ticked. Farris joins the first base only; a player or team reaching a floor someone already cleared takes the story on from there. Always on for the Elin Together public server\n" +
            "The save keeps the host's own story (as it was when the box was ticked): unticked, or the save opened without the mod, the world goes on with it. Otherwise all players share one story progression, as before\n" +
            "实验性。每个玩家按自己的节奏经历剧情（阿什的任务、尼梅尔的探索、所有对话选择、关键物品、欠洛伊特的债务和额外税金），队友一起：一个队伍共享一条剧情，没有队伍的玩家有自己的剧情。勾选时所有人从世界当前的剧情进度开始。法里斯只加入第一个据点；到达已被他人通过的楼层的玩家或队伍从那里继续。Elin Together 公共服务器上始终开启。存档保留房主自己的剧情：取消勾选或不带模组打开存档时，世界沿用它。关闭时所有玩家如前共享一条剧情进度");

        Server.TeamOwnGuilds = config.Bind(
            "Server",
            "TeamOwnGuilds",
            false,
            "With 'Each player or team lives its own story': each player or team also has its own place in the Fighters', Mages', Thieves' and Merchants' guilds (membership, rank, contribution, guild quests). Everyone starts with the guilds the world has when the box is ticked. Off even on the Elin Together public server\n" +
            "Unticked, or the save opened without the mod: the guilds of the host's own story. Otherwise one membership for the group, as before\n" +
            "配合“每个玩家或队伍经历自己的剧情”：每个玩家或队伍在战士、法师、盗贼和商人公会中也有自己的位置（成员身份、等级、贡献、公会任务）。勾选时所有人从世界当前的公会状态开始。Elin Together 公共服务器上也默认关闭。取消勾选或不带模组打开存档时：房主自己剧情的公会。关闭时整个团体共用一个成员身份，如前");

        Server.GlobalChat = config.Bind(
            "Server",
            "GlobalChat",
            true,
            "What a player types in the chat is read by every player, wherever they are: on another map, on the world map, away on their own\n" +
            "Otherwise only the players on the same map as the speaker read it. Lines of townspeople and the game stay on their map either way\n" +
            "玩家输入的聊天内容所有玩家都能看到，无论身在何处（其他地图、世界地图、独自行动）；关闭时只有同一地图上的玩家能看到。居民和游戏的台词无论如何都只在本地图显示");

        Server.WorldMapTime = config.Bind(
            "Server",
            "WorldMapTime",
            WorldMapTime.SharedClock,
            "Who pays the hours of a trip on the world map. SharedClock: the world map is a map like any other, every step of anyone moves the date; the others catch up without losing hunger or quest time\n" +
            "TravelTogether: a step moves the date only when all the players are on the world map, whoever steps; otherwise the traveller pays its own turns and the date stays\n" +
            "PersonalPace: as SharedClock, but a player travelling while the others are elsewhere runs ahead of the world (its hunger and quests pay the hours, the date stays) up to WorldMapLeadCap hours; the world catches up once everyone is on the world map or sleeps together\n" +
            "世界地图上旅行的时间由谁承担。SharedClock：世界地图与其他地图相同，任何人的每一步都会推进日期，其他人追上时间但不会损失饱食度或任务时间；TravelTogether：只有所有玩家都在世界地图上时，一步才会推进日期；PersonalPace：独自旅行的玩家领先于世界（饱食度与自己的任务承担时间，日期不动），所有人在世界地图上相聚或一起睡觉时世界追上");
        if (OldWorldMapTime(before) is { } old) {
            Server.WorldMapTime.Value = old;
        }

        Server.WorldMapLeadCap = config.Bind(
            "Server",
            "WorldMapLeadCap",
            24,
            "Personal Pace: how many hours a player travelling alone may run ahead of the world; the hours past it move the date as in SharedClock\n" +
            "个人步调：独自旅行的玩家最多可以领先世界多少小时，超出的部分像共享时钟一样推进日期");

        Server.DumpSparesBelt = config.Bind(
            "Server",
            "DumpSparesBelt",
            true,
            "Auto-dump leaves what the player holds and what is in the tool belt, as it leaves the hotbar\n" +
            "Otherwise as in the game\n" +
            "自动收纳不会收走玩家手持的物品和工具腰带里的物品，就像它不会收走快捷栏一样。关闭时与原版相同");

        Server.ImportCharacter = config.Bind(
            "Server",
            "ImportCharacter",
            false,
            "A player joining can bring the character of one of its own saves: the character, its equipment, bag and gold, its fame and karma\n" +
            "Not its companions, base, quests or bank. Its save is only read, never changed\n" +
            "加入的玩家可以带来自己存档中的角色：角色本身、装备、背包、金币、声望和业力；不包括同伴、据点、任务和银行。存档只被读取，不会被修改");

        Server.ExportCharacter = config.Bind(
            "Server",
            "ExportCharacter",
            false,
            "In the list of its characters, a joining player can take one of them to one of its own solo saves, where it replaces the save's character (a copy of the save is made first)\n" +
            "The character leaves this world until it comes back through \"bring a character from a save\", or is brought back unchanged. Only with ImportCharacter on\n" +
            "加入的玩家可以在角色列表中把自己的某个角色带到自己的单人存档中，替换该存档的角色（会先备份存档）。角色离开这个世界，直到通过导入角色带回或原样召回。仅在允许导入角色时有效");

        Server.DeleteCharacter = config.Bind(
            "Server",
            "DeleteCharacter",
            true,
            "In the list of its characters, a joining player can delete one of them, with what it carries (asked twice)\n" +
            "Its companions stay in the world and no longer follow anyone\n" +
            "加入的玩家可以在角色列表中删除自己的某个角色及其携带的物品（会确认两次），其同伴留在世界中");

        // off by default, the one rule that is: the checks are new and logged first ("would have refused"), a
        // refusal of a legit act would cost a player what it did (council 2, audit 2026-10-23). A public game
        // (PublicGame) refuses whatever this says, see ElinDelta.TrustEnforced
        Server.EnforceOwnership = config.Bind(
            "Server",
            "EnforceOwnership",
            false,
            "What a guest's game says about a character that is neither its own nor one of its companions (its task, its damage, its skills, its party), " +
            "about a thing held by another player or its companions, and a flood of data (over 500 of one kind a second) are refused. Always on in a public game\n" +
            "Off: only written to the log. A trade, a gift to another player or an order to one's own companion is never refused\n" +
            "客机游戏发来的、关于既非其角色也非其同伴的角色的数据（任务、伤害、技能、队伍）、关于其他玩家或其同伴所持物品的数据，以及大量数据洪流会被拒绝；公开游戏中始终开启。关闭时仅写入日志。交易、送给其他玩家的礼物或对自己同伴的命令永远不会被拒绝");
        Server.ActivitiesFromActor = config.Bind(
            "Server",
            "ActivitiesFromActor",
            true,
            "What a barrel brews and what a baby pet learns from its mother's milk follow the player it is about: the one nearest to the barrel, the pet's owner\n" +
            "Otherwise they follow the host's character, as for a player alone\n" +
            "酒桶酿出的酒和幼宠喝母乳的成长取决于相关的玩家（离酒桶最近的玩家、宠物的主人）；关闭时取决于主机的角色");

        Server.TravelCheckpointSeconds = config.Bind(
            "Server",
            "TravelCheckpointSeconds",
            60,
            new ConfigDescription(
                "Seconds between two checkpoints of a client travelling alone (its zone and character)\n" +
                "If it disconnects, only what happened since the last checkpoint is lost. 0 disables\n" +
                "独自行动的客机每隔多少秒回传一次进度，断线时只丢失最后一次之后的内容。0 为关闭",
                new AcceptableValueRange<int>(0, 600)));

        Server.ServerRestsWhenEmpty = config.Bind(
            "Server",
            "ServerRestsWhenEmpty",
            false,
            "A dedicated server (-empserver) with nobody connected runs at 5 frames a second and with its sound stopped, the full rate again as soon as a player connects. Otherwise it runs at the game's own frame rate all the time\n" +
            "无人连接时，专用服务器（-empserver）以每秒 5 帧运行并停止声音，有玩家连接时立即恢复正常帧率；关闭时始终以游戏自身的帧率运行");

        Server.FastJoinsAndSaves = config.Bind(
            "Server",
            "FastJoinsAndSaves",
            false,
            "For big worlds: a player joining and the periodic save freeze the game less. The world and the map are still copied in the game; their compression and sending, and the writing of the save files, are done beside it\n" +
            "适用于大型世界：玩家加入和定期保存时游戏卡顿更少。世界和地图仍在游戏中复制，其压缩、发送以及存档文件的写入在旁边完成");

        Server.ServerAvatar = config.Bind(
            "Server",
            "ServerAvatar",
            true,
            "A dedicated server (-empserver) plays no character: the save owner's character becomes an ordinary player character tied to its Steam id (it joins like any guest), and the server's own player is a hidden, untouchable stand-in. Off: the owner's character stays planted at the base as before. Has no effect on a game hosted from the menu\n" +
            "专用服务器（-empserver）不扮演任何角色：存档所有者的角色变为与其 Steam ID 绑定的普通玩家角色（像其他访客一样加入），服务器自身的玩家是一个隐藏且无敌的替身；关闭：所有者的角色像以前一样留在基地。对从菜单开启的游戏无效");

        Server.HiddenServer = config.Bind(
            "Server",
            "HiddenServer",
            false,
            "Beta, Windows only: \"Start Server\" starts your world in a second, hidden Elin on this machine (as a Minecraft server is started), and your own game joins it like any other player. You then live exactly what your friends live: no host character. Needs about 2 GB of free memory; if the hidden game is not up in 2 minutes, the world is hosted as before\n" +
            "测试版，仅 Windows：“启动服务器”会在本机另开一个隐藏的 Elin 来运行你的世界（像启动 Minecraft 服务器一样），你自己的游戏像其他玩家一样加入，不再有主机角色，体验与好友完全相同。需要约 2 GB 空闲内存；隐藏游戏 2 分钟内未启动时，像以前一样直接开放世界");

        Dev.Listener = config.Bind(
            "Dev",
            "Listener",
            false,
            "Open the localhost debug listener for scripts/mcp (Debug builds only)\n" +
            "It executes arbitrary C#, never enable on a machine you share\n" +
            "为 scripts/mcp 打开本机调试监听（仅 Debug 构建）\n" +
            "它会执行任意 C#，不要在共用机器上开启");

        Dev.Identity = config.Bind(
            "Dev",
            "Identity",
            0,
            new ConfigDescription(
                "Local udp test identity (Debug builds only), 0 uses the Steam account\n" +
                "Lets several game instances on one Steam account join a local session as different players\n" +
                "本地UDP测试身份（仅 Debug 构建），0 为使用 Steam 账号",
                new AcceptableValueRange<int>(0, 999)));

        // (no page shows it: a stand-in until the store says which world is peer-hosted, PLAN_peer_hosted_server P4)
        Dev.PeerHosted = config.Bind(
            "Dev",
            "PeerHosted",
            false,
            "Test of the world kept by its players: hosting always goes through the hidden server, whoever keeps the world is a plain guest of it, " +
            "admins, bans and rules come from ElinMP/peer_hosting.json (the store replaces it), and a keeper whose rules differ is left. " +
            "Same as the launch argument -emppeerhosted. Leave off\n" +
            "玩家共同维护的世界测试：开放世界总是通过隐藏服务器，维护世界的人只是普通访客；管理员、封禁和规则来自 ElinMP/peer_hosting.json；访客会离开规则不同的维护者。与启动参数 -emppeerhosted 相同。请保持关闭");

        Dev.BotLaunchers = config.Bind(
            "Dev",
            "BotLaunchers",
            "",
            "Game copies a bot player can be started from (Debug builds only), separated by ';'\n" +
            "Empty looks for Elin*/Elin.exe, made by dev/_tools/make_lab.py, in the folder the environment variable\n" +
            "ELINTOGETHER_LAB names, else in Documents/ElinMods/_lab");

        Dev.BotAllActions = config.Bind(
            "Dev",
            "BotAllActions",
            false,
            "Bot players also accept quests and sell through the shipping chest (Debug builds only)\n" +
            "Leave off on a world you care about");

        Reload();
    }

    internal static class Dev
    {
        internal static ConfigEntry<bool> Listener { get; set; } = null!;
        internal static ConfigEntry<int> Identity { get; set; } = null!;
        internal static ConfigEntry<bool> PeerHosted { get; set; } = null!;
        internal static ConfigEntry<string> BotLaunchers { get; set; } = null!;
        internal static ConfigEntry<bool> BotAllActions { get; set; } = null!;
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
        internal static ConfigEntry<bool> ChatGameTime { get; set; } = null!;
        internal static ConfigEntry<bool> HideChatImages { get; set; } = null!;
        internal static ConfigEntry<string> FirstStepsSeen { get; set; } = null!;
        internal static ConfigEntry<string> DepotPath { get; set; } = null!;
        internal static ConfigEntry<string> DepotPassword { get; set; } = null!;
        internal static ConfigEntry<string> ServerAddress { get; set; } = null!;
        internal static ConfigEntry<bool> FetchMods { get; set; } = null!;
        internal static ConfigEntry<bool> RecordReplay { get; set; } = null!;
        internal static ConfigEntry<bool> KeepMods { get; set; } = null!;
        internal static ConfigEntry<bool> FetchCodeMods { get; set; } = null!;
        internal static ConfigEntry<string> TrustedHosts { get; set; } = null!;
        internal static ConfigEntry<string> PatchNotesSeen { get; set; } = null!;
        internal static ConfigEntry<bool> CheckUpdates { get; set; } = null!;
        internal static ConfigEntry<string> UpdateLatest { get; set; } = null!;
        internal static ConfigEntry<string> UpdateAnnounced { get; set; } = null!;
    }

    internal static class Server
    {
        internal static ConfigEntry<string> SourceValidationSet { get; set; } = null!;
        internal static ConfigEntry<bool> StrictValidationMode { get; set; } = null!;
        internal static ConfigEntry<bool> SharedAverageSpeed { get; set; } = null!;
        internal static ConfigEntry<bool> TurnBasedCombat { get; set; } = null!;
        internal static ConfigEntry<bool> IndependentTravel { get; set; } = null!;
        internal static ConfigEntry<bool> SameGameVersion { get; set; } = null!;
        internal static ConfigEntry<bool> PublishMods { get; set; } = null!;
        internal static ConfigEntry<bool> PublicGame { get; set; } = null!;
        internal static ConfigEntry<bool> LookModsOwn { get; set; } = null!;
        internal static ConfigEntry<string> ClientOnlyMods { get; set; } = null!;
        internal static ConfigEntry<bool> PlayerShipping { get; set; } = null!;
        internal static ConfigEntry<bool> GuestBuild { get; set; } = null!;
        internal static ConfigEntry<bool> PlayerKill { get; set; } = null!;
        internal static ConfigEntry<bool> HostManagesBase { get; set; } = null!;
        internal static ConfigEntry<bool> Duels { get; set; } = null!;
        internal static ConfigEntry<bool> GuestBouts { get; set; } = null!;
        internal static ConfigEntry<bool> DuelArena { get; set; } = null!;
        internal static ConfigEntry<bool> DuelBets { get; set; } = null!;
        internal static ConfigEntry<bool> PersonalQuests { get; set; } = null!;
        internal static ConfigEntry<bool> ChooseCharacter { get; set; } = null!;
        internal static ConfigEntry<bool> AutoReconnect { get; set; } = null!;
        internal static ConfigEntry<bool> AutoResync { get; set; } = null!;
        internal static ConfigEntry<bool> AutoSave { get; set; } = null!;
        internal static ConfigEntry<bool> WorldCopy { get; set; } = null!;
        internal static ConfigEntry<bool> Takeover { get; set; } = null!;
        internal static ConfigEntry<bool> SharedTax { get; set; } = null!;
        internal static ConfigEntry<bool> OncePerPlayer { get; set; } = null!;
        internal static ConfigEntry<bool> TownLife { get; set; } = null!;
        internal static ConfigEntry<bool> TownBoards { get; set; } = null!;
        internal static ConfigEntry<bool> ShopsFromEveryPlayer { get; set; } = null!;
        internal static ConfigEntry<bool> NearestSpawns { get; set; } = null!;
        internal static ConfigEntry<bool> AutoCombatPerPlayer { get; set; } = null!;
        internal static ConfigEntry<bool> SoftRecall { get; set; } = null!;
        internal static ConfigEntry<bool> AutoHost { get; set; } = null!;
        internal static ConfigEntry<bool> OwnSleep { get; set; } = null!;
        internal static ConfigEntry<int> IdleMinutes { get; set; } = null!;
        internal static ConfigEntry<int> KickKeepOutMinutes { get; set; } = null!;
        internal static ConfigEntry<int> MaxCharacters { get; set; } = null!;
        internal static ConfigEntry<bool> MembersOnly { get; set; } = null!;
        internal static ConfigEntry<bool> ContainerAccessLog { get; set; } = null!;
        internal static ConfigEntry<bool> AdminExtras { get; set; } = null!;
        internal static ConfigEntry<NightRule> NightPasses { get; set; } = null!;
        internal static ConfigEntry<bool> TimeStopsWhenEmpty { get; set; } = null!;
        internal static ConfigEntry<bool> AbsentQuestsWait { get; set; } = null!;
        internal static ConfigEntry<int> CatchUpDays { get; set; } = null!;
        internal static ConfigEntry<int> LivingWorldMinutes { get; set; } = null!;
        internal static ConfigEntry<bool> SummonsFollowMaster { get; set; } = null!;
        internal static ConfigEntry<bool> PlayersAtBase { get; set; } = null!;
        internal static ConfigEntry<bool> GlobalChat { get; set; } = null!;

        internal static ConfigEntry<bool> Follow { get; set; } = null!;
        internal static ConfigEntry<bool> PrivateChat { get; set; } = null!;
        internal static ConfigEntry<bool> ChatBubbles { get; set; } = null!;
        internal static ConfigEntry<bool> ChatImages { get; set; } = null!;
        internal static ConfigEntry<bool> GuestMoongate { get; set; } = null!;

        internal static ConfigEntry<bool> StrangerVisits { get; set; } = null!;
        internal static ConfigEntry<bool> GuestLandingPortal { get; set; } = null!;
        internal static ConfigEntry<bool> GuestSkyHelm { get; set; } = null!;
        internal static ConfigEntry<bool> SharedMarriages { get; set; } = null!;
        internal static ConfigEntry<bool> AffinityPerPlayer { get; set; } = null!;
        internal static ConfigEntry<bool> Teams { get; set; } = null!;
        internal static ConfigEntry<bool> TeamBases { get; set; } = null!;
        internal static ConfigEntry<bool> TeamApproval { get; set; } = null!;
        internal static ConfigEntry<bool> TeamDeed { get; set; } = null!;
        internal static ConfigEntry<bool> TeamLeaders { get; set; } = null!;
        internal static ConfigEntry<bool> LandRules { get; set; } = null!;
        internal static ConfigEntry<bool> CommonsStartLand { get; set; } = null!;
        internal static ConfigEntry<bool> SiblingLands { get; set; } = null!;
        internal static ConfigEntry<bool> LargeCrowds { get; set; } = null!;
        internal static ConfigEntry<bool> LandAbandon { get; set; } = null!;
        internal static ConfigEntry<int> AbandonDays { get; set; } = null!;
        internal static ConfigEntry<int> LandsPerTeam { get; set; } = null!;
        internal static ConfigEntry<int> TeamMaxSize { get; set; } = null!;
        internal static ConfigEntry<int> TeamFoundCooldown { get; set; } = null!;
        internal static ConfigEntry<bool> TeamPersonal { get; set; } = null!;
        internal static ConfigEntry<string> SpawnZone { get; set; } = null!;

        internal static ConfigEntry<bool> TeamPvp { get; set; } = null!;
        internal static ConfigEntry<bool> TeamKnowledge { get; set; } = null!;
        internal static ConfigEntry<bool> TeamBoxes { get; set; } = null!;
        internal static ConfigEntry<bool> TeamBills { get; set; } = null!;
        internal static ConfigEntry<bool> TeamStory { get; set; } = null!;
        internal static ConfigEntry<bool> TeamOwnStory { get; set; } = null!;
        internal static ConfigEntry<bool> TeamSheets { get; set; } = null!;
        internal static ConfigEntry<bool> ActivityJournal { get; set; } = null!;
        internal static ConfigEntry<bool> TeamBanner { get; set; } = null!;
        internal static ConfigEntry<bool> Leaderboards { get; set; } = null!;
        internal static ConfigEntry<bool> Market { get; set; } = null!;
        internal static ConfigEntry<bool> Mailbox { get; set; } = null!;
        internal static ConfigEntry<bool> TeamQuestSharing { get; set; } = null!;
        internal static ConfigEntry<bool> ShareLogForReports { get; set; } = null!;
        internal static ConfigEntry<bool> TeamOwnGuilds { get; set; } = null!;
        internal static ConfigEntry<WorldMapTime> WorldMapTime { get; set; } = null!;
        internal static ConfigEntry<int> WorldMapLeadCap { get; set; } = null!;
        internal static ConfigEntry<bool> DumpSparesBelt { get; set; } = null!;
        internal static ConfigEntry<bool> ImportCharacter { get; set; } = null!;
        internal static ConfigEntry<bool> ExportCharacter { get; set; } = null!;

        internal static ConfigEntry<bool> DeleteCharacter { get; set; } = null!;
        internal static ConfigEntry<bool> EnforceOwnership { get; set; } = null!;
        internal static ConfigEntry<bool> PlayerTrade { get; set; } = null!;
        internal static ConfigEntry<bool> OwnJournal { get; set; } = null!;
        internal static ConfigEntry<bool> PermadeathRevive { get; set; } = null!;
        internal static ConfigEntry<bool> PlayerCombatTime { get; set; } = null!;
        internal static ConfigEntry<bool> PlayerClock { get; set; } = null!;
        internal static ConfigEntry<bool> PlayerStepPace { get; set; } = null!;
        internal static ConfigEntry<bool> SharedWorldTime { get; set; } = null!;
        internal static ConfigEntry<bool> WorldKeeper { get; set; } = null!;
        internal static ConfigEntry<bool> ActivitiesFromActor { get; set; } = null!;
        internal static ConfigEntry<int> TravelCheckpointSeconds { get; set; } = null!;
        internal static ConfigEntry<bool> ServerRestsWhenEmpty { get; set; } = null!;
        internal static ConfigEntry<bool> FastJoinsAndSaves { get; set; } = null!;
        internal static ConfigEntry<bool> ServerAvatar { get; set; } = null!;
        internal static ConfigEntry<bool> HiddenServer { get; set; } = null!;
    }
}