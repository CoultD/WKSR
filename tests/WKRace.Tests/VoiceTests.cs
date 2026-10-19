using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class VoiceTests
    {
        private sealed class Sent
        {
            public IPEndPoint To;
            public byte[] Packet;
        }

        private static List<Sent> Capture(Harness h)
        {
            var sent = new List<Sent>();
            h.Core.VoiceSend = (to, packet) => sent.Add(new Sent { To = to, Packet = packet });
            return sent;
        }

        private static IPEndPoint Ep(int n) { return new IPEndPoint(IPAddress.Parse("198.51.100." + n), 50000 + n); }

        // The client's side: register the address (keepalive), turn voice on.
        private static void Join(Harness h, TestClient c, int n, ref ulong counter)
        {
            VoiceHello hello = c.Last<VoiceHello>();
            c.Send(new VoiceState { Enabled = true });
            byte[] keep = VoiceWire.SealToServer(hello.Key, hello.Token, ++counter, VoiceWire.KindKeepAlive, 0, ReadOnlySpan<byte>.Empty);
            h.Core.OnVoicePacket(Ep(n), keep, keep.Length);
        }

        private static void Speak(Harness h, TestClient c, int n, ulong counter, uint seq, byte[] opus)
        {
            VoiceHello hello = c.Last<VoiceHello>();
            byte[] packet = VoiceWire.SealToServer(hello.Key, hello.Token, counter, VoiceWire.KindAudio, seq, opus);
            h.Core.OnVoicePacket(Ep(n), packet, packet.Length);
        }

        private static List<TestClient> Lobby(Harness h, params string[] names)
        {
            var players = names.Select(n => h.Login(n)).ToList();
            players[0].Send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.None });
            uint id = players[0].Last<LobbyCreated>().LobbyId;
            foreach (TestClient p in players.Skip(1)) p.Send(new LobbyJoin { LobbyId = id });
            return players;
        }

        [Fact]
        public void EveryConnection_GetsItsOwnVoiceKey()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                VoiceHello first = a.Last<VoiceHello>();
                Assert.Equal(h.Options.VoicePort, first.Port);
                Assert.NotEqual(0ul, first.Token);
                a.Reconnect();
                VoiceHello second = a.Last<VoiceHello>();
                Assert.NotEqual(first.Token, second.Token);
                Assert.False(first.Key.SequenceEqual(second.Key));
            }
        }

        [Fact]
        public void Lobby_VoiceReachesTheOthers_EncryptedForEach()
        {
            using (var h = new Harness())
            {
                List<Sent> sent = Capture(h);
                List<TestClient> p = Lobby(h, "Alpha", "Bravo", "Charlie");
                TestClient outsider = h.Login("Outsider");
                ulong ca = 0, cb = 0, cc = 0, co = 0;
                Join(h, p[0], 1, ref ca);
                Join(h, p[1], 2, ref cb);
                Join(h, p[2], 3, ref cc);
                Join(h, outsider, 4, ref co);

                Speak(h, p[0], 1, ++ca, 7, new byte[] { 1, 2, 3 });
                Assert.Equal(new[] { Ep(2), Ep(3) }, sent.Select(s => s.To).OrderBy(e => e.Port));

                // Each listener can open it with their own key; it carries the speaker's voice id and frame number.
                Sent toBravo = sent.First(s => s.To.Equals(Ep(2)));
                byte kind; ulong speaker, counter;
                Assert.True(VoiceWire.TryHeader(toBravo.Packet, toBravo.Packet.Length, out kind, out speaker, out counter));
                Assert.Equal(VoiceWire.KindRelay, kind);
                Assert.Equal(p[1].Last<VoicePeers>().Peers.First(x => x.Name == "Alpha").VoiceId, (ushort)speaker);
                byte[] body;
                Assert.True(VoiceWire.TryOpen(p[1].Last<VoiceHello>().Key, toBravo.Packet, toBravo.Packet.Length, false, out body));
                Assert.Equal(7u, VoiceWire.Seq(body));
                Assert.Equal(new byte[] { 1, 2, 3 }, body.Skip(4).ToArray());
                Assert.False(VoiceWire.TryOpen(p[2].Last<VoiceHello>().Key, toBravo.Packet, toBravo.Packet.Length, false, out body));
            }
        }

        [Fact]
        public void ForgedReplayedAndSilencedPackets_AreNotRelayed()
        {
            using (var h = new Harness())
            {
                List<Sent> sent = Capture(h);
                List<TestClient> p = Lobby(h, "Alpha", "Bravo");
                ulong ca = 0, cb = 0;
                Join(h, p[0], 1, ref ca);
                Join(h, p[1], 2, ref cb);

                // Wrong key.
                VoiceHello hello = p[0].Last<VoiceHello>();
                byte[] forged = VoiceWire.SealToServer(VoiceWire.NewKey(), hello.Token, ++ca, VoiceWire.KindAudio, 1, new byte[] { 9 });
                h.Core.OnVoicePacket(Ep(1), forged, forged.Length);
                Assert.Empty(sent);

                // A replay of a counter already seen.
                Speak(h, p[0], 1, ++ca, 2, new byte[] { 9 });
                Assert.Single(sent);
                Speak(h, p[0], 1, ca, 2, new byte[] { 9 });
                Assert.Single(sent);

                // A listener with voice off gets nothing.
                p[1].Send(new VoiceState { Enabled = false });
                Speak(h, p[0], 1, ++ca, 3, new byte[] { 9 });
                Assert.Single(sent);
            }
        }

        [Fact]
        public void Race_OnlyRacersInRangeHear()
        {
            using (var h = new Harness())
            {
                h.Options.VoiceRangeMeters = 50;
                List<Sent> sent = Capture(h);
                List<TestClient> p = Lobby(h, "Alpha", "Bravo", "Charlie");
                ulong ca = 0, cb = 0, cc = 0;
                Join(h, p[0], 1, ref ca);
                Join(h, p[1], 2, ref cb);
                Join(h, p[2], 3, ref cc);
                p[0].Send(new LobbyStart());
                foreach (TestClient c in p) c.Ready();
                h.Advance(h.Options.CountdownMs + 1000);

                p[0].Send(new PlayerState { Attempt = 1, RaceTimeMs = 1000, X = 0, Y = 0, Z = 0 });
                p[1].Send(new PlayerState { Attempt = 1, RaceTimeMs = 1000, X = 30, Y = 0, Z = 0 });
                p[2].Send(new PlayerState { Attempt = 1, RaceTimeMs = 1000, X = 0, Y = 80, Z = 0 });
                Speak(h, p[0], 1, ++ca, 1, new byte[] { 5 });
                Assert.Equal(new[] { Ep(2) }, sent.Select(s => s.To));

                // The racers' voice ids map to their racer numbers, for where each voice comes from.
                VoicePeers peers = p[0].Last<VoicePeers>();
                Assert.Equal(p[1].Match.YourRacer, peers.Peers.First(x => x.Name == "Bravo").Racer);
            }
        }

        [Fact]
        public void Peers_FollowTheLobby()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Lobby(h, "Alpha", "Bravo", "Charlie");
                Assert.Equal(new[] { "Bravo", "Charlie" }, p[0].Last<VoicePeers>().Peers.Select(x => x.Name).OrderBy(x => x));
                p[2].Send(new LobbyCancel());
                Assert.Equal(new[] { "Bravo" }, p[0].Last<VoicePeers>().Peers.Select(x => x.Name));
                Assert.Empty(p[2].Last<VoicePeers>().Peers);
                Assert.All(p[0].Last<VoicePeers>().Peers, x => Assert.Equal(VoicePeer.NoRacer, x.Racer));
            }
        }

        [Fact]
        public void VoiceWire_RoundTrips()
        {
            byte[] key = VoiceWire.NewKey();
            byte[] packet = VoiceWire.SealToServer(key, 42, 7, VoiceWire.KindAudio, 99, new byte[] { 1, 2, 3, 4 });
            byte kind; ulong id, counter;
            Assert.True(VoiceWire.TryHeader(packet, packet.Length, out kind, out id, out counter));
            Assert.Equal(42ul, id);
            Assert.Equal(7ul, counter);
            byte[] body;
            Assert.True(VoiceWire.TryOpen(key, packet, packet.Length, true, out body));
            Assert.Equal(99u, VoiceWire.Seq(body));
            // The wrong direction never opens (a relayed packet cannot be bounced back as the sender's).
            Assert.False(VoiceWire.TryOpen(key, packet, packet.Length, false, out body));
            packet[packet.Length - 1] ^= 1;
            Assert.False(VoiceWire.TryOpen(key, packet, packet.Length, true, out body));
        }
    }
}
