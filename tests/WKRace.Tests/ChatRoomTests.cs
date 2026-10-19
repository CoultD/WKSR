using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class ChatRoomTests
    {
        private static ChatMessage[] Global(TestClient c)
        {
            return c.Conn.Sent.OfType<ChatMessage>().Where(m => m.Room == ChatRooms.Global && !m.System).ToArray();
        }

        [Fact]
        public void Global_GoesToEveryoneWhoJoinedIt_AndANewcomerReadsBack()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                TestClient c = h.Login("Charlie");
                a.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                b.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });

                a.Send(new ChatSend { Room = ChatRooms.Global, Text = "hello all" });
                ChatMessage got = Global(b).Single();
                Assert.Equal("Alpha", got.From);
                Assert.Equal("hello all", got.Text);
                Assert.Equal(h.Now, got.SentUnixMs);
                Assert.False(got.History);
                Assert.Single(Global(a));
                Assert.Empty(Global(c));

                // Not in the room: nothing is passed on, and the player is told why.
                c.Send(new ChatSend { Room = ChatRooms.Global, Text = "me too" });
                Assert.True(c.Last<ChatMessage>().System);
                Assert.Single(Global(b));

                // Joining brings what was said, marked as older lines.
                h.Advance(60000);
                c.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                ChatMessage old = Global(c).Single();
                Assert.True(old.History);
                Assert.Equal("hello all", old.Text);
                Assert.Equal(h.Now - 60000, old.SentUnixMs);
                // Asking again does not send it all again.
                c.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                Assert.Single(Global(c));

                // Leaving the room ends it.
                b.Send(new ChatJoin { Room = ChatRooms.Global, Join = false });
                a.Send(new ChatSend { Room = ChatRooms.Global, Text = "still here?" });
                Assert.Single(Global(b));
                Assert.Equal(2, Global(c).Length);
            }
        }

        [Fact]
        public void Global_ForgetsLinesAfter24Hours()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                a.Send(new ChatSend { Room = ChatRooms.Global, Text = "yesterday" });
                h.Advance(23L * 60 * 60 * 1000);
                a.Send(new ChatSend { Room = ChatRooms.Global, Text = "an hour ago" });
                h.Advance(60L * 60 * 1000 + 1);

                TestClient b = h.Login("Bravo");
                b.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                Assert.Equal("an hour ago", Global(b).Single().Text);
                Assert.Equal(1, h.Core.GlobalChatLineCount);
            }
        }

        [Fact]
        public void Global_IsAskedForAgainAfterSigningInAgain()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                a.Send(new ChatSend { Room = ChatRooms.Global, Text = "one" });
                a.Disconnect();
                a.Reconnect();
                int before = Global(a).Length;
                a.Send(new ChatJoin { Room = ChatRooms.Global, Join = true });
                Assert.Equal(before + 1, Global(a).Length);
                Assert.True(Global(a).Last().History);
            }
        }

        [Fact]
        public void ALobbyKeepsItsChat_ForAPlayerWhoJoinsLater()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                a.Send(new LobbyCreate { Mode = MatchMode.Party });
                a.Send(new ChatSend { Text = "waiting for you" });
                b.Send(new LobbyJoin { LobbyId = a.Last<LobbyCreated>().LobbyId });

                ChatMessage old = b.Conn.Sent.OfType<ChatMessage>().Single(m => !m.System);
                Assert.Equal("waiting for you", old.Text);
                Assert.Equal(ChatRooms.Match, old.Room);
                Assert.True(old.History);
            }
        }

        [Fact]
        public void TheStatus_SaysHowManyPlayersAreSearching()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Assert.Equal(0, a.Last<ServerStatus>().PlayersQueued);
                b.Send(new QueueJoin());
                h.Advance(3000);
                Assert.Equal(1, a.Last<ServerStatus>().PlayersQueued);
                b.Send(new QueueLeave());
                h.Advance(3000);
                Assert.Equal(0, a.Last<ServerStatus>().PlayersQueued);
            }
        }
    }
}
