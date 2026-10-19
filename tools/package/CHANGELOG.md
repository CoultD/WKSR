# Changelog

## 0.9.6

- **STEAM LOBBIES: play without the match server.** On the WKSR window's first page, next to CONNECT, STEAM LOBBIES lets you play with others in lobbies the players host themselves through Steam. Open a lobby and your game hosts it; others join from the lobby browser, with the code or with a Steam invite. 1v1 and party lobbies (up to 15) with the same modifiers, bans, ready check, standings and lobby chat as on the match server. Players connect through Steam's relay, so nobody sees anyone's IP address, and nothing is sent to the match server. Nothing is ranked there: no ratings, leaderboards or Global chat. If the host leaves, a match in progress ends without a result and another player in the lobby becomes the host. LEAVE STEAM LOBBIES (or CONNECT) goes back; the choice is kept for the next start. Steam lobbies stay open to players on the version before as long as an update does not change the match rules.
- **Lobby browser.** LOBBY, BROWSE LOBBIES lists the public lobbies of players on your version: host, 1v1 or party, modifiers, how many are in and whether they are in a match. JOIN gets you in, no code needed. When you open a lobby, PUBLIC (ticked to begin with) puts it in the browser; untick it for a lobby that is only joined with its code. The match server needs its 0.9.6 update for the browser; until then the page says so.

## 0.9.5

Everyone needs 0.9.5: it can't play with 0.9.4.

- **SEGMENTS: a new mode.** A solo run of one part of the climb, where the clock stops at the end of the segment. The first one is **SILOS**: from the start of the game to the button that seals the shaft at the end of the Silos (the one that unlocks the gate to the elevator). Connected, with the standard modifiers, every run is on a new seed and your fastest time goes on the segment's own leaderboard (LEADERBOARDS, SEGMENTS, then pick the segment). You can change the modifiers or type a seed: the page then says the run is unranked and does not count for the leaderboard. Offline, segment runs are always unranked. More segments will follow.
- **Chat rooms.** The chat is now one panel for the whole game, with a tab for each room you are in. **Global** is everyone who is connected; **Match** is your lobby or 1v1 and opens and closes with it (a lobby keeps its chat from run to run). **F8** opens the panel and closes it again (change the key under OPTIONS; it was T before, and a key that types a letter is moved to F8). The panel shows the room's lines of the last 24 hours, and Enter sends. A line can be 500 characters long, and the input line counts down what is left. Tabs are picked with the mouse, in the menus or while paused. The x on a tab leaves that room, and the + lists the rooms to join or leave.
- **Chat's one automatic rule:** five lines within five seconds silence you for five seconds ("Slow down.. Silenced for 5 seconds"). The input line counts the seconds down, and what you typed stays in the box until you can send again. Trying to send while silenced does not make it longer.
- **With the panel closed**, the bottom left corner shows the newest line of the room you have selected.
- **OPTIONS, CHAT:** you join Global when you connect; untick JOIN THE GLOBAL CHAT WHEN I CONNECT to stop that.
- **The lobby page has no chat box of its own any more:** the lobby talks in the Match room.
- **IN QUEUE:** under the number of players online, the WKSR window shows how many players are searching for a ranked 1v1.
- **Fixed:** typing in a text box of the WKSR window also typed in the game's chat (any word with a "t" opened it), and Enter there pressed the highlighted button of the main menu.
- **Fixed: the overlay and the WKSR window showing through each other.** When a match or run begins the WKSR window closes, so the artifact bans have the screen to themselves, and whenever the window is open it is drawn over the overlay.
- **The result panel** (VICTORY, FINISHED and so on) is now at the top of the screen, narrower and darker, so it no longer covers the game's own end screen.
- **When your character does not start:** if another mod breaks the start of your character on a new version of the game (you spawn but cannot move), a red notice at the top of the screen now says so, names the mod when it can, and tells you to try without it.
- **Privacy, IP addresses:** the match server's log now holds only a shortened IP address (203.0.113.x), and the privacy notice has its own section on what happens with your IP address. It is not stored with your player, and other players never see it.
- **Privacy:** so that players can read back, the match server keeps chat lines of the last 24 hours in its memory (Global, and a lobby's while it is open). They are not written to disk or logs.

## 0.9.4

Update to keep playing with others: 0.9.4 is not matched with 0.9.3.

- **CONNECT and DISCONNECT:** the game only connects to the match server after you press CONNECT in the WKSR window, and it remembers that for the next start. Until then the window has UNRANKED only: solo runs work offline. DISCONNECT takes you offline again.
- **No first-start page any more.** What the match server keeps is in the readme, and under OPTIONS, PRIVACY NOTICE.
- **Escape in a run** closes the WKSR window together with the pause menu.
- **Timer check:** the check on a timer that stands still while you move is switched off for now.
- **The mod's options** (WKLib's mod list) no longer show the old WKSR program's settings or the test settings, and the mouse wheel scrolls faster there.

## 0.9.3

Everyone needs 0.9.3: it can't play with 0.9.1 or 0.9.2.

- **WKSR is now inside the game.** There is no separate WKSR program any more. A **WKSR** button on the side of the game's main menu opens RANKED, LOBBY, UNRANKED, LEADERBOARDS and OPTIONS, each with a BACK button. The same button is in the pause menu of a run, with the match, its splits and FORFEIT. Start the game from your mod manager as before. The package now installs ImuiBepInEx and WKLib, which draw the menu.
- **Your player name is your Steam name**, and your Steam account links by itself when the game runs through Steam: there is no LINK STEAM any more. If another player already has your name, yours gets a number. Your rating, record and times stay yours.
- **First start:** the WKSR window first shows what the match server keeps and what other players see. The game connects to the match server after OK.
- **The game and its public test release:** players on either one can be matched and can share a lobby.
- **Ranked solo:** dying and restarting in the game stays a ranked solo run, on a new seed. Before, it turned into an unranked run for the rest of the session.
- **Timer check:** a finished run is no longer refused when the check thinks the timer stood still. The time counts, and the run is marked for us to look at.
- **OPTIONS** has the game's hotkeys, ghost opacity, EXPORT MATCH DATA and the privacy notice.
- **Not in this version:** proximity voice chat, REPORT BUG, the mods window and the desktop shortcut. Bug reports go to the Discord (EXPORT MATCH DATA makes a file to send along).

## 0.9.2

- **Added a readme line**

## 0.9.1

Everyone needs 0.9.1: it can't play with 0.9.0.

- **New WKSR layout:** RANKED (1v1 and solo), LOBBY (create or join) and UNRANKED (solo with your own modifiers and seed). The options behind the gear hold voice chat, the game's hotkeys, ghost opacity, export match data, report bug, and CREATE SHORTCUT, which puts WKSR on your desktop so you can open it without starting the game first.
- **Ranked solo:** a solo run hosted by the match server, with the ranked modifiers. The server picks the seed, and every restart in the game is a new run on a new seed.
- **Unranked solo:** with a seed typed in, a restart runs that seed again; with no seed, every restart is a new random seed.
- **Leaderboards** (the trophy): ranked 1v1 by rating and by fastest time, and ranked solo by fastest time. One row per player, their best, with the version it was set on. Search for a name, or FIND ME. The boards start empty with 0.9.1: you are listed once you play ranked on it.
- **Replays:** the 20 fastest runs of a time board can be run against as a ghost (PLAY AGAINST). After a run, SAVE REPLAY keeps its replay on your PC; UNRANKED, SAVED REPLAYS lists them with their seed and modifiers, to run against.
- **Mods list:** the game tells WKSR which other mods are loaded, and the match server checks them against its list of approved mods. Every mod is approved to start with. Once a mod is marked as not approved, a game with it gets no ranked 1v1 and no ranked solo; lobbies and unranked solo runs need ALLOW UNAPPROVED MODS ticked, and stay unranked. WKSR names the mods concerned.
- **Times follow the game's own timer.** The time in WKSR and on the overlay is the game's timer, to the hundredth: shortcut sections count three times over, as in the game. A 1v1 is still won by whoever finishes first; a party ranks finishers by their timer. Solo, pausing the game stops the clock; in a match it keeps running.
- **Competitive mode** counts as unlocked while the mod is loaded, so every player can set up the competitive options.
- **A closed or crashed game ends the run** in WKSR (in a match, that is a forfeit).
- **Hotkeys** for start/ready, forfeit, splits, ghosts and chat can be changed in WKSR's options.
- **Players online** is live, and WKSR says when a newer version is out. If the match server can't be reached, it asks Thunderstore for the newest version number.
- **LAUNCH GAME** works after the game opened WKSR. (Steam kept counting the game as running while that WKSR was open.)
- **Performance:** the in-game overlay and the level seeding do far less work each frame.
- **The flare gun** of the "hunted" trinket always starts on the same place in the inventory, just right of the middle.
- **Deep Storage food** now also makes the first Deep Storage level one with a food bar or pill bottle, and keeps the secret room open.
- **Perks:** the Chute to Habitation interlude and the Hadal Diver room offer both Rabbit DNA and Pulse Organ. A refresh no longer brings back the guaranteed perk it just showed.

## 0.9.0

Party matches, proximity voice chat, modifiers, a ready check and text chat. Everyone needs 0.9.0: it can't play with 0.7.

- **Party matches:** lobbies of up to 15 players, started by the host. One life and no restarts: die and you're out. The match ends when everyone is done, or 5 minutes after the first finish. In-game position (Mario Kart style, "?" on a shortcut), gaps at each split, and a ghost for every player.
- **Proximity voice chat:** talk to your lobby, and in a match to the players within 65 m, who hear you from where you are. Push to talk (any key or mouse button) or open mic. WKSR's voice settings pick the microphone and both volumes, and you can mute anyone. A speaker symbol shows over whoever is talking. Voice is never recorded.
- **Lobbies** are rooms now: they stay open after a match for a rematch, and the next player takes over if the host leaves.
- **Modifiers:** CREATE LOBBY picks 1v1 or party, artifact bans (1v1) and curated seeds (Deep Storage food, perks, items, separately). SOLO has its own picker. Ranked always uses all of them.
- **Ready check** in every match: press F9 or READY within 60 seconds once everyone has loaded.
- **Chat** in lobbies and matches: T in the game, or the chat box in WKSR. /mute and /unmute hide someone.
- **Deaths** are announced with the game's own death lines ("FUSED INTO THE MASS").
- **Ghost opacity** slider in WKSR.

## 0.7.4

First release on Thunderstore.

- The README lists what every match world guarantees (fair seeds).

## 0.7.2

- WKSR, the client, is included and opens with the game.
- WKSR never starts the game by itself; LAUNCH GAME starts it modded the way mod managers do, with the profile that has the mod.
- First start: WKSR asks for a player name, starting from a random one.
- Matches: one run each. Restarts are off, and a death ends the match (the survivor wins).
- Practice: solo runs on any seed, no countdown, restarts as in singleplayer, and your best run on each seed as a ghost.
- Ranked matchmaking with provisional ratings, friendly lobbies with join codes, artifact bans, a linked Steam account for ranked.
- REPORT BUG sends bug reports with screenshots from WKSR.
