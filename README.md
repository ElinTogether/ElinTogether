# Elin Together — "independence" fork

**Current version: 1.0** (Steam Workshop, or the zip `ElinTogether-independence.zip`).

English | [Français](README_fr.md) | [中文](README_zh.md) | [日本語](README_ja.md)

[![Latest release](https://img.shields.io/github/v/release/jeanfreger/elin-together-independence?include_prereleases&label=latest%20release)](https://github.com/jeanfreger/elin-together-independence/releases)

> **No official server or Discord yet.** Elin Together has no official public server and no official Discord for now. Nobody from the project will ever ask you for money, a password or your Steam login: anyone who does in the mod's name is not us. The mod and its updates are only on this page and on the Steam Workshop.

A fork of [Elin Together](https://github.com/ElinTogether/ElinTogether), the multiplayer mod for
[Elin](https://store.steampowered.com/app/2135150/Elin/), with one goal: **in game, no difference between the host
and the other players**. Everyone goes where they want, with their own companions, quests, fame and money, while
the world (story, home base, guilds) stays shared. The world itself no longer has to live on one player's PC.

The original mod keeps the whole party on the host's map and treats the other players as teammates of the host.
Its authors consider separate maps out of scope; this fork is where that is tried. All credit for the mod itself
goes to them (see [Credits](#credits)).

> **Status: 1.0, with limits.** Built for Elin's Nightly branch. Almost everything is checked by automated in-game
> tests on one PC (two to five game windows, and up to 50 simulated players for the load), and the core was played for
> real by one small group. Several parts of 1.0 have **never been played between two real PCs**: the release notes
> mark them, and so does [What is left](#what-is-left-before-a-real-release). Back up your saves first:
> `%USERPROFILE%\AppData\LocalLow\Lafrontier\Elin`.

**Contents**

1. [What it changes](#what-it-changes)
2. [Install](#install)
3. [Your first game together](#your-first-game-together)
4. [Keeping the world online: the depot](#keeping-the-world-online-the-depot)
5. [Games with mods](#games-with-mods)
6. [Settings](#settings)
7. [How it works](#how-it-works)
8. [Known limits](#known-limits)
9. [What is left before a real release](#what-is-left-before-a-real-release)
10. [How it is tested](#how-it-is-tested)
11. [If something goes wrong](#if-something-goes-wrong)
12. [Build](#build)
13. [Credits](#credits)

## What it changes

### Everyone goes where they want

- **Independent travel.** A player leaves for another map without the host. The map and what was done there are
  kept, progress is saved every minute while away, and chat works across maps.
- **Shared maps.** A player can join another player on their map, host or not. If the one holding the map leaves,
  another takes over.
- **Nobody is dragged along.** When the host changes map, the others stay where they are. A player enters a map by
  its entrance; nobody is teleported onto the host.
- **Each at their own pace.** A monster acts when the player it fights acts, not at the host's pace, and a guest
  walks as smoothly as the host.
- **Your time is your own.** A player who goes to bed sleeps at once; the night passes for the world when enough
  players sleep at the same time (the host chooses: everyone, half, or one). A player away from the keyboard no
  longer holds the night. When another player travels or sleeps, the date moves on, but you are no hungrier, your
  food does not rot and your quest deadlines do not move.
- **Big worlds.** Time can stop when nobody plays (a map a player holds alone stops too while that player is away),
  and a long absence is caught up in steps of 1, 3 or 7 days, the host's choice. Quests wait for absent players: a
  player who comes back finds the days the world lived in the meantime added to its deadlines. Every player of the
  world is listed in the adventurers' ranking.
- **A living world** (host choice, off by default). The world's date follows real time at a pace the host picks: a
  day every 20, 40, 60 or 120 real minutes, whether players act or stand still. It stops when nobody plays, and a
  night of the world runs to the morning. Each player's own actions still count in that player's own turns.

### To each player their own

- **Companions** follow the player who recruited them, whatever the way (dialog, monster ball, mount, bought
  animal), travel with them, count in that player's ally limit and obey that player's orders.
- **Random quests, fame and karma.** Five quests each, with their reward. Crime too: the player who did it loses
  the karma, and guards only chase that player.
- **Money.** The money of a shipping sale goes to whoever put the item in the chest; a guest pays a bill with its
  own gold.
- **Your character.** When joining, you get the character you played, with no question asked; a new player makes
  one on the game's creation screen. The host can also let players bring a character from one of their solo saves,
  and let them **take a character out to a solo save**: it plays there, comes back once (its bag, gold and fame with
  it) and is marked away in the world meanwhile. Each player has a limit of characters (3 by default, the host's
  choice) and the host sees a list of everybody's characters, with Delete.
- **Your own journal.** Each player keeps its own message history and its own records (digs, deaths, nights slept)
  across world copies and trips, instead of reading the host's (host box, on by default).
- **Your settings.** Windows, auto combat, bag sorting and companion orders stay yours at every map change and
  reconnection.
- **Auto combat protects your own.** The game's auto combat saves the player who runs it and its companions from a
  deadly hit (with that player's own tactics skill and stamina). Before, the host's auto combat protected everyone
  and a guest's protected no one. A companion that belongs to nobody is protected by no one.
- **Your title.** The title (alias) you chose is yours: a guest no longer gets the host's, and keeps its own from one
  session to the next.

### One world for everyone

- **Story.** Story quests are in one log: anyone starts, advances and finishes them. Dialog memory, key items and
  debt are shared.
- **Home base.** A guest builds like the host (floors, walls, furniture, mining, digging, cutting, zones, the
  terrain tool) and what one builds is seen by the other at once. Research, hearth skills, policies, residents and
  chest settings can be managed by any player. The host can keep the base to itself with one checkbox.
- **Guilds, affinity, codex, bank.** Joining a guild counts for the group, an inhabitant's affinity is the same for
  all, the cards one player collects reach everyone's codex, and the bank shows the same content everywhere.
- **Dungeon quests.** Any player can take a quest that has its own zone. When one sets out, the other gets a Yes/No
  box to come along: the zone is shared and the reward goes to whoever took the quest.
- **Map music.** The music chosen for a map in its playlist window is the same for every player on that map.
- **Between players.** A trade window (both put items and gold, both confirm; a worn item comes off when traded),
  duels where nobody dies and nothing is lost (with a *Give up* in your own menu), and no killing outside a duel.
  Two options (host boxes, off by default) go further: a duel with the host is fought in an **arena**, a zone made for
  the occasion, and each side can put a **gold bet** that the winner takes (stakes are given back when the duel
  has no winner; giving up loses the stake).
- **Duels with adventurers** work for every player like for the host: the adventurer leaves town for the fight, the
  week before the next bout is the same for all, a win is recorded and the reward comes from the adventurer.
- **Permadeath with friends** (host option, on by default, permadeath worlds only): a player who dies waits in the
  world instead of the game ending, and any other player revives them at a bartender for the game's price. Nothing
  is deleted, not even when the host dies.

### Teams (optional)

Off by default (host box *Teams*). When the host turns them on, players form teams: each team has its own land,
chests, buildings and doors, a land deed for its founder, and, if the host allows it, PvP between teams. Another
team's chests cannot be used, not even for crafting (the host refuses it too). A new player appears at the spawn
zone: the start meadow, the host's map, or a zone given by name or number. Without the box, nothing changes. Three
more host boxes, each off by default:

- **Each team pays its own land.** One monthly bill per team, for its own land only, on the highest fame of its
  members (land of nobody: the wanderers' bill). Unpaid bills are counted per team, so only that team's players lose
  karma.
- **Each team keeps its own story journal**: the furthest main-quest chapter the team took part in, shown on the
  team card. The story itself stays the world's.
- **Each player or team learns on its own** (also works without teams): recipes, crafts and the codex belong to the
  team (a team shares one book, a player without a team has its own). A scroll another player reads is learnt in its
  own game, kills count for the killer's team, recipes the story gives reach every team, and what a player knew
  follows it from team to team.

### Each player or team lives its own story (experimental)

Off by default (host box *Each player or team lives its own story*), and **always on on the official server**. Each
player goes through the main story at its own pace (Ash's quests, the exploration of Nymelle and its dialogs); a
team shares one story, a player without a team has its own. When the box is ticked everyone starts from the world's
current progress. Farris only joins the first base; a player or team arriving on a Nymelle floor another one has
already cleared carries on from there; story rewards are paid once per player. The save keeps the host's own story:
untick the box, or open the save without the mod, and the world goes on with it. Off: one shared story, as before.

### Time and settings made simpler

- **World map time, three modes** (host choice): *Shared clock* (one clock, the world map waits for no one),
  *Travel together* (the group moves on the world map as one, a player still loading is waited for) and *Personal
  pace* (each player travels at their own pace; the others catch up when they regroup).
- **Settings reorganised.** Presets and plain choices on top ("Recommended co-op", "Classic host-led", or Custom),
  every individual box kept in an *Advanced* section, a description under each box, dependent boxes greyed out,
  "Reset to recommended" and "Undo". Friends see "This world's rules" read only. The same page is in the game's own
  mod config page.

### The world reads every player

- **World content from every player, not only the host.** Town boards (fame gate, danger, rewards), shop stock and
  special shelves, map spawns, barrels, aging shelves and mother's milk follow the players on the map instead of the
  host's character alone.
- **Base and town life for every player.** Raids, lucky month, days with the player and rain on a farm count every
  player; townspeople, priests, nefia bosses, rumours and stones act around each player and by that player's karma;
  a boss kill credits the depth to every player on the map; summons follow their own master.
- **Each player's fortune, diaries and counters** (fortune rolls, reconstruction level, diaries, hoard, known
  crafts, sketches, tunes, respawn anchors) belong to that player and survive world copies. Recruits are paid once.
- **Auto combat per player**, combat parity (weak spots, summon floor, throw growth, speed) and a duel with the
  game's own rules.
- **Difficulty follows the player.** A quest's difficulty follows the player who takes the quest.

### A guest plays like a solo player

Dozens of fixes so that what works for the host works for a guest: death and will, the god's gifts, altars, traps,
spellbooks, the healer, blessings, investing, runes, auto-dump, tools held in hand, Kettle's copy shop, bringing a
companion back at the barman's, windows that used to open on the host's screen, hitching posts, a guest's
companion using the guest's scavenger feat on traps, a story resident who stands by the guest who finished the
quest, "the day ends" read by guests, a guest's "always sort" inventory that froze its game, a guest who quits
while away no longer taken for a lost link… Newer ones: a defense quest's war horns sound in the host's game for a guest, a relocation deed moves the base in
the host's world for everyone, a guest's world-map journey is paid when it walks into the host's town, the
militant feat and the void boss floor read the player's own side, Nasu's fairy dungeon and Nasu's stairs look for
the Nasu of a player who is there, a meal quest asks for a dish of the taker's cooking, saved party setups and the
character sheet's total feat stay the player's own across a world copy, the base's mail is each player's, a
wedding seats the base's people, an achievement goes to the player it is about, and encounters are rolled by the
visitor's own game. Each guest also keeps its own story scenes, backers list and Churyu count across world copies. The full list is kept in the developer's notes, not published.

### The world does not depend on the host's PC

- **A depot keeps the world**: a private GitHub repository, a shared folder, or a small server application. The
  first player there takes the world and hosts it, every save goes back, and when they leave the next one takes
  over.
- **A world that changes hands.** Whoever takes the world over plays their own character; the former host gets
  theirs back when joining. If someone already hosts, you are sent into their game instead of opening a copy.
- **Nothing left for the host to do.** The world saves itself every 2 minutes while others play, and a world
  already shared opens to Steam friends as soon as it is loaded.
- **A dropped link is harmless.** A guest who loses the connection comes back into the game by itself (it tries
  for 3 minutes).
- **The games stay in agreement.** No message is dropped when the link is saturated. Every 2 seconds the games
  compare a few numbers per map, and a map that keeps differing is reloaded in place.
- **Safer network data** (taken from the original mod). What another game sends is only read as the game's and its
  mods' own kinds of data; anything else is refused.
- **A dedicated server**, like a Minecraft server: an Elin nobody plays keeps the world running, reachable through
  Steam or by address. It makes a backup of the world every hour, and the *Elin Together Server* app starts it again
  if it crashes, restores a backup, starts with Windows and shows players, the admin log and live graphs.
- **The host leaves cleanly.** When the host quits for good, the guests are told so instead of waiting on a frozen
  screen.

### Public servers: admins and safety

- **Moderation.** Kick with a reason (the player cannot come back for a few minutes), ban and mute by Steam account
  (kept in the world), admins named by the host who get Kick, Mute and Ban buttons in the *Players* tab, and a
  *Members only* mode. Every action is written to `ElinMP/Logs/admin.log`; a dedicated server takes the same orders
  from its app or from `ElinMP/server.cmd`.
- **The official server.** The title screen lists the Elin Together account's public game first, marked as official,
  with its player count (a quiet line when it is not open); it takes up to 100 players. Its entry is signed, so a
  game that only calls itself "Elin Together" is not shown as official. The official server refuses imported
  characters and always runs each player's own story.
- **Safety against modified games.** The host refuses what a guest has no right to do (other players' items and
  characters, made-up gold or items, chat under another name), always in a public game and on a dedicated server
  (box *Refuse acts on what others own* otherwise). A crafted packet or map can no longer crash the host or run code. Dialog outcomes, shared quests, story gifts,
  personal quest rewards, base resources, town investments, world time and knowledge messages, bubbles, pings and
  private lines are all checked on the host's side, a failed check refuses, and a player is hosted on another's map
  only by a teammate or a vouched player.
- **Many players.** Each player only receives what concerns its own map, players arriving together share one copy of
  the world, snapshots carry only the characters that changed, and the load was measured with up to 50 simulated
  players.
- **Report a bug from the game.** *Client Settings → Help → Report a bug to the developers*: a real text box (Enter
  for a new line, Ctrl+Enter to send), and the report goes with the last 30 seconds of your screen as a video and
  your logs. The public issue on GitHub holds your words, your Steam name, the versions and the video; the logs
  (they hold Steam ids) are kept privately for the developers. If it cannot be sent, GitHub's page opens, already
  filled.

### The same mods for everyone

- **The mods of the game are fetched by themselves** when you join, and Elin restarts once with them. A player can
  choose to keep them, so that this restart happens once and never again for that game.
- **Different Elin versions can play together.** Only the mod's version must be the same for everyone; players on
  another Elin version are let in with a warning.

Almost all of this is a **checkbox or choice on the host's side**: off, the mod behaves like the original. See
[Settings](#settings).

## Install

You need Elin on Steam, on the **Nightly** branch (Steam → right-click Elin → Properties → Betas), and the
[YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753) mod (the Workshop install below
brings it along). There are two ways to get the mod; both give the same version 1.0. Use one of them, not both.

**A. Steam Workshop (simplest, updates by themselves)**

1. Subscribe to [Elin Together](https://steamcommunity.com/sharedfiles/filedetails/?id=3773298709) on the Workshop.
2. Launch Elin through Steam, check that *Elin Together* is ticked in the game's mod list, and restart if the game
   asks.

**B. The zip (also on the [Releases page](https://github.com/jeanfreger/elin-together-independence/releases))**

Launch Elin once with YK Framework installed, then close it.

1. Download `ElinTogether-independence.zip`.
2. Unzip the whole folder and run `Install.bat`. It finds Elin, copies the mod into `Elin\Package\Mod_ElinTogether`
   and switches from the Workshop version to this one in the game's mod list.
3. Launch Elin through Steam.

`Uninstall.bat` switches back to the Workshop version; nothing is deleted, the former state is kept in
`Elin\_ElinTogether_sauvegarde`. The installer and its notes (`README.txt`) are in English (the notes also in
Chinese); the steps are also on each release page. If you used the zip and now prefer the Workshop, run
`Uninstall.bat` first.

**Every player must have the same version.** This mod does not talk to the original Elin Together, nor to another
version of itself: a player on another version is refused with a message that names both versions. Each release is
built for one Elin build, named in its title.

**Updating**: on the Workshop, Steam does it; with the zip, download the new one and run `Install.bat` again. Your
settings are kept.

## Your first game together

The mod's panel opens with the *Elin Together* button on the title screen, or Esc → Mods → Elin Together in game.
Its tabs are *Lobby*, *Server Setting* (the host's rules) and *Client Settings*, plus *Session Info* once a game is
open.

**The host**

1. Starts a new game or loads a save. A game can only be opened to others once the world has a **claimed land**:
   in a new game, talk to Ashland, pick up the deed he drops, read it and answer yes.
2. Opens the game: *Start Server* in the Lobby tab. A world that was already shared opens to Steam friends by
   itself when it is loaded.
3. Invites: *Invite Friend* in the Lobby tab, or lets friends use *Join Game* on their name in the Steam friends
   list.

**A guest**

1. Joins from the title screen: accepts a Steam invite, uses *Join Game* in the Steam friends list, or picks the
   game in the Lobby tab. *Join by address* is for a server.
2. The first time in that world, makes a character on the game's own creation screen. Later it gets that character
   back with no question.
3. If the game has Workshop mods the guest does not have, Elin closes and restarts once with them, then comes back
   into the game by itself: see [Games with mods](#games-with-mods). **This is not a crash.**

**Then**

- Everyone plays as in a solo game: walk out of the map alone, take quests at the board, build in the base, sleep
  when you want.
- The host's rules are in *Server Setting*; a change applies at once, for everyone.
- When the host leaves, the session ends for everyone unless the world is in a depot (next section).

## Keeping the world online: the depot

A depot keeps the shared world outside any player's saves, so the group does not depend on one person being
there. Each player who may host sets it once, in *Client Settings*: **Depot**, and **Depot key** when there is a
password or a key. A friend who only joins needs neither.

| Depot | What it takes | "Depot" setting |
|---|---|---|
| Private GitHub repository | A GitHub account for one player of the group. No PC left on, no port to open. | `github:owner/repository` |
| Elin Together Server (`ElinTogetherServer.exe`, in the zip) | A PC that stays on. Elin is not needed on it. TCP port 55557. | `host:55557`, or just *Join by address* |
| Shared folder | A folder every player can reach, shared or synced. | The path of the folder |

Then, in the Lobby tab:

- **Put this save on the server**, with a save loaded and the game not yet open to others: that save becomes the
  world of the depot. The game goes back to the title screen and loads the world again from the depot: from then on
  it is that world you play.
- **Take the world from the server and host it**, from the title screen: you get the latest world and host it;
  the others join you through Steam. If someone already hosts, the button names them and lets you join their game.
- Every save goes back to the depot, at most every 5 minutes and once more when quitting. When the host leaves,
  anyone can take the world. If the host crashes, the world frees itself after 3 minutes, and up to 5 minutes of
  play can be lost.

**With GitHub**, the player who owns the repository:

1. creates an empty **private** repository on github.com, only for this world (the mod refuses a public one);
2. creates a fine-grained access token (Settings → Developer settings → Fine-grained tokens) limited to that
   repository, with *Contents: Read and write*;
3. sets **Depot** to `github:owner/repository` and pastes the token into **Depot key**;
4. gives both to the friends who may host. They need no GitHub account.

The key stays in plain text in the mod's settings file on each PC; it only opens that repository and is revoked in
one click. A world over 20 MB zipped is refused. GitHub keeps every version of the world, which is a free backup
and also means the repository grows with each save: about one small commit a minute while someone hosts, and a
copy of the world every 5 minutes. Delete it and create a new one when it gets large. The depot also holds
`modlist.txt`, the mods of the world.

**Dedicated server.** `ElinTogetherServer.exe` has a second mode, "With Elin on this PC: the world runs all the
time": Elin runs behind without a window; players join through the server's Steam lobby or *Join by address*
(`host:55556`, UDP). The app starts Elin again if it stops by itself, keeps an hourly backup of the world (restored
in one click while stopped), can start with Windows (`--elin <save>`), and has pages for moderation (kick, mute, ban,
a message to everyone), the admin log and the server's state (players, network, memory). `Server.bat` does the same
with a game window. Windows only.

## Games with mods

Elin loads its mods when it starts, so a player cannot receive a mod in the middle of a run. This is what the mod
does about it.

**When you join** (or take a world from a depot), the mod compares your mods with the reference list: the
`modlist.txt` of the depot when the world comes from one, else the host's mods. If Workshop mods are missing:

1. they are downloaded by Steam, without your account subscribing to anything;
2. Elin closes and restarts **once** with exactly the mods of that game;
3. it brings you back into the game by itself. If it does not restart, start Elin by hand within half an hour.

By default your own mod list is back at the next start of Elin. The price: Elin restarts again at the first join
of **every** start. Two ways to avoid that:

- **Tick "Keep the mods of the game (subscribe on the Workshop)"** in *Client Settings*. Your Steam account
  subscribes to the mods of the game and they stay on in your own list. Elin restarts once more, then no longer for
  that game, even after closing Elin. To undo, unsubscribe on the Workshop or switch them off in the Mod Viewer.
- Or subscribe to those mods yourself on the Workshop.

To keep your own mods and only be told what differs, untick "Fetch the mods of the game by itself".

Good to know:

- A mod installed by hand, outside the Workshop, cannot be downloaded: its name is shown.
- The mod that brings the language you read the game in is never switched off.
- A join refused because of mods names them: missing, to install by hand, extra.
- **These mods are chosen by the host and their code runs on your PC: use this with people you trust.**
- AutoAct and Dynamic Riding are supported. For other mods, compatibility is the original's.

## Settings

### Host: the *Server Setting* tab

Each new behaviour has its own checkbox and a line of explanation in game. The rules are the host's and apply to
everyone as soon as they change.

| Checkbox | Default | Ticked |
|---|---|---|
| Independent travel | on | Everyone goes where they want, alone or together. Off: everyone follows the host. |
| Players choose their character when joining | off | A joining player picks one of its characters in this world, or makes a new one. Off: the one it played last. |
| Players can bring a character from their own saves | off | The character, its equipment, bag, gold, fame and karma. Not its companions, base, quests or bank. The save is only read. Never on the official server. |
| Friends can take a character out to a solo save | off | In the character list, a character can be taken to a solo save (backed up first); it is away from this world until brought back or recalled as it was. Needs the box above. |
| Characters per player | 3 | How many characters one player may have in this world (1, 3, 5, 10 or no limit). The host also has a list of everybody's characters, with Delete. |
| Revive at the bartender (permadeath) | on | Permadeath worlds only: a dead player waits in the world and any player revives them at a bartender. Off: the game's own permadeath (the host's death deletes the world). |
| Public game | off | Anybody who finds the game can join, not only friends; the safety checks are always on. |
| Kicked players wait | 5 min | A kicked player cannot come back for that long (Off, 5, 15, 60 min). |
| Members only | off | Only the players of this world (and accounts let in with `allow`) can join. |
| Refuse acts on what others own | off | A guest's acts on others' characters and items, and floods of data, are refused. Always on in a public game and on a dedicated server. |
| Random quests, fame and karma per player | on | 5 random quests each, with their reward, fame and karma. Story quests stay shared. |
| Shipping per player | on | Shipping money goes to the player who put the item in. Off: all to the host. |
| Trade window between players | on | Click another player to trade items and gold. Both confirm. |
| Other players can use build mode | on | The other players build in the base; they pay the gold and the materials. Off: only the host builds. |
| Only the host manages the base | off | What the other players ask of the base (research, policies, residents, build mode…) is refused. |
| Duels between players | on | "Challenge to a duel" on another player's character. Nobody dies, both are healed, nothing is lost. |
| Every player can bout adventurers | on | A guest fights an adventurer like the host: the adventurer leaves town, the win is recorded, the reward is the adventurer's. Off: the guest only fights in its own game and nothing is left in the world. |
| Duel with the host in an arena | off | An accepted duel with the host is fought in a zone made for it; both go back where they were afterwards. Needs duels. |
| Duel bets | off | The challenger names a gold stake (none, 100, 1,000 or the smaller purse); the winner takes both stakes, a duel without winner gives them back, giving up loses yours. Needs duels. |
| Each player's auto combat protects its own | on | Auto combat saves from a deadly hit the player who runs it and its companions, with that player's tactics and stamina. Off: the host's auto combat protects everyone, a guest's no one. |
| Players can kill each other | off | Off: a strike that would kill another player's character leaves it at 0 hit points. |
| Combat on each player's time | on | A monster acts when the player it fights acts: nobody waits. Takes over turn-based combat. |
| Each player on its own clock | on | A guest walks and acts as smoothly as the host, not at the pace of the network. |
| Every player walks at the pace of a solo game | on | The length of a step no longer depends on the other players' speed. |
| One date for the whole world | on | Time passed by a player alone on another map counts for everyone. Off: only the host's date counts. |
| What time does to the world happens once | on | Weather, expired quests, taxes, salaries and letters are handled by one game only. |
| Everyone sleeps for themselves | on | A player who goes to bed sleeps at once. The night passes for the world when enough players sleep at the same time. |
| Night passes when | Everyone | Dropdown: *Everyone*, *Half* or *One* of the active players asleep. |
| Away after | 5 min | A player without input for that long no longer holds the night, the world map or the date (Off, 5, 15, 30 min). |
| Time stops when nobody plays | off | The world's date waits while no player is connected; a map a player holds alone stops while that player is away. |
| Living world | off | Dropdown: the date follows real time, a day every 20, 40, 60 or 120 minutes, players acting or not. Off: the date follows the players' turns. |
| Quests wait for absent players | on | Needs personal quests. A player who comes back finds the days the world lived meanwhile added to its deadlines. |
| Catch-up days | 7 | A game far behind the world's date lives at most the last 1, 3 or 7 days (one per frame) and skips the rest. |
| World map time | Shared clock | Dropdown: *Shared clock*, *Travel together* or *Personal pace* (see above). |
| The base counts every player | on | Raids, lucky month and days with the player count every player on the base map. |
| Barrels and milk follow the player | on | Aging shelves, drying bricks and mother's milk use the player they are about. |
| Summons follow their summoner | on | A summon stays with its own master when the map changes. |
| Town boards from every player present | on | Fame gate and danger follow the players on the map (needs personal quests). |
| Shop stock from every player | on | The best player's bonus counts; special shelves follow the player who opens the shop. |
| Spawns from the nearest player | on | Map spawns read the feats of the player nearest to the spawn. |
| Town life around every player | on | Townspeople, rumours and stones act around each player, by that player's karma. |
| Tax on the most famous player | on | The monthly tax is computed on the highest fame among the connected players. Off: on the host's. |
| Auto-dump spares hand and tool belt | on | Auto-dump leaves what you hold and what is in your tool belt. Off: as in the game. |
| Players come back by themselves after a connection loss | on | A player who loses the connection joins the game again by itself, for 3 minutes. |
| Repair the map by itself | on | A player whose map no longer matches the host's loads it again, at most once every 30 seconds. |
| The world is saved by itself while others play | on | Every 2 minutes while another player is in the game, and when the last one leaves. |
| The game opens to friends by itself when a world is loaded | on | Friends can join without the host starting the server by hand. |
| The other players keep a copy of the world | off | After each automatic save, every player's PC keeps a copy of the world, outside its own saves. |
| No world reload when the host and a player meet again (new) | off | The player stays on its map, or loads that one map, instead of the whole world. |
| Show the mods of the game to the players | on | The mod list is known before joining, and a refused player is told which mods differ. |
| Require the same Elin version | off | Players on another version of Elin are refused. Off: let in with a warning. |
| Turn-Based Combat | on | The original's rule. No effect while "Combat on each player's time" is on. |
| Shared Average Speed | off | The original's rule: one speed for all players, the average. |
| Each player has their own journal | on | Message history and records (digs, deaths, nights slept) are the player's own across trips and world copies. |
| Players can be in teams | off | Players form teams; the host keeps who is in which team in its save. Off: no teams, everything as before. |
| A base belongs to its team | off | A base belongs to the team that claimed it: other teams cannot open its chests, pick up, build or manage there. |
| Joining a team needs its leader's approval | on | A player asking to join a team waits for its leader to accept. Off: joins at once. |
| A land deed for a new team | on | The founder of a new team receives one land deed. Off: the team buys it from the merchant. |
| Personal ownership inside a team | off | A chest, door or bed one installs is that player's; "Share with team" gives it back. |
| Where a new player appears | start | Where a new player appears: *start* (the start meadow), *host* (the host's map) or a zone name or number. |
| PvP between teams | off | Players of different teams are enemies outside towns and the spawn zone. Death still needs "Players can kill each other". |
| Each player or team learns on its own | off | Recipes, crafts and the codex per team (a player without a team has its own). Off: one book for all. |
| Each team pays its own land | off | One monthly bill per team. Needs teams. |
| Each team keeps its own story journal | off | The furthest main-quest chapter each team took part in. Needs teams. |
| Each player or team lives its own story (experimental) | off | Own pace through the main story, per team or player. Forced on on the official server. |

### Player: the *Client Settings* tab

| Setting | Default | What it does |
|---|---|---|
| Bind Ping Key | P | The key that pings a place on the map for the others. |
| Depot | empty | Where the shared world is kept: `github:owner/repository`, `host:55557` or a folder. |
| Depot key | empty | The password of the server, or the GitHub access key. Never shown again once typed. |
| Fetch the mods of the game by itself | on | Missing Workshop mods are downloaded and Elin restarts once with the mods of the game. |
| Keep the mods of the game (subscribe on the Workshop) | off | Your Steam account subscribes to them and they stay on: no restart the next time. |

These settings are in `Elin\BepInEx\config\dk.elinplugins.elintogether.cfg`. That file holds the depot key in plain
text: do not share it.

## How it works

### The original design

One game, the host's, simulates the world. Every change (a step, a strike, an item picked up) leaves it as a small
message, a **delta**, that the other games apply. The other games send their actions to the host as requests. The
host is the reference: when two games disagree, its version wins.

The fork keeps that and adds the following.

### Leaving the host's map: zone leases

A player who walks out of the host's map asks the host for a **lease** on the map it goes to. With the lease, its
own game loads its copy of the world and simulates that map itself: monsters, time, items. Its link with the host
stays open for chat, quests and dialog memory.

- Every minute (a **checkpoint**), and when it comes back, it sends the map and its character to the host, which
  stays the reference. A crash loses what happened since the last checkpoint.
- A dungeon quest works the same way, on a new zone created for the occasion and destroyed afterwards.
- Each lease reserves a range of numbers for the quests created on that map, so two games never give the same
  number to two different quests.

### Meeting on a map: zone sessions

The holder of a lease can open that map to others: it becomes the host **for that map**, and the others connect to
it as guests, on top of their link with the real host. When the holder leaves, the lease is **handed over** to a
player who stays, and the others reconnect to that one. The same mechanism is used when the host itself leaves its
map: the first player who stays receives the lease.

When a player walks into a map someone else holds, it receives that map from its holder. Without the checkbox "No
world reload when the host and a player meet again", it first receives a fresh copy of the whole world, which is
why the screen reloads.

### To each player their own

- **Companions** carry the number of their player. They follow that player, travel with it and count in its ally
  limit only.
- **Random quests**: each game keeps in its journal only the random quests of its own player. The host stores
  everyone's in the save, with each player's fame and karma, and gives them back when the player arrives.
- **Shipping**: every item put in the chest is marked with the number of whoever put it. At 5 a.m. the host sells
  everything, keeps the accounts per player and sends each one its money, or keeps it for an absent player.
- **Karma and crime**: the game removes karma "from the player" where the action is settled. The mod finds the
  player behind the killer or behind the task and sends the penalty to that one. When a guard looks at someone,
  "is the player a criminal?" is asked about the player it looks at.
- **Combat and pace**: a monster bound to a player only moves when that player takes a turn. Each game runs on its
  own clock, so a guest's steps do not wait for the network.

### One world for everyone

- **Story quests**: the host holds the only journal. A dialog played by a guest runs in the guest's game; what it
  changes in the world (a quest that moves on, a land claimed, a character who joins) is sent to the host, which
  does it for everyone. What the dialog gives is created by the host at the guest's feet, once.
- **Base**: research, hearth skills, policies and resident settings are requests to the host, which checks, pays
  once and sends the result to everyone. In build mode, the guest's game prepares the task (place, mine, cut) and
  sends it to the game that holds the map, which carries it out with the guest's gold and materials. The game that
  simulates a map sends, at the end of each frame, the state of every tile that changed.
- **Affinity, guilds, codex**: the game of the player who acts computes, the host keeps the value and sends it to
  all.
- **Trade**: the game that simulates the map holds the "table". It receives the intentions (invite, accept, offer,
  confirm), sends the state to both players and makes the transfer in one go, after checking again that every item
  and every coin still exists.
- **Players against players**: a strike that would kill another player's character leaves it at 0 hit points,
  unless both are in a duel, where the fight ends at that floor and both are healed.

### Own story and own knowledge

The main story and the book of recipes and codex have owners: a team, or a player alone. The host holds its own and
sends each guest its own with the world; a guest's story steps are checked and applied in its own story, and relayed
to its teammates only. The save also keeps the world's original story and book, so that unticking the boxes gives
the world back as it was.

### Time

There is one date for the world: the furthest ahead. What time does to the world (weather, taxes, salaries,
expired quests) is done once, by one game. What time does to a player (hunger, food that rots, quest deadlines)
is counted per player, so another player's trip or night costs you nothing. With the *Living world* box the date
instead follows real time at the pace the host picked. A player who goes to bed sleeps its own
night in a few seconds; the world's night only passes when everyone sleeps at the same time.

### Joining, and staying in agreement

1. **Handshake**: the two games compare the mod's version (must be the same), Elin's version (a warning) and the
   list of mods.
2. **Character**: the host says which characters of this world are yours; a new player makes one.
3. **World copy**: the host sends its save; your game loads it.
4. **Map**: the host sends the state of its map, then the tile where your character stands. What the others did
   while you were loading is kept and replayed afterwards.

After that, every 2 seconds the games compare a few numbers per map (how many things, how many characters, a
checksum). A difference that lasts is written in the logs, and the map is loaded again in place, at most once every
30 seconds and never during a fight or with a menu open. A guest whose link drops tries the same game again every
5 seconds for 3 minutes.

### The depot

A depot is the world as one archive (`world.zip`) plus a lock that says who hosts (`lock.json`). The host renews
the lock every minute; a lock that has not been renewed for 3 minutes is free. With GitHub, every write names the
version it replaces: of two players writing at once, GitHub accepts one, and that refusal **is** the lock. Nothing
is ever forced, and the history keeps every world.

The save of a world remembers whose its local character is. Loaded by another player who has a character in it,
the two are exchanged before anything is played: the one who takes the world plays its own, the former one waits
for its player.

### Where things are on your PC

| What | Where |
|---|---|
| The mod | `Elin\Package\Mod_ElinTogether` |
| Its settings | `Elin\BepInEx\config\dk.elinplugins.elintogether.cfg` |
| Its logs | `%USERPROFILE%\AppData\LocalLow\Lafrontier\Elin\ElinMP\Logs\Session_<date>.log` |
| The game's log | `%USERPROFILE%\AppData\LocalLow\Lafrontier\Elin\Player.log` |
| Saves | `%USERPROFILE%\AppData\LocalLow\Lafrontier\Elin\Save` |
| The world taken from a depot | `…\Save\world_depot` (replaced each time the world is taken) |

## Known limits

- **Played for real by one small group only.** Almost everything is checked by in-game tests on one PC (two or three
  windows). "Not played" in a release note or a commit message means exactly that.
- **One date for the world.** How a trip on the world map moves it depends on the host's *World map time* choice;
  the shared night only passes when everyone sleeps.
- **When the host leaves or crashes**, a guest can take the world over by itself: the checkbox exists ("Takeover") and
  stays off until it has been played between two real PCs. Without it someone takes the world from the depot.
- Meeting the host again no longer reloads the whole world (ticked by default since 0.26.691); at the slightest doubt
  the mod reloads as before.
- A player refused by the holder of a map stays where it is.
- On the host's screen, walking guests can move in jumps of three tiles.
- **Many players**: five game windows were tested on one PC and up to 50 simulated players (a load tool that speaks
  the protocol), never more than three real players at once. With many players, everyone arriving at the same time
  makes the host pause about half a second to a second per group.
- Dungeon quests for two, when the guest took the quest: subdue, harvest and music quests. Defense quests are
  settled alone. An adventurer duel started by a guest leaves the adventurer in two places during the duel.
- Arena and bets only exist in duels with the host; a duel between two guests is fought where they stand, without bets.
- Each player or team living its own story is experimental: the endings of the main story and every dialog were not
  all walked through with several real players.
- Mods: Elin restarts once at the first join (see [Games with mods](#games-with-mods)). A mod removed from the game
  later stays subscribed for a player who chose to keep the mods. Not tried on Steam Deck, Linux or a Mac.
- Catching up worlds played before "a share for everyone": recipes, the tickets of the debt, the wedding gifts and
  dialog gifts are not given again.
- A character taken out to a solo save is away from the world until it comes back; a solo save that is never loaded
  keeps it away (the host can recall it as it was).

The full list is kept in the developer's notes, not published.

## What is left before a real release

Version 1.0 is meant to be usable by a group of friends and by a public server. Here is what is still not proven
(as of 28 October 2026).

**1. Not tested between two real PCs (marked so in the release notes)**

- Almost everything is proven on one PC, over the local network. Steam lobbies, invitations, real latency, three or
  four players and a long game are only covered by a few real evenings. In particular, never played between two real
  PCs with two Steam accounts: the official server's entry in the list of games (Debug lobbies are private, so the
  list itself was not seen), the Steam link replacing an older one of the same account, arenas and bets, the whole
  of "each player or team lives its own story", teams paying their own land, the living world over a long play,
  taking a character out to a solo save, the bug report's video on another PC, and the new checks of public servers
  with real strangers.
- A guest taking the world over ("Takeover"): played with three windows, never between two PCs.
- Many players: 50 were simulated, never more than three real players at once.

**2. Safe releases**

- The whole chain of tests must be green before each release; a few series are still unstable (night, guest).
- A world hosted for the first time with a new version is first copied to the game's own backups. Loading such a
  world in an Elin without the mod, or with the original mod, has never been played.

**3. Installing**

- The mod needs Elin's **Nightly** channel, and each release is built for one build of the game.
- Both the Workshop and the zip are provided. Every player must have exactly the same version.

**4. Missing pieces**

- Mods: the host's list applied without a restart; a game whose mod list changes.
- Defense quests for two. The online depot of the world: automatic take-over from the depot.

**5. Before recommending it to anyone**

- The Chinese and Japanese READMEs are shorter than this one and were not proofread by a native speaker. In game,
  the mod's texts (menus, settings and their tooltips, messages, patch notes) are in English, Japanese and Chinese;
  other languages see them in English.
- To report a defect: the *Report a bug to the developers* button of *Client Settings → Help* (see
  [If something goes wrong](#if-something-goes-wrong)).

## How it is tested

A change is meant to come with an in-game test, named in its commit message; when it was published without one,
the commit and the release note say "not played". The tests drive real game windows through a debug bridge (Debug
builds only): a host window and one to four guest windows on one PC.

- **Suites by subject**: travel, shared maps, quests, trade, base, building, duels, sleep, death, the depot,
  mods… Each prints what it checks and what it does **not** play like a player.
- **A first-time game**: two players playing together for the first time on a brand-new world, through the game's
  own screens: character creation, the opening text, the deed, opening the game, the guest joining, Ashland's
  gifts, then save, both games closed, started again and joined again.
- **A dialog hunt**: each player walks to every character of the map and tries every choice of its menus; a loop,
  a game error, two games that no longer agree afterwards or a doubled item is a defect.
- **The bot**: plays at random on one window and checks that the games still agree.

The test bench is not published.

What the bench cannot prove: Steam between two real PCs, a real mouse, and anything nobody thought of testing.
The real evenings found those. The rule of the project: read the logs before concluding, and say what was not
played.

## If something goes wrong

- The host unticks the checkbox involved: for that point the mod behaves like the original again.
- A player whose game no longer matches the others' types `emp.reconnect_self` in the console.
- Elin closed by itself at the first join: it is the restart for the mods, see [Games with mods](#games-with-mods).
- Report it from the game: *Client Settings → Help → Report a bug to the developers* (your words, the last 30 seconds
  of your screen as a video, and the logs). Or [here](https://github.com/jeanfreger/elin-together-independence/issues),
  with every player's `Player.log` and `ElinMP/Logs/Session_<date>.log` from
  `%USERPROFILE%\AppData\LocalLow\Lafrontier\Elin`. Please do not report fork problems to the original project.

## Build

The mod builds with `dotnet build ./ElinTogether -c ReleaseNightly`. The game must be installed: set the `ElinGamePath`
environment variable to its root folder. `SteamContentPath`
(`steamapps/workshop/content`) gives `YKFramework.dll`. .NET SDK 11.0 preview, see `global.json`.

```ps
git clone https://github.com/jeanfreger/elin-together-independence
cd elin-together-independence
dotnet restore ./ElinTogether --locked-mode
dotnet build ./ElinTogether -c ReleaseNightly
```

The release packaging and the test tools are kept off the repository.

## Credits

The mod is the work of the Elin Together team: [DK](https://github.com/gottyduke) and
[Redgeioz](https://github.com/Redgeioz) (code, framework), [105gun](https://github.com/105gun) (code),
[Han](https://github.com/chuahan), Omega, [InuiDame](https://github.com/InuiDame),
[Drakeny](https://github.com/Drakeny) (testing), noa (Elin). MIT license, see [LICENSE](LICENSE).

Testing of this fork: lemiwinks, nardole, 梦天冰, fartcreature.

Made by jeanfreger together with Claude (Anthropic). The fork's changes were written with an AI coding assistant (Claude Code), directed by the fork's owner. No game
file and no decompiled game code is in this repository.
