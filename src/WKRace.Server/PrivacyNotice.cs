using System.Net;

namespace WKRace.Server
{
    // The privacy page (/privacy, next to the Steam sign-in page). It describes exactly what this server stores; keep
    // it in step with the code when that changes.
    internal static class PrivacyNotice
    {
        public static string Html(string contact, bool steamRequiredForRanked)
        {
            string who = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(contact) ? "the operator of this server" : contact.Trim());
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                   "<title>WKSR - Privacy</title><style>" +
                   "body{margin:0;background:#160d0d;color:#d39a5f;font:17px/1.6 monospace}" +
                   "main{max-width:760px;margin:24px auto;padding:24px 28px;border:3px solid #be824d}" +
                   "h1,h2{color:#eaa142;letter-spacing:1px}h1{font-size:28px;margin:0 0 4px}h2{font-size:20px;margin:24px 0 6px}" +
                   "p,li{margin:6px 0}ul{padding-left:22px}" +
                   "</style></head><body><main>" +
                   "<h1>WKSR privacy notice</h1>" +
                   "<p>What the White Knuckle SpeedRun (WKSR) match server stores about you, why, and for how long.</p>" +

                   "<h2>What is stored</h2><ul>" +
                   "<li><b>Your player:</b> a random player id, your player name, your rating, wins, losses and best time, " +
                   "when your player was created and when you last connected. From version 0.9.3 your player name is your " +
                   "<b>Steam name</b> (with a number added when another player already has it); your game sends it when it connects.</li>" +
                   "<li><b>Your key:</b> only a one-way hash of the secret key WKSR keeps on your PC. The key itself is never stored here.</li>" +
                   "<li><b>Your Steam account:</b> only a keyed hash of your Steam account number, used to recognise " +
                   "the same account again. Your Steam name is used as your player name (above). Your Steam profile, friends and " +
                   "password are never seen or stored.</li>" +
                   "<li><b>Finished 1v1 matches:</b> both players' ids and names, the seed, times, outcome, rating change and artifact bans. " +
                   "Party matches are not stored: their results are only sent to the players in them.</li>" +
                   "<li><b>Leaderboard times:</b> your fastest finished run in ranked 1v1, your fastest in ranked solo and your fastest of each segment (one of each), " +
                   "with its time, seed, modifiers and artifact bans, the WKSR and game versions it was played on, and when you set it. " +
                   "A faster run replaces it; slower runs are not kept.</li>" +
                   "<li><b>Replays:</b> while one of those times is among the 20 fastest on its leaderboard, a recording of the run is " +
                   "kept with it: where you were in the levels over time, and your split times. It is deleted as soon as the time is " +
                   "no longer among the 20 fastest, and replaced when you beat it.</li>" +
                   "<li><b>Mods:</b> while your game is running, WKSR tells the server which other mods (BepInEx plugins) it has loaded: " +
                   "their ids, names and versions. The server checks them against its list of approved mods and keeps your list in memory " +
                   "only while you are connected. What it stores is a list of the mods it has seen, not who had them. The server log can " +
                   "name the mods in your game.</li>" +
                   "<li><b>Server logs:</b> technical events such as failed connections and temporary blocks, with a shortened IP address " +
                   "(see <b>Your IP address</b> below). They are deleted automatically after <b>21 days</b>.</li>" +
                   "<li><b>Bug reports</b> (only if you send one from WKSR): your title and description, the files you attach, " +
                   "the contact you give (optional), your WKSR name and player id, the WKSR, mod and game versions, and WKSR's event " +
                   "log for that session if you leave it ticked. A report is emailed to the operator at the contact address below " +
                   "and deleted from this server once sent; the email is kept only as long as the bug needs.</li>" +
                   "</ul><p>During a match, your position and progress are sent to the other players' games. <b>Chat</b> lines are passed " +
                   "to the players in the same chat room: in the Global room everyone who has it open, in the Match room " +
                   "your lobby or match. So that players who join can read back, the server keeps the lines of the last " +
                   "24 hours in its memory, with the name of who said them (the Global room's, and a lobby's while it is open). " +
                   "They are never written to disk or to the logs, and a restart of the server forgets them. <b>Voice chat</b> (when you turn it on) " +
                   "is relayed live, encrypted, to the players in your lobby, or in a match to the players near you; it is never " +
                   "recorded, stored or logged. Lobbies exist in memory only, " +
                   "while they are open. A lobby its host makes public is shown in the lobby browser to every connected " +
                   "player, with the host's name, the mode and modifiers and how many players are in it. Nothing is sold, shared with " +
                   "advertisers or used for anything except running the matches.</p>" +

                   "<h2>Your IP address</h2>" +
                   "<p>Your game connects to this server directly, so the server sees your IP address, as every internet service does. " +
                   "It is <b>not stored with your player</b>, it is not in the database or its backups, and <b>other players never " +
                   "see it</b>: matches, chat and voice all pass through this server, and players' games never connect to each other.</p><ul>" +
                   "<li><b>While you are connected</b> the server holds your address in memory, to limit how many connections and " +
                   "connection attempts one address can make, and, with voice chat on, to know where to send the voice.</li>" +
                   "<li><b>Temporary blocks:</b> an address that floods the server or keeps breaking the protocol is refused for a " +
                   "while. The block is in memory only, and a restart of the server forgets it.</li>" +
                   "<li><b>New players:</b> to limit fake accounts, the server remembers how many new players an address created, " +
                   "in memory only, for 24 hours.</li>" +
                   "<li><b>Logs:</b> from version 0.9.5 the server log holds only a <b>shortened</b> address: the last number of an " +
                   "IPv4 address is left out (203.0.113.x), and of an IPv6 address only the first three groups are written. That names " +
                   "a network, not a connection. Log lines written before 0.9.5 hold the full address until they are deleted, " +
                   "21 days after they were written.</li>" +
                   "<li><b>This page and bug reports</b> go through a web server on the same machine. It keeps no record of visits; " +
                   "when a request fails, its error log can hold the full address of that request for a short time.</li>" +
                   "</ul><p>The company that hosts the server carries the traffic and can see addresses, as any network provider can.</p>" +

                   "<h2>What other players can see</h2>" +
                   "<p>The leaderboards in WKSR can be read by everyone who uses this server. They show your <b>name</b>; your " +
                   "<b>rating</b> with your wins and losses, once you are past your first " + Elo.ProvisionalGames + " ranked matches; " +
                   "your <b>fastest times</b> with the version they were played on; and, for the 20 fastest times of a leaderboard, " +
                   "the <b>replay</b>, which other players can download and run against as a ghost. You are only listed once you have " +
                   "played ranked on version 0.9.1 or later. Lobby matches and unranked runs are never listed. Replays you save " +
                   "yourself in WKSR stay on your PC and are never sent here.</p>" +

                   "<h2>Why</h2><ul>" +
                   "<li>To run matches and keep ratings and records (that is the service you use).</li>" +
                   "<li>To keep ranked play fair: only approved mods are allowed in it (the mods list).</li>" +
                   "<li>To keep the server safe from abuse and cheating (logs, limits, Steam link" +
                   (steamRequiredForRanked ? ", which ranked matches require" : "") + ").</li>" +
                   "<li>To fix the bugs you report.</li>" +
                   "</ul>" +

                   "<h2>Where and how</h2>" +
                   "<p>On a server in the European Union. The database is encrypted as a whole, and its backups are encrypted too. " +
                   "Connections to the match server use TLS. Bug reports and server alerts are delivered by email, through the " +
                   "operator's email provider, which may store them outside the EU.</p>" +

                   "<h2>Steam sign-in</h2>" +
                   "<p>From version 0.9.3 there is nothing to press: when your game connects, it tells this server which Steam " +
                   "account it is running under and your Steam name. The account number is stored only as a keyed hash, as " +
                   "described above, so that your rating and times stay yours on another PC. In older versions, linking happened " +
                   "on Steam's own website, under Valve's privacy policy, with the same result.</p>" +

                   "<h2>How long</h2>" +
                   "<p>Your player, match history and leaderboard times are kept until you ask for them to be deleted. A replay is " +
                   "kept only while its time is among the 20 fastest. Logs are kept for 21 days. IP addresses are not " +
                   "stored with your player (see Your IP address).</p>" +

                   "<h2>Your rights</h2>" +
                   "<p>You can ask for a copy of everything stored about you, for a leaderboard time (and its replay) to be removed, " +
                   "or for your player to be deleted. When a player is deleted, their leaderboard times and replays are deleted too, and " +
                   "their matches stay in the other players' history with the name and id replaced by \"deleted player\". " +
                   "Contact: <b>" + who + "</b>. Mention your WKSR name.</p>" +
                   "</main></body></html>";
        }
    }
}
