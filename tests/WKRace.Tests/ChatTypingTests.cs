using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class ChatTypingTests
    {
        private static TestClient LoginAs(Harness h, string name, string clientVersion)
        {
            var c = new TestClient(h, name);
            ClientHello hello = c.Hello();
            hello.ClientVersion = clientVersion;
            c.Send(hello);
            return c;
        }

        // A party lobby with these players in it (the first one hosts).
        private static void Lobby(params TestClient[] players)
        {
            players[0].Send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds });
            uint id = players[0].Last<LobbyCreated>().LobbyId;
            foreach (TestClient p in players.Skip(1)) p.Send(new LobbyJoin { LobbyId = id });
        }

        [Fact]
        public void ServerFeatures_SayTypingOnlyToClientsThatKnowIt()
        {
            using (var h = new Harness())
            {
                Assert.Equal(0, LoginAs(h, "Old", "0.9.5").Conn.Count<ServerFeatures>());   // older clients hear of no features at all
                Assert.True(LoginAs(h, "New", "0.9.6").Last<ServerFeatures>().ChatTyping);
            }
        }

        [Fact]
        public void Typing_ReachesTheOthersInTheLobby_NotTheTypistNorOldClients()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha", "0.9.6");
                var b = LoginAs(h, "Bravo", "0.9.6");
                var old = LoginAs(h, "Charlie", "0.9.5");
                var outsider = LoginAs(h, "Delta", "0.9.6");
                Lobby(a, b, old);

                a.Send(new ChatTyping { Room = ChatRooms.Match, Typing = true });
                ChatTypingNote note = b.Last<ChatTypingNote>();
                Assert.Equal("Alpha", note.From);
                Assert.Equal(ChatRooms.Match, note.Room);
                Assert.True(note.Typing);
                Assert.Equal(0, a.Conn.Count<ChatTypingNote>());
                Assert.Equal(0, old.Conn.Count<ChatTypingNote>());
                Assert.Equal(0, outsider.Conn.Count<ChatTypingNote>());

                a.Send(new ChatTyping { Room = ChatRooms.Match, Typing = false });
                Assert.False(b.Last<ChatTypingNote>().Typing);
            }
        }

        [Fact]
        public void TypingRepeats_AreLimited_ButStoppingAlwaysGetsThrough()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha", "0.9.6");
                var b = LoginAs(h, "Bravo", "0.9.6");
                Lobby(a, b);
                h.Advance(1000);
                a.Send(new ChatTyping { Room = ChatRooms.Match, Typing = true });
                a.Send(new ChatTyping { Room = ChatRooms.Match, Typing = true });
                Assert.Equal(1, b.Conn.Count<ChatTypingNote>());
                a.Send(new ChatTyping { Room = ChatRooms.Match, Typing = false });
                Assert.Equal(2, b.Conn.Count<ChatTypingNote>());
                h.Advance(500);
                a.Send(new ChatTyping { Room = ChatRooms.Match, Typing = true });
                Assert.Equal(3, b.Conn.Count<ChatTypingNote>());
            }
        }

        [Fact]
        public void GlobalTyping_ReachesOnlyPlayersInGlobal()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha", "0.9.6");
                var b = LoginAs(h, "Bravo", "0.9.6");
                var c = LoginAs(h, "Charlie", "0.9.6");
                a.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                b.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                a.Send(new ChatTyping { Room = ChatRooms.Global, Typing = true });
                Assert.Equal(ChatRooms.Global, b.Last<ChatTypingNote>().Room);
                Assert.Equal(0, c.Conn.Count<ChatTypingNote>());
            }
        }

        [Fact]
        public void TypingMessages_SurviveTheWire_AndFeaturesSkipWhatTheyDoNotKnow()
        {
            var note = (ChatTypingNote)MessageCodec.Decode(MessageCodec.Encode(new ChatTypingNote { Room = ChatRooms.Match, From = "Alpha", Typing = true }));
            Assert.Equal("Alpha", note.From);
            Assert.True(note.Typing);
            var typing = (ChatTyping)MessageCodec.Decode(MessageCodec.Encode(new ChatTyping { Room = ChatRooms.Global, Typing = true }));
            Assert.Equal(ChatRooms.Global, typing.Room);

            // The 0.9.6 form (one byte) still reads.
            Assert.False(((ServerFeatures)MessageCodec.Decode(MessageCodec.Encode(new ServerFeatures { LobbyBrowser = true }))).ChatTyping);
            // A later server's list, with flags this version does not know, reads too.
            var w = new PacketWriter(8);
            w.WriteByte((byte)MessageType.ServerFeatures);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            var features = (ServerFeatures)MessageCodec.Decode(w.ToArray());
            Assert.True(features.LobbyBrowser);
            Assert.True(features.ChatTyping);
        }
    }
}
