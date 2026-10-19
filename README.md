# WK Race (WKSR)

Speedrun matches for [White Knuckle](https://store.steampowered.com/app/3195790/): everyone climbs the same seeded world, with live splits and ghosts of the other players. It is played from a **WKSR** button in the game's main menu.

- **Ranked:** 1v1 matches with ratings, and solo runs, with leaderboards.
- **Lobbies:** 1v1 or party matches of up to 15, on the official server or in Steam lobbies hosted by the players.
- **Unranked and segment runs:** solo runs with your own rules and seeds.

Install it from Thunderstore: [WKSR-WKRace](https://thunderstore.io/c/white-knuckle/p/WKSR/WKRace/).

## Repository

| Folder | What it is |
|---|---|
| `src/WKRace.Mod` | The BepInEx plugin: WKSR's menu, the HUD, ghosts, seeded worlds, and the Steam lobby host |
| `src/WKRace.Server` | The official match server: matchmaking, ratings, leaderboards |
| `src/WKRace.Protocol` | The messages the plugin and the server exchange |
| `src/WKRace.DevTools` | A bot opponent for testing |
| `tests` | Automated tests |
| `tools` | Build script, packaging, VPS deploy kit |

## Build

Needs the .NET 10 SDK and White Knuckle installed. Copy `local.props.example` to `local.props`, set `GameDir`, then:

```
powershell -ExecutionPolicy Bypass -File tools/build.ps1
```

This downloads the two mods the menu needs, runs the tests and puts the packages in `dist/`. How everything works is in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## License

MIT, see [LICENSE](LICENSE). Not affiliated with Dark Machine; none of the game's files are included. Written with an AI assistant (Claude, by Anthropic), directed and play-tested by a human.
