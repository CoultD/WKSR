using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Chat rooms. Match: a line goes to everyone in the sender's match, or else in their lobby. Global: to every
    // connected player who has the room open. Lines of the last 24 hours are kept in memory (the Global room's, and
    // each lobby's while it is open), so that a player who joins can read back; they are never written to disk or
    // to the log, and a restart of the server forgets them.
    public sealed partial class ServerCore
    {
        // The one automatic rule of the chat: ChatLinesPer5Seconds lines within the window silence the player for
        // ChatSilenceMs. Trying to send while silenced does not make it longer.
        private const long ChatWindowMs = 5000;
        private const int ChatSilenceMs = 5000;
        private const long ChatKeepMs = 24L * 60 * 60 * 1000;
        private const int ChatKeepLines = 300;
        // How many of a room's kept lines a player gets on joining.
        private const int ChatHistoryLines = 150;

        private readonly List<ChatMessage> _globalChat = new List<ChatMessage>();

        public int GlobalChatLineCount { get { return _globalChat.Count; } }

        private void HandleChat(Player player, ChatSend chat)
        {
            string text = CleanChat(chat.Text);
            if (text.Length == 0) return;

            bool global = chat.Room == ChatRooms.Global;
            IEnumerable<Player> to;
            List<ChatMessage> kept = null;
            if (global)
            {
                if (!player.InGlobalChat)
                {
                    player.Send(new ChatMessage { System = true, Room = ChatRooms.Global, Text = "You are not in the Global chat: add it with the + next to the chat tabs." });
                    return;
                }
                to = _byConnection.Values.Where(p => p.InGlobalChat).ToList();
                kept = _globalChat;
            }
            else if (player.Match != null)
            {
                to = player.Match.Slots.Select(s => s.Player);
                if (player.Lobby != null) kept = player.Lobby.Chat;
            }
            else if (player.Lobby != null)
            {
                to = player.Lobby.Members;
                kept = player.Lobby.Chat;
            }
            else
            {
                player.Send(new ChatMessage { System = true, Text = "The Match chat works in lobbies and matches: there is nobody to talk to right now." });
                return;
            }

            long now = _now();
            int limit = _options.ChatLinesPer5Seconds;
            bool silence = false;
            if (limit > 0)
            {
                if (now < player.ChatSilencedUntilMs)
                {
                    // Not sent, and the silence stays as long as it was: the player is told how much is left.
                    int left = (int)(player.ChatSilencedUntilMs - now);
                    int seconds = (left + 999) / 1000;
                    player.Send(new ChatMessage { System = true, Room = chat.Room, SilencedMs = left, Text = "Silenced for " + seconds + (seconds == 1 ? " more second." : " more seconds.") });
                    return;
                }
                Queue<long> times = player.ChatTimes;
                while (times.Count > 0 && times.Peek() <= now - ChatWindowMs) times.Dequeue();
                times.Enqueue(now);
                // The line that reaches the limit is still sent; the silence starts with it.
                silence = times.Count >= limit;
            }

            var line = new ChatMessage { From = player.Record.Name, Text = text, Room = global ? ChatRooms.Global : ChatRooms.Match, SentUnixMs = now };
            if (kept != null)
            {
                kept.Add(line);
                PruneChat(kept, now);
            }
            foreach (Player p in to) p.Send(line);
            if (silence)
            {
                player.ChatTimes.Clear();
                player.ChatSilencedUntilMs = now + ChatSilenceMs;
                player.Send(new ChatMessage { System = true, Room = line.Room, SilencedMs = ChatSilenceMs, Text = "Slow down.. Silenced for " + ChatSilenceMs / 1000 + " seconds" });
            }
        }

        // Joining the Global room brings its lines of the last 24 hours. (The Match room needs no asking: it is the
        // player's lobby or match.)
        private void HandleChatJoin(Player player, ChatJoin join)
        {
            if (join.Room != ChatRooms.Global) return;
            if (!join.Join)
            {
                player.InGlobalChat = false;
                return;
            }
            if (player.InGlobalChat) return;
            player.InGlobalChat = true;
            SendChatHistory(player, _globalChat);
        }

        private void SendChatHistory(Player player, List<ChatMessage> lines)
        {
            PruneChat(lines, _now());
            for (int i = System.Math.Max(0, lines.Count - ChatHistoryLines); i < lines.Count; i++)
            {
                ChatMessage l = lines[i];
                player.Send(new ChatMessage { From = l.From, Text = l.Text, System = l.System, Room = l.Room, SentUnixMs = l.SentUnixMs, History = true });
            }
        }

        private static void PruneChat(List<ChatMessage> lines, long now)
        {
            int drop = 0;
            while (drop < lines.Count && (lines.Count - drop > ChatKeepLines || lines[drop].SentUnixMs <= now - ChatKeepMs)) drop++;
            if (drop > 0) lines.RemoveRange(0, drop);
        }

        private void SystemChat(Match match, string text)
        {
            SystemChat(match.Slots.Select(s => s.Player), text);
        }

        private static void SystemChat(IEnumerable<Player> to, string text)
        {
            var line = new ChatMessage { System = true, Text = text };
            foreach (Player p in to) p.Send(line);
        }

        // Printable characters only, single spaces, at most MaxChatLength. Both the HUD and WKSR show it as plain text.
        internal static string CleanChat(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            bool space = true;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!space) sb.Append(' ');
                    space = true;
                    continue;
                }
                if (char.IsControl(c) || char.IsSurrogate(c) || c == '​' || c == '‮' || c == '‭') continue;
                sb.Append(c);
                space = false;
                if (sb.Length >= ProtocolConstants.MaxChatLength) break;
            }
            return sb.ToString().Trim();
        }
    }
}
