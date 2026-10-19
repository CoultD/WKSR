using Imui.Controls;
using Imui.Core;
using WKRace.Client;

namespace WKRace.Mod
{
    // STEAM LOBBIES in the WKSR window: the header and the first page. There is no match server, so no RANKED,
    // LEADERBOARDS or Global chat; lobbies are hosted by the players themselves.
    internal sealed partial class WksrWindow
    {
        private static void SteamHeader(ImGui gui, MainViewModel vm)
        {
            gui.Text(vm.PlayerName + "     STEAM LOBBIES");
            gui.Text(vm.SteamLobbyStatus, vm.HasLobby ? Gold : Dim);
            if (EmbeddedClient.StartProblem != null) gui.Text(EmbeddedClient.StartProblem, Red, true);
            gui.Separator();
        }

        private void SteamMain(ImGui gui)
        {
            Title(gui, "STEAM LOBBIES", "LOBBY is for playing with others without the match server: open a lobby and you host it in your game, or join one from the lobby browser, with a code or a Steam invite. UNRANKED is solo, with your own rules. SEGMENTS is a solo run of one part of the climb. Nothing is ranked here.");
            float half = (gui.GetLayoutWidth() - Gap - 8f) / 2f;
            gui.BeginHorizontal();
            if (gui.Button("LOBBY", new ImSize(half, 120f))) _page = Page.Lobby;
            gui.AddSpacing(Gap);
            if (gui.Button("UNRANKED", new ImSize(half, 120f))) _page = Page.Unranked;
            gui.EndHorizontal();
            gui.AddSpacing(Gap);
            gui.BeginHorizontal();
            if (gui.Button("SEGMENTS", new ImSize(half, 56f))) _page = Page.Segments;
            gui.AddSpacing(Gap);
            if (gui.Button("OPTIONS", new ImSize(half, 56f))) Show(Page.Options);
            gui.EndHorizontal();
            gui.AddSpacing(12f);
            gui.Text(OtherModsNote, Gold, true);
        }
    }
}
