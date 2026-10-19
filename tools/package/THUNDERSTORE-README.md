# WKSR: White Knuckle SpeedRun

Race other people up the same White Knuckle climb. Everyone gets the exact same seeded world, you see each other's splits as you go, and the other racers climb next to you as ghosts.

Players cant interact with each other with this mod, during a match each game sends coordinates where their character is to the server, that way you can see each other as ghosts.

It's made with fun in mind and there's a solo q mode for those who want it and an offline queue.

You can launch the game via r2/thunderstore.

Not all mods are compatible with this mod (anything relating to added levels, changes in world building, new items, physics etc.), cosmetic mods should work, if they dont work dm me.

As the disclamer at the bottom suggests this was 100% made with AI, but there is some modicum of care put in to not make it ultraslop, especially for user security.

Join the discord for discussions about features, potential changes, bug reports, etc. https://discord.gg/AZpr9XPTsD or add me to send dms .coult

## Planned

- Feature to allow players to connect to steam relay instead of the dedicated server the game runs currently, it will allow steam invites/lobbies etc but not custom leaderboards due to its limitations.
- Better livesplit implementation (custom entries, imports, style editing).
- More gamemodes for bigger lobbies.
- Customizable models.
- Open source.

## What it do

- **Ranked races:** 1v1 against whoever is searching, with a rating.
- **SOLO ranked:** You can use this mod to have run solo runs with the better seed, perk, item RNG while still being applicable for this mods leaderboards.
- **SOLO offline:** Play offline without connecting to anything, allows turning off/on existing modifiers.
- **Lobbies:** send your friends a code. Race them 1v1, or in a **party mode** of up to 15 players. The host picks the modifiers for everyone.
- **NEW Segemented runs:** Silos has a segmented mode, reach the end of silos by pressing the close shaft button in the safe room. Has leaderboards. Currently WIP (possibly daily/weekly seeds planned)
- **Practice:** solo offline runs on any seed, with the modifiers you choose. Your best run on each seed comes back as a ghost to beat.
- **Chat:** in lobbies and races: press F8 or whatever u change it to in the game to access it. **NEW** has a global chat when connected to the server.
- **WKSR:**, The client is no more, the mod is inbuilt in game using ImuiBepinEx and WKLib.
- **Leaderboard:** 1v1, solo q leaderboards available
- **Proximity voice:** removed for now.
- **Approved/unapproved mods:** might come a time where some mods would need to get unapproved for leaderboards later down the line.
- **Auto unlocks competitive mode:** the mod uses comp mode as the main gamemode, so it will auto unlock it for you while using it.
- **The Hunted flare**: Flare no longer has rng where it spawns in your inventory when you load in.


## Installation / launching first time

1. Install this package with your mod manager. BepInExPack and other mods come with it.
2. Press **Start modded**.

## Starting a run

Press F9 on the game's menu to load the race world. Once everyone's world has loaded, there's a ready check: press **F9** again (or **READY** in WKSR) within 60 seconds. The countdown starts when everyone is ready.

If you don't ready up in time: in a ranked sesh you forfeit, a friendly 1v1 is called off, and a party race starts without waiting for you.

## The rules
		
- One run each. No restarts during a race.
- **1v1:** first to the top wins. **If you die, you lose**, even if you were miles ahead. If someone's game or connection drops, the race pauses for up to 60 seconds.
- **Party run:** one life. Die and you're out, and the run carries on without you. Nobody waits for anyone who drops out. The race ends when everyone has finished or is out, or 5 minutes after the first finish (subject to change if intrusive). Finishers are placed by time. Everyone else is placed by how far they got, and on different routes, by who survived longer. On a shortcut your place shows as "?" until you're back on the main route.

## Modifiers

Ranked races always use all of them. In lobbies the host picks them, and in practice you do.

- **Artifact bans** (1v1 only): each player bans one artifact before the race. **NOTE** in solo you will be prompted to ban the artifacts once per session. Restart the game and launch a fresh sesh to ban something else.
- **Curated seeds**, so early-game luck doesn't decide the race. Each part can be picked on its own:
  - **Deep Storage food:** every Deep Storage level that can have them gets at least one food bar and one pill bottle. **Update** Deep storage's first tile will include a pill or a candy bar.
  - **Perks:** the Safe Room's experimental console offers Conditioned Polyphagia or Metabolic Overdrive, the Silo to Pipeworks interlude offers Metabolic Stasis, and the interlude consoles before the Habitation and the Nest offer Rabbit DNA or Pulse Organ.
  - **Items:** the Silo to Pipeworks interlude always has a slug grub, and a Blink Eye is for sale in the Safe Room or that interlude.

Whatever the modifiers, every racer gets identical levels, items, spawns and perk cards, and everyone counts as having everything unlocked.

## Keys

You can change these in game.

| | |
|---|---|
| F9 | Start your run, then ready up (in practice: twice for a fresh run) |
| F10 | Twice to forfeit |
| F7 / F6 | Hide the splits / the ghosts |
| F8 | Chat |

WKSR's **GHOST OPACITY** slider sets how solid the ghosts look.

## Something wrong?

Send me a dm .coult or join the discord with the link above.

You can use this for feature requests, if you have any.

## Privacy

WKSR connects to our server in the EU. It keeps your racer name, rating and 1v1 race results, and nothing is sold or shared. Chat is passed on to the other players and is stored for 24h (temporary, can be changed). Privacy button in WKSR > options shows exactly what is stored, and how to get a copy or have it deleted.

## Made with AI

This mod and WKSR were built with an AI assistant: the code was generated with Claude, by Anthropic, directed and play-tested by a human. It has been tested in real races and practice runs, and backed by over 200 automated tests. If you find something that slipped through, REPORT BUG is the fastest way to tell us.

Most of the current features can be changed/added if people want, not a lot here is set in stone.

Thanks to:

-Aricin
-Spilled
-AgeCube
-Nicole
-chimitto

For testing and feature discussing!