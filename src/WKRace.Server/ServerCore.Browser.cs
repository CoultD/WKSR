using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // The lobby browser: hosts can list their lobby, and anyone can see the listed lobbies and join one. Clients from
    // BrowserVersion on are told about it (ServerFeatures); older ones never see the new messages.
    public sealed partial class ServerCore
    {
        // The first WKSR version that knows the lobby browser's messages.
        private const string BrowserVersion = "0.9.6";
        // A player may ask for the list this often.
        private const int LobbyListIntervalMs = 1500;

        private void SendFeatures(Player player)
        {
            if (!BuildTag.AtLeast(player.ClientVersion, BrowserVersion)) return;
            player.Send(new ServerFeatures { LobbyBrowser = true });
        }

        // Only the host lists or unlists their lobby.
        private void HandleLobbyVisibility(Player player, LobbyVisibility visibility)
        {
            Lobby lobby = player.Lobby;
            if (lobby == null || lobby.Host != player || lobby.Public == visibility.Public) return;
            lobby.Public = visibility.Public;
            _log(player.Record.Name + (lobby.Public ? " listed their lobby in the browser" : " took their lobby out of the browser"));
        }

        private void HandleLobbyListRequest(Player player)
        {
            long now = _now();
            if (now - player.LastLobbyListMs < LobbyListIntervalMs) return;
            player.LastLobbyListMs = now;
            List<Lobby> listed = _lobbies.Values
                .Where(l => l.Public && l.Host.Conn != null && l.Members.Count > 0)
                // Lobbies that can be joined right now first, then the fuller ones.
                .OrderBy(l => l.Match != null || l.Members.Count >= l.MaxPlayers ? 1 : 0)
                .ThenByDescending(l => l.Members.Count)
                .ThenBy(l => l.CreatedMs)
                .ToList();
            var list = new LobbyList { Cut = listed.Count > LobbyList.MaxEntries };
            foreach (Lobby l in listed.Take(LobbyList.MaxEntries))
            {
                list.Entries.Add(new LobbyListEntry
                {
                    LobbyId = l.Id,
                    Host = l.Host.Record.Name,
                    Mode = l.Mode,
                    Rules = l.Rules,
                    Members = (byte)l.Members.Count,
                    MaxPlayers = (byte)l.MaxPlayers,
                    Racing = l.Match != null,
                });
            }
            player.Send(list);
        }
    }
}
