# White Knuckle Race

Play a friend up the same seeded White Knuckle world and see where they are. You get live splits at every level transition, a translucent ghost of your opponent, and matchmaking from a WKSR (White Knuckle SpeedRun) button in the game's main menu.

It has two parts:

| Part | What it does |
|---|---|
| **Plugin** (`WKRace.dll`) | BepInEx plugin. Holds WKSR, the in-game menu (drawn with the ImuiBepInEx and WKLib mods): connects to the official server, finds matches, lobbies, solo runs, leaderboards, chat. In a run it forces the game's *competitive* rules, records splits, streams your position and draws the HUD and ghosts. |
| **Server** (`WKRace.Server`) | Matchmaking, ratings and relay. Players never connect to each other directly; the server forwards positions and splits. |

## How a match works

1. Both players press **CONNECT** in WKSR (remembered from then on), then **RANKED > 1V1**.
2. When a match is found, the plugin loads the **Campaign** gamemode on the match seed, with **competitive** rules, automatically.
3. Each world is generated and frozen. When every player's world has loaded, the **ready check** starts: each player presses F9 (or READY in WKSR) within 60 seconds (`--ready-seconds`). When everyone is ready, a 5 second countdown starts and everyone is released at the same moment. Not ready in time: in a ranked match that player forfeits (rated); a friendly 1v1 is called off and both are back in the lobby; a party match starts anyway, with the unready players standing at the bottom.
4. The match clock is real time since GO. Every new level you enter is a split; the HUD shows the difference to your opponent's time at the same split.
5. First to finish wins (a 1.5 second window decides near-ties by time). Winner and loser get Elo rating changes: 16 points for an even match, twice that for a player in their first 10 ranked matches (shown as PROVISIONAL), so new players reach their level quickly.

The player name is the Steam name. Names are unique (ignoring case): a name another player already has gets a number ("Name 2").

**One run each, finish it or die trying.** Restarting is off during a match: the game's restart command (and any bind that runs it, such as `bind rr restart`), the pause and death menus' restart and F9 all refuse with a message; other binds keep working. **A death ends the match at once: whoever is still alive wins**, however far behind they are. Leaving for the menu does not stop the clock and cannot be undone either (F10 twice forfeits). A game that crashes rejoins on its own.

**Disconnects pause the match.** If a player's client, connection or game drops, both games freeze and the match clock stops. The missing player has 60 seconds to come back; after that they forfeit. When they are back, play resumes after a 3 second countdown with the clock exactly where it stopped. A player who has already finished is never forfeited and does not pause the match.

## Friendly matches (lobbies)

To play friends instead of whoever is queued, press **Create lobby**, pick **1v1** or **Party (up to 15)** and the modifiers, then **Open lobby** and **Copy code**. Send the code (it looks like `WKR-2A46-F92E`); friends press **Join game** and paste it. A code only names a lobby on the official server; it never carries a server address. Lobby matches are unranked: the result is shown, but ratings and win/loss records do not change.

**Lobby browser** (0.9.6, with a 0.9.6 server): **BROWSE LOBBIES** lists the public lobbies (`LobbyListRequest` / `LobbyList`, at most 40, joinable ones first) with host, mode, modifiers, player count and whether they are in a match; JOIN joins without a code. **PUBLIC** (ticked to begin with) makes a new lobby public (`LobbyVisibility`, host only). The three message types are new, so old and new versions share a server: the server sends `ServerFeatures` only to clients reporting 0.9.6 or later, and a client only offers the browser after that message.

A lobby is a room: WKSR shows who is in it, the host, the modifiers and the lobby chat. A 1v1 starts as soon as the second player joins; a party when the host presses **Start match** (two players or more). After a match everyone is back in the lobby (REMATCH / START MATCH again). Nobody can join while the lobby is racing. A host who leaves hands the lobby to the next player; a lobby nobody joins closes after 10 minutes. Leaving the lobby, joining the queue or starting SOLO takes you out of it.

### Party matches

Up to 15 players, unranked, no artifact bans. One life and no restarts: a death puts the player out and the match goes on. Party matches never pause: a player gone for 60 seconds is out. The match ends when everyone has finished or is out, `--party-finish-window` (default 300) seconds after the first finish, at the time limit, or when the only players left never readied up. Places: finishers by time; everyone else by `Standings.Compare` (shared by the server, WKSR and the HUD): on the same route as far as both went, more levels and then sooner; on different routes, whoever survived longer. The HUD shows a big position (`?` while on a shortcut, from the split's region: `shortcut` or its sub-regions Tangled Sink, Expulsion Chute, Rotten Duct, Hadal Diver), the gap to the player ahead and to the leader at each split (the leader sees the gap back to second), a ghost per player (labels 40% smaller above 8 players), and a standings table at the end. Party results are not written to the match history.

### Modifiers

Ranked matches always use all of them (`MatchRules.Ranked`; tests switch the bans off with `ServerOptions.RankedRules`). A lobby's host picks them for everyone; SOLO has its own picker. **Artifact bans** (1v1 only), and **curated seeds** in three parts that `RaceGuarantees` gates separately: Deep Storage food (food bar and pills), guaranteed perks, guaranteed items (slug grub, Blink Eye). Identical worlds (same contents for every player) are always on, and the modifiers are part of the world fingerprint.

### Proximity voice (on hold)

Voice lived in the WKSR program, which is gone; the in-game WKSR has no voice yet. The server's relay is still there, and this is how it worked: WKSR recorded the microphone (NAudio, 48 kHz mono, 20 ms frames), encodes it with Opus (Concentus, 24 kbit/s) and sends it over UDP to the server's voice relay (`--voice-port`, default 47601; open it in the firewall, `deploy.sh` does that for ufw). Every packet is AES-GCM encrypted and authenticated with a key the player gets over the TLS connection (`VoiceHello`), with counters against replays. The server passes each frame on to the players who can hear the speaker: everyone in the same lobby, or in a match the players within `--voice-range` metres (default 65), by the positions their games report. Nothing is stored or logged.

WKSR plays the voices itself, each through a small jitter buffer. In a match a voice fades with distance (silent at the range) and is panned left or right by where that player is relative to where you look; in a lobby voices are plain. The voice settings window has: on/off, the microphone, mic volume with a level meter, voice volume, push to talk (any key or mouse button, read with `GetAsyncKeyState`, so it works in the game and anywhere else) or open mic (a noise gate). Push to talk is the default, since there is no echo cancellation. MUTE on a lobby row silences that player locally. The game shows a speaker symbol in the bottom left corner (lit while you talk) and over the head of whoever is talking (`VoiceActivity` from WKSR).

`WKRace.DevTools.exe bot ... --voice [hz]` makes a bot speak a test tone every 4 seconds through the real relay and print how many voice packets it hears.

### Chat

F8 (rebindable in OPTIONS) opens and closes the chat panel; Enter sends to the selected room, and tabs are clicked with the mouse. Rooms: **Global** (joined after connecting unless OPTIONS > CHAT is unticked) and **Match** (the lobby or 1v1, opens and closes with it). While typing, the game gets no input and held grips stay held. The server keeps each room's last 24 hours (300 lines) in memory only and sends recent lines to whoever joins; nothing is written to disk. 5 lines within 5 seconds silence a player for 5 seconds. `/mute name` and `/unmute name` hide a player's lines (kept in `client.json`).

## Steam lobbies

**STEAM LOBBIES** (next to CONNECT on the WKSR window's first page; `ClientSettings.SteamLobbies`, kept for the next start; CONNECT and STEAM LOBBIES exclude each other) lets players meet in **Steam lobbies** instead of on the official server. It is part of the same mod: the code lives in `src/WKRace.Mod/Steam/`, and `MainViewModel.SteamMode` switches the shared code at run time (`partial void ...Edition()` hooks in `MainViewModel.cs`, filled in by `Steam/Client/MainViewModel.Steam.cs`, each doing nothing outside Steam mode).

- **The host runs the server's core.** Opening a lobby starts `LobbyHost`: the official server's `ServerCore` (lobbies, matches, parties, bans, chat; the server files are listed one by one in `WKRace.Mod.csproj`), with in-memory stand-ins for storage and the ranked-only parts (`Steam/Host/HostStandIns.cs`), on a thread of its own. The host's own WKSR talks to it in memory (`LocalLink`); other players through Steam's relay (`SteamNetworkingSockets`, virtual port 47, `SteamPeer`, `SteamHostLink`), so nobody sees anyone's IP address. A player's identity on a host is derived from the Steam account the relay proves, never from what they send.
- **The Steam lobby** (`SteamMatchmaking`) is how players find the host: its data says `wksr`, a compatibility tag (`wksr_ver` = protocol version + `RaceGuarantees.RulesVersion`, also used as the build tag's mod part, so different mod versions share lobbies while neither changes), the code (the core's lobby id, shown as `WKR-XXXX-XXXX`), the host's Steam id and name, mode, modifiers, player count and whether it is racing. Public lobbies are Steam *public* lobbies and appear in the browser; private ones are Steam *invisible* lobbies, found by their code (Steam's own private type only takes invites). Accepting a Steam invite while the game runs switches to Steam lobbies and joins.
- **Leaving.** A player who drops is let back into their match by the core (same Steam account = same player). When the host leaves, the core closes the lobby for everyone ("The host left."), a match in progress ends without a result, Steam makes another member the owner, whose game starts a new host with the same mode and modifiers and writes itself into the lobby data; the others connect to it. Nobody taking over within 30 s ends the lobby.
- Nothing is ranked: no queue, ratings, leaderboards, Global chat, voice, mods list or reports. UNRANKED and SEGMENTS solo runs work as usual (segment runs unranked).
- **Testing alone:** `Debug/HostTcpPort` makes a hosted lobby also take players on that local TCP port, so the DevTools bot can join (`bot --server 127.0.0.1:<port> --lobby <code>`); `Debug/MenuAction = steam-lobby` walks it. The relay between two Steam accounts needs two PCs.

## Practice

**UNRANKED > SOLO** starts a practice run: the same match world, competitive rules and seed guarantees as a match, alone and on your PC only. No server is needed (it works offline) and ratings never change. Type a seed or leave it blank for a random one; after any match or run, **RUN THIS SEED** runs that seed again with the same artifact bans, so the world is exactly the one you played. (**RANKED > SOLO** is the server-hosted version for the leaderboard: a new seed on every run.)

Restarting works as in singleplayer: your restart bind (`bind rr restart`), the pause or death menu, or F9 twice all start a **fresh run** (on the same seed when one was typed, otherwise a new one), with the clock, the splits and your best's ghost back at the start (the bans are kept). There is no countdown: a practice run starts the moment the world has loaded. A run ends when you finish or die.

Your best run on each seed is kept in `%AppData%\WKRace\practice` (100 seeds at most): practice runs that finished or died, matches you finished, and both players' runs of a match that ended with a death. A finished run beats one that did not finish (the faster finish wins); between runs that died, the one that reached more levels wins, and on a tie the one that got to its last level sooner. Practising that seed again matches it: its splits fill the BEST column and the HUD, and its movement comes back as the ghost (up to where it died). A restarted run is not kept. F10 twice (or **END RUN** in WKSR) ends a practice run early. Practice runs show in the recent list and in Export Match Data, but not in the session's match count.

## Steam sign-in

On a server started with `--steam-public-url https://<its address>`, players are linked to their Steam account: the game reports the Steam ID it was launched with (`SteamIdentity`) and the server links it, or restores the player that Steam ID already has onto this PC. The older browser link (Steam's own sign-in page, back to `https://<address>/steam/return`, confirmed with Steam by the server) is still on the server but no client uses it.

- Ranked matches need a linked account (unless `--steam-optional`); lobbies do not.
- One player per Steam account. On a new PC (or after losing `client.json`), the player comes back by itself; the old PC's key stops working and that PC comes back the same way.
- The server stores only a keyed hash of the Steam ID (HMAC-SHA256 with a secret kept in the encrypted database).
- The sign-in page listens on 127.0.0.1 (`--steam-http-port`, default 47680) and is published through a reverse proxy with TLS. On the VPS, `deploy/enable-steam.sh <domain>` sets that up with nginx.

## Artifact bans

In the Habitation lab the game puts 2 of its artifacts on pedestals. Before the match world loads, each player bans one artifact so it cannot appear: the match seed decides who bans first, the second player sees that ban and then bans their own, and each turn has 20 seconds (running out passes without a ban). The list comes from the game itself, so artifacts added by game updates show up automatically. Ranked matches always have bans; in a 1v1 lobby the host decides.

In the game the ban screen takes number keys **1-9** (or Up/Down and Enter); **0** passes.

## Hotkeys

| Key | Action |
|---|---|
| F9 | Start your run from the menu, then ready up. In practice, press twice for a fresh run (restarts are off in matches). |
| F10 | Press twice to forfeit (or to end a practice run). |
| F7 / F6 | Toggle the split list / the ghosts. |
| F8 | Open / close the chat. |

Keys are changed in WKSR's OPTIONS. Everything else (HUD scale, ghost colour, split rows) is in `BepInEx/config/com.wkrace.mod.cfg`.

## Install (players)

**Thunderstore:** the package `WKSR-WKRace` is the plugin alone. A mod manager (Thunderstore Mod Manager, r2modman, Gale) installs it into `BepInEx\plugins\WKSR-WKRace\` of the profile, together with its dependencies BepInExPack, ImuiBepInEx and WKLib. Start the game from the mod manager ("Start modded"); WKSR is the button on the main menu. The package page is `tools/package/THUNDERSTORE-README.md`, its changelog `tools/package/CHANGELOG.md`, its icon `tools/package/icon.png`.

The player's identity is kept in `%AppData%\WKRace\client.json`.

> White Knuckle 0.61 runs on Unity 6.3, which ignores the usual `winhttp.dll` loader file in the game folder. Starting the game through Steam with the Doorstop switches works, and that is what the mod managers do. Launching `White Knuckle.exe` directly (or from Steam's library) will not load BepInEx.

## Host a server

Hosts get `WKRace-Server-<version>.zip`: a self-contained `WKRace.Server.exe` plus `start-server.bat` (fill in `PUBLIC_ADDRESS` there) and a `README.txt`. By hand:

```
WKRace.Server.exe --port 47600 --data ./data --public-address your-address:47600
```

Open TCP port 47600 on your router/firewall and give players `your-address:47600`. Players, ratings and finished matches are stored in one SQLite database, `data/wkrace.db`. Give the server a key (`--db-key-file <file>`, or on Linux `--db-key-credential <name>` with systemd's `LoadCredential`) and the whole file is encrypted (SQLite3 Multiple Ciphers, ChaCha20-Poly1305); without one it is plain SQLite. Every hour the server also writes `data/wkrace-snapshot.db`, a consistent encrypted copy for backups. Data from versions before 0.5.0 (`players.json`, `matches.jsonl`) is imported on the first start and the old files renamed to `*.imported-<date>`; delete them once checked. Run with `--help` for all options (connection limits, time limit, countdown, reconnect grace, TLS).

Traffic is plain TCP by default. Player identity is a random token stored on each player's machine, so a public server should use TLS (`--cert server.pfx --cert-password-env VAR`). The server checks the certificate file every minute and picks up a renewed one without a restart, so live matches are not dropped.

Abuse limits (all adjustable, see `--help`): at most 5 new players per address per day and 100000 in total; more than 60 connection attempts a minute from one address, or three kicks for flooding or bad data within ten minutes, earns a 10-minute ban; 10 lobby creates/joins per player per minute. Level names relayed between players lose `<` and `>` so they cannot inject rich text into the HUD.

**Linux VPS:** `dist/wkrace-server-linux-x64-<version>.tar.gz` contains the server and a `deploy/` kit. Upload it, extract it (`mkdir -p ~/wkrace-deploy && tar -xzf <file> -C ~/wkrace-deploy`) and run `sudo bash ~/wkrace-deploy/deploy/deploy.sh`: it backs up `/var/lib/wkrace`, installs a daily backup timer (`/var/backups/wkrace`, 30 kept), swaps the program folder (old one kept as `/opt/wkrace.prev`) and restarts the service.

The mod always connects to the built-in official server (its host name is in `src/WKRace.Mod/Client/OfficialServer.cs`, in plain text: Thunderstore does not take hidden code or data, and the name is public anyway through the privacy notice); it is not stored in `client.json`. To point a test build at another server, set `TestServer = host:port` (no TLS) under `[Debug]` in `com.wkrace.mod.cfg`, and `Profile = <name>` to use `client.<name>.json` instead of your own identity. The `--public-address` server option is only used by clients older than 0.3.0.

### Administration

Start the server with an admin key (`--admin-key-file <file>`, or `--admin-key-credential <name>` with systemd) and it takes commands on `127.0.0.1:47690` (`--admin-port`). Send them with the same program: `WKRace.Server admin --admin-key-file <file> <command>`. On the VPS, `deploy.sh` creates the key and a wrapper, so it is `sudo wkrace-admin <command>`:

| Command | |
|---|---|
| `status` | players online, queue, matches |
| `players [text]` | list players (name or id containing text) |
| `show <player>` | one player's details |
| `rename <player> <new name>` | rename and lock the name; the player reconnects with it and cannot change it back |
| `ban <player> [reason]` / `unban <player>` | refuse a player's sign-ins (a match in progress ends as a loss) |
| `ipban <ip> <minutes>` | refuse connections from an address for a while |
| `export <player>` | everything stored about a player, as JSON (data access requests) |
| `delete <player>` | erase a player; their matches stay in the opponents' history as "deleted player" |
| `merge <from> <into>` | fold one player (record, Steam link, matches) into another and delete `<from>` |

`<player>` is a name, a full id, or at least 6 characters of the id.

### Privacy notice

A server with Steam sign-in also serves its privacy notice at `https://<address>/privacy` (what is stored, why, where, for how long, and players' rights); WKSR's OPTIONS > PRIVACY NOTICE opens it. Set the contact it shows with `--privacy-contact "<email or Discord>"`. On the VPS, rerun `deploy/enable-steam.sh <domain>` once to publish the page (and `/report`) through nginx.

### Bug reports and email

Players report bugs on Discord; the in-game WKSR has no report form. The server still takes reports (the `BugReport` format, which the old WKSR program's REPORT BUG form posted over HTTPS to `https://<address>/report`). The server only takes reports from known players with their key (5 per player per day, 200 per day in all, `--no-reports` turns them off) and keeps each one in `<data>/reports` until it is emailed.

On the VPS, `wkrace-mail` sends them (every 2 minutes) and the health check's alerts. Set it up once with `sudo wkrace-mail setup`: it asks for the address that sends (any provider with SMTP; the server and port are suggested for Gmail, Outlook, Yahoo, iCloud, GMX, Zoho and others), its password, usually an app password made for this in the account's security settings (the typing is not shown, and it is stored in `/etc/wkrace/mail.conf`, root only), and the address to send to, which can be a different one. `sudo wkrace-mail test` sends a test email. Big reports arrive as several emails, so each stays under the usual 25 MB limit. A report email's Reply-To is the player's contact when that is an email address.

### Monitoring

`deploy.sh` also installs `wkrace-monitor`, run every 5 minutes: match server running and listening, the sign-in page, nginx if installed, any other systemd units listed one per line in `/etc/wkrace/monitor-services`, disk use (85%), TLS certificate expiry (14 days), the daily backup's age and bug reports not emailed within an hour. Alerts go by email once `wkrace-mail` is set up, and/or to a Discord webhook URL or an ntfy topic URL put in `/etc/wkrace/alert-url` (`sudo chmod 600` it). It alerts when something breaks or recovers and every 6 hours while it stays broken. Run `sudo wkrace-monitor` to see the current result. A check from outside (any free uptime service doing a TCP check of port 47600) also catches the whole VPS going down.

## Try it alone

The bot is a fake opponent that queues on your server. Start the server and the bot in two terminals, set `TestServer = 127.0.0.1:47600` and `Profile = test` under `[Debug]` in the mod's config, then start the game and press **RANKED > 1V1** in WKSR (`tools/race-the-bot.bat` starts both on port 47601):

```
dist\server\WKRace.Server.exe
dist\devtools\WKRace.DevTools.exe bot --server 127.0.0.1:47600 --name Pacer --speed 0.7
```

`Profile = test` keeps the test identity in `client.test.json`, apart from your real one. Clear both entries afterwards.

For party matches, one bot hosts and the others join with its code (printed in its output); the host starts once `--start-at` players are in, and bots in a lobby stay for rematches:

```
dist\devtools\WKRace.DevTools.exe bot --server 127.0.0.1:47600 --name Host --host-party --start-at 4
dist\devtools\WKRace.DevTools.exe bot --server 127.0.0.1:47600 --name Bee --lobby WKR-XXXX-XXXX --die-at 20
dist\devtools\WKRace.DevTools.exe bot --server 127.0.0.1:47600 --name Cee --lobby WKR-XXXX-XXXX --afk
```

`--die-at s` dies at that match time, `--afk` never readies up or climbs, `--say text` sends one chat line.

The bot climbs at `--speed` metres per second, posts a split every `--split-every` metres (default 45) and finishes after `--finish-after` seconds (default 900), so it wins if you take longer. Add `--loop` to make it queue again after every match.

## Fairness and anti-cheat

- Competitive rules are forced for the match run, and every other campaign modifier (hard mode, iron knuckle, ...) is off.
- Players are only matched (queue and lobbies) when they run the same White Knuckle build and the same WK Race mod version.
- Level layout is identical for both players: the game re-rolls level mirroring from a shared random stream, so the plugin reseeds it per level. Both clients compare a fingerprint of the generated world and warn on a mismatch.
- Level contents are identical too: items, spawn chances, vending machines and which creatures spawn are rolled from the match seed, the level and the object's place in it, instead of from the game's shared random stream (which depends on how each player has played so far).
- Spawn tables and perk offers treat every player as having everything unlocked, so a new player and a veteran see the same pools.
- Guaranteed match essentials (identical for both players, picked by the match seed):
  - every Deep Storage level with food-bar or pill-bottle spawn points has at least one food bar and one pill bottle;
  - the Safe Room's Experimental Perks console offers Conditioned Polyphagia or Metabolic Overdrive;
  - the Silo-to-Pipeworks interlude console offers Metabolic Stasis, and that interlude always has a slug grub;
  - the Chute-to-Habitation, Abyss-to-Nest and Diver-to-Nest interlude consoles offer Rabbit DNA or Pulse Organ;
  - a vending machine in the Safe Room or the Silo-to-Pipeworks interlude sells a Blink Eye.
- Perk consoles the game leaves unseeded (such as the Safe Room's Experimental console) are seeded from the match seed, so both players see the same cards.

The rules live at the top of `src/WKRace.Mod/RaceGuarantees.cs`.
- Console cheats are disabled for the Campaign during a match, and any use of them disqualifies the run.
- Restarts are off during a match and a death ends it, so nobody can re-roll a bad start or a mistake.
- The server stamps every finish with its own clock; a client cannot claim a faster time than the server allows.
- With `--min-finish-seconds` and `--min-finish-splits` (the VPS uses 180 and 15), a finish faster than that, or after fewer level splits, is disqualified: the opponent wins and the server logs it.
- Your saved campaign run is never touched: saving and deleting are blocked while you are in the match world.

Not synchronised: what creatures do once spawned (they react to each player), anything that depends on a player's own run so far (items picked up, perks taken, distance climbed), and anything else the game rolls while you play rather than when a level loads.

## Build from source

Requires the .NET 10 SDK and a White Knuckle install (the plugin compiles against the game's assemblies, which are not in this repository). The server, tests and tools target .NET 10 (LTS); the plugin and the shared protocol stay on netstandard2.1 for the game.

1. Copy `local.props.example` to `local.props` and set `GameDir` (and optionally `PluginInstallDir` to copy the plugin into a BepInEx profile after each build).
2. `powershell -ExecutionPolicy Bypass -File tools/fetch-libs.ps1` downloads ImuiBepInEx and WKLib (the mods the in-game menu is drawn with) from Thunderstore into `lib/`. The plugin compiles against them; they are not in the repository and not shipped (the mod manager installs them for players). `build.ps1` does this by itself.
3. `dotnet test tests/WKRace.Tests` runs the protocol, server and TCP tests.
4. `powershell -ExecutionPolicy Bypass -File tools/build.ps1` builds everything into `dist/`: the Thunderstore zip, the host zip and the Linux server tarball (`-Out <folder>` builds elsewhere without wiping `dist/`). It stops if `Plugin.cs` and `Directory.Build.props` disagree on the version.

Debug builds of the plugin add `Debug > AutoAdvance` (teleports through levels so splits can be tested) and `AutoScreenshots` is available in every build.

## Troubleshooting

- **No WKSR button on the main menu**: BepInEx (or ImuiBepInEx / WKLib) did not load. Start the game from the mod manager, not from `White Knuckle.exe`.
- **Nothing happens when a match is found**: the game must reach the main menu; the run starts there. Press F9 if auto-start is disabled.
- **`Patch target missing` in `BepInEx/LogOutput.log`**: a game update renamed something the plugin hooks.

## License and credits

MIT, see `LICENSE`.

WK Race is a mod for [White Knuckle](https://store.steampowered.com/app/3195790/) by Dark Machine; none of the game's code or assets are in this repository. It runs on [BepInEx](https://github.com/BepInEx/BepInEx), and the in-game menu is drawn with [ImuiBepInEx](https://thunderstore.io/c/white-knuckle/p/prideunique/ImuiBepInEx/) and [WKLib](https://thunderstore.io/c/white-knuckle/p/MonkSilly/WKLib/), installed separately as dependencies. Steam lobbies use Facepunch.Steamworks as shipped with the game. The server uses Microsoft.Data.Sqlite with SQLite3 Multiple Ciphers.

The code was written with an AI assistant (Claude, by Anthropic), directed and play-tested by a human.
