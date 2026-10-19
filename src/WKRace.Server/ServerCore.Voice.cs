using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Proximity voice: every connection gets a voice key, token and id (VoiceHello). WKSR sends its encrypted Opus
    // frames over UDP (RaceServer's voice socket hands them to OnVoicePacket on the core thread); each frame is
    // re-encrypted for, and sent to, everyone who can hear the speaker: the other players in their lobby, or in a race
    // the racers within VoiceRangeMeters. Voice is never stored or logged.
    public sealed partial class ServerCore
    {
        // At most this many frames a second per speaker (50 is normal speech).
        private const int VoiceFramesPerSecond = 75;
        // A position older than this does not count for the range check.
        private const long VoicePositionMaxAgeMs = 5000;

        private readonly Dictionary<ulong, Player> _voiceByToken = new Dictionary<ulong, Player>();
        private ushort _nextVoiceId = 1;

        // Sends a UDP packet to a player's voice address (set by RaceServer; tests capture it).
        public Action<IPEndPoint, byte[]> VoiceSend { get; set; }

        public bool VoiceAvailable { get { return _options.VoicePort > 0; } }

        // A fresh voice identity for a new connection (a reconnect gets a new key, so an old one is worthless).
        private void GiveVoice(Player player)
        {
            if (!VoiceAvailable) return;
            if (player.VoiceToken != 0) _voiceByToken.Remove(player.VoiceToken);
            ulong token;
            do
            {
                var bytes = new byte[8];
                RandomNumberGenerator.Fill(bytes);
                token = BitConverter.ToUInt64(bytes, 0);
            } while (token == 0 || _voiceByToken.ContainsKey(token));
            player.VoiceToken = token;
            player.VoiceKey = VoiceWire.NewKey();
            player.VoiceEndpoint = null;
            player.VoiceInCounter = 0;
            player.VoiceOutCounter = 0;
            player.VoiceEnabled = false;
            if (player.VoiceId == 0) player.VoiceId = NextVoiceId();
            _voiceByToken[token] = player;
            player.Send(new VoiceHello
            {
                Port = _options.VoicePublicPort > 0 ? _options.VoicePublicPort : _options.VoicePort,
                Key = player.VoiceKey,
                Token = token,
                RangeMeters = _options.VoiceRangeMeters,
            });
        }

        private ushort NextVoiceId()
        {
            for (int tries = 0; tries < 65536; tries++)
            {
                ushort id = _nextVoiceId++;
                if (_nextVoiceId == 0) _nextVoiceId = 1;
                if (id != 0 && !_players.Values.Any(p => p.VoiceId == id)) return id;
            }
            return 1;
        }

        private void DropVoice(Player player)
        {
            if (player.VoiceToken != 0) _voiceByToken.Remove(player.VoiceToken);
            player.VoiceToken = 0;
            player.VoiceEndpoint = null;
            player.VoiceEnabled = false;
        }

        private void HandleVoiceState(Player player, VoiceState state)
        {
            player.VoiceEnabled = state.Enabled && VoiceAvailable;
        }

        // One UDP datagram from the voice socket.
        public void OnVoicePacket(IPEndPoint from, byte[] data, int length)
        {
            byte kind;
            ulong token, counter;
            if (!VoiceWire.TryHeader(data, length, out kind, out token, out counter) || kind == VoiceWire.KindRelay) return;
            Player player;
            if (!_voiceByToken.TryGetValue(token, out player) || player.Conn == null) return;
            if (counter <= player.VoiceInCounter) return;
            byte[] body;
            if (!VoiceWire.TryOpen(player.VoiceKey, data, length, true, out body)) return;
            player.VoiceInCounter = counter;
            player.VoiceEndpoint = from;
            if (kind != VoiceWire.KindAudio || !player.VoiceEnabled || body.Length < 4 || body.Length - 4 > VoiceWire.MaxOpusBytes) return;

            long now = _now();
            if (now - player.VoiceWindowMs >= 1000)
            {
                player.VoiceWindowMs = now;
                player.VoiceFrames = 0;
            }
            if (++player.VoiceFrames > VoiceFramesPerSecond) return;

            uint seq = VoiceWire.Seq(body);
            var opus = new ReadOnlySpan<byte>(body, 4, body.Length - 4);
            Action<IPEndPoint, byte[]> send = VoiceSend;
            if (send == null) return;
            foreach (Player to in VoiceListeners(player, now))
            {
                if (to.VoiceEndpoint == null || !to.VoiceEnabled || to.Conn == null) continue;
                send(to.VoiceEndpoint, VoiceWire.SealToClient(to.VoiceKey, player.VoiceId, ++to.VoiceOutCounter, seq, opus));
            }
        }

        // Who hears this speaker: in a race, the other racers in range (everyone, before positions are known); else
        // the other players in their lobby who are not racing.
        private IEnumerable<Player> VoiceListeners(Player speaker, long now)
        {
            Match match = speaker.Match;
            if (match != null && match.Phase != MatchPhase.Completed)
            {
                Slot me = match.SlotOf(speaker);
                long range = _options.VoiceRangeMeters;
                foreach (Slot s in match.Slots)
                {
                    if (s == me) continue;
                    if (range > 0 && me != null && HasFreshPosition(me, now) && HasFreshPosition(s, now))
                    {
                        double dx = me.PosX - s.PosX, dy = me.PosY - s.PosY, dz = me.PosZ - s.PosZ;
                        if (dx * dx + dy * dy + dz * dz > range * range) continue;
                    }
                    yield return s.Player;
                }
                yield break;
            }
            if (speaker.Lobby != null)
            {
                foreach (Player p in speaker.Lobby.Members)
                    if (p != speaker && p.Match == null) yield return p;
            }
        }

        private static bool HasFreshPosition(Slot slot, long now)
        {
            return slot.PosAtMs > 0 && now - slot.PosAtMs <= VoicePositionMaxAgeMs;
        }

        // Tells a player whose voices they can hear now (and which racer each is, for where the voice comes from).
        private void SendVoicePeers(Player player)
        {
            if (!VoiceAvailable || player.Conn == null) return;
            var peers = new VoicePeers();
            Match match = player.Match;
            if (match != null && match.Phase != MatchPhase.Completed)
            {
                foreach (Slot s in match.Slots)
                    if (s.Player != player) peers.Peers.Add(new VoicePeer { VoiceId = s.Player.VoiceId, Name = s.Name, Racer = s.Racer });
            }
            else if (player.Lobby != null)
            {
                foreach (Player p in player.Lobby.Members)
                    if (p != player && p.Match == null) peers.Peers.Add(new VoicePeer { VoiceId = p.VoiceId, Name = p.Record.Name });
            }
            player.Send(peers);
        }

        private void SendVoicePeers(IEnumerable<Player> players)
        {
            foreach (Player p in players.ToList()) SendVoicePeers(p);
        }
    }
}
