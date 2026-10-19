using System;
using System.Collections.Generic;
using System.Linq;

namespace WKRace.Protocol
{
    // What kind of run a recording is of.
    public enum RunKind : byte
    {
        UnrankedSolo = 0,
        RankedSolo = 1,
        Ranked1v1 = 2,
        Friendly = 3,
    }

    // One recorded run, stored as the messages an opponent's game would have received, each at its race time.
    public sealed class RunRecording : ISoloBest
    {
        private const string Magic = "WKRUN";
        // 3: every time is the game's own run timer (0.9.1). Older files were timed on the real-time clock, so their
        // ghost and splits would not line up with a run made now: they are not read any more.
        // 4: the file also says which modifiers and artifact bans the run had, who ran it and on which version
        // (a saved replay or a leaderboard replay has to be run with the same ones).
        // 5: the file also says which segment the run was of, if any.
        private const byte FormatVersion = 5;
        private const byte OldestReadable = 3;
        public const int MaxBans = 8;
        public const int MaxEvents = 200000;

        public int Seed;
        // When the run ended: its finish, or when it died (or when the race ended for a survivor).
        public int EndMs;
        // Reached the end of the climb. Otherwise the run died, or was cut short by the other racer's death.
        public bool Finished;
        public DateTime RecordedUtc;
        public bool FromRace;
        // The modifiers and artifact bans the run was played with, the player's name, the WKRace version, and what
        // kind of run it was. Files from before these were kept have the ranked modifiers, no bans and no name.
        public MatchRules Rules = MatchRules.Ranked;
        public List<string> Bans = new List<string>();
        public string Player = "";
        public string Version = "";
        public RunKind Kind;
        // A run of a segment (Segments), which ended where the segment does. 0: the whole climb.
        public byte Segment;
        public List<KeyValuePair<int, Message>> Events = new List<KeyValuePair<int, Message>>();
        public IList<KeyValuePair<int, Message>> Replay { get { return Events; } }
        bool ISoloBest.Finished { get { return Finished; } }
        int ISoloBest.EndMs { get { return EndMs; } }

        // Levels reached in the run's last attempt, and when it reached the last of them.
        public int Levels { get { return LastAttemptSplits().Count(); } }
        public int LastSplitMs { get { return LastAttemptSplits().Select(s => s.RaceTimeMs).DefaultIfEmpty(EndMs).Max(); } }

        private IEnumerable<OpponentSplit> LastAttemptSplits()
        {
            List<OpponentSplit> splits = Events.Select(e => e.Value).OfType<OpponentSplit>().ToList();
            if (splits.Count == 0) return splits;
            int attempt = splits.Max(s => s.Attempt);
            return splits.Where(s => s.Attempt == attempt).GroupBy(s => s.Index).Select(g => g.First());
        }

        // The personal-best rule: a finished run beats one that did not finish, the faster finish wins; between
        // unfinished runs the one that got further wins, and on a tie the one that reached its last level sooner.
        public bool IsBetterThan(RunRecording other)
        {
            if (other == null) return true;
            if (Finished != other.Finished) return Finished;
            if (Finished) return EndMs < other.EndMs;
            if (Levels != other.Levels) return Levels > other.Levels;
            return LastSplitMs < other.LastSplitMs;
        }

        // "12:34.56" for a finish, "out after 17 levels" otherwise.
        public string Describe()
        {
            return Finished ? RaceFormat.Time(EndMs) : "out after " + Levels + (Levels == 1 ? " level" : " levels");
        }

        public byte[] ToBytes()
        {
            var w = new PacketWriter(64 * 1024);
            foreach (char c in Magic) w.WriteByte((byte)c);
            w.WriteByte((byte)('0' + FormatVersion));
            w.WriteInt32(Seed);
            w.WriteInt32(EndMs);
            w.WriteInt64(new DateTimeOffset(RecordedUtc).ToUnixTimeMilliseconds());
            w.WriteBool(FromRace);
            w.WriteBool(Finished);
            w.WriteInt32((int)Rules);
            int bans = Math.Min(Bans != null ? Bans.Count : 0, MaxBans);
            w.WriteByte((byte)bans);
            for (int i = 0; i < bans; i++) w.WriteString(Bans[i] ?? "", ProtocolConstants.MaxItemNameLength);
            w.WriteString(Player ?? "", ProtocolConstants.MaxNameLength);
            w.WriteString(Version ?? "", 32);
            w.WriteByte((byte)Kind);
            w.WriteByte(Segment);
            w.WriteInt32(Events.Count);
            foreach (KeyValuePair<int, Message> e in Events)
            {
                byte[] payload = MessageCodec.Encode(e.Value);
                w.WriteInt32(e.Key);
                w.WriteUInt16((ushort)payload.Length);
                w.WriteFixedBytes(payload, payload.Length);
            }
            return w.ToArray();
        }

        // The file, compressed: how the server keeps a leaderboard replay and sends it.
        public byte[] ToPacked()
        {
            byte[] raw = ToBytes();
            using (var buffer = new System.IO.MemoryStream())
            {
                using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.Optimal, true))
                    deflate.Write(raw, 0, raw.Length);
                return buffer.ToArray();
            }
        }

        // Throws ProtocolException for damaged data, or data that unpacks to more than maxBytes.
        public static RunRecording FromPacked(byte[] packed, int maxBytes = 16 * 1024 * 1024)
        {
            try
            {
                using (var input = new System.IO.MemoryStream(packed))
                using (var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress))
                using (var output = new System.IO.MemoryStream())
                {
                    var chunk = new byte[16384];
                    int n;
                    while ((n = deflate.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (output.Length + n > maxBytes) throw new ProtocolException("Replay too large");
                        output.Write(chunk, 0, n);
                    }
                    return FromBytes(output.ToArray());
                }
            }
            catch (System.IO.InvalidDataException e)
            {
                throw new ProtocolException("Damaged replay: " + e.Message);
            }
        }

        // Throws ProtocolException for a damaged or foreign file. Version 1 files (0.6.3) only held finished runs.
        public static RunRecording FromBytes(byte[] data)
        {
            var r = new PacketReader(data, 0, data.Length);
            foreach (char c in Magic)
                if (r.ReadByte() != (byte)c) throw new ProtocolException("Not a WKSR run file");
            int version = r.ReadByte() - '0';
            if (version < 1 || version > FormatVersion) throw new ProtocolException("Unknown run file version");
            if (version < OldestReadable) throw new ProtocolException("Run file from before 0.9.1 (timed on another clock)");
            var run = new RunRecording
            {
                Seed = r.ReadInt32(),
                EndMs = r.ReadInt32(),
                RecordedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64()).UtcDateTime,
                FromRace = r.ReadBool(),
            };
            run.Finished = version == 1 || r.ReadBool();
            if (version >= 4)
            {
                run.Rules = (MatchRules)r.ReadInt32() & MatchRules.All;
                int bans = r.ReadByte();
                if (bans > MaxBans) throw new ProtocolException("Bad ban count");
                for (int i = 0; i < bans; i++) run.Bans.Add(r.ReadString(ProtocolConstants.MaxItemNameLength));
                run.Player = r.ReadString(ProtocolConstants.MaxNameLength);
                run.Version = r.ReadString(32);
                run.Kind = (RunKind)r.ReadByte();
            }
            if (version >= 5) run.Segment = r.ReadByte();
            int count = r.ReadInt32();
            if (count < 0 || count > MaxEvents) throw new ProtocolException("Bad event count");
            for (int i = 0; i < count; i++)
            {
                int time = r.ReadInt32();
                int length = r.ReadUInt16();
                Message m = MessageCodec.Decode(r.ReadFixedBytes(length));
                if (!(m is OpponentState || m is OpponentSplit || m is OpponentAttempt || m is OpponentFinished))
                    throw new ProtocolException("Unexpected message in a run file: " + m.Type);
                run.Events.Add(new KeyValuePair<int, Message>(time, m));
            }
            if (r.Remaining != 0) throw new ProtocolException("Trailing data in a run file");
            return run;
        }
    }

    // Records the player's own run (as the game reports it) for replaying later. PlayerState arrives on the game link's
    // thread, everything else on the UI thread, hence the lock.
    public sealed class RunRecorder
    {
        // The ghost is replayed at about 10 updates a second, so closer states are not kept. (The server keeps
        // fewer: it records every ranked run in case it makes the leaderboard's top places.)
        private readonly int _minStateGapMs;

        public RunRecorder(int minStateGapMs = 90)
        {
            _minStateGapMs = Math.Max(0, minStateGapMs);
        }

        private readonly object _gate = new object();
        private readonly List<KeyValuePair<int, Message>> _events = new List<KeyValuePair<int, Message>>();
        private int _lastStateMs = int.MinValue;

        public bool Cheated { get; private set; }
        // When the player died, or -1.
        public int DiedMs { get; private set; } = -1;

        public void Add(Message fromGame)
        {
            lock (_gate)
            {
                if (_events.Count >= RunRecording.MaxEvents - 1) return;
                switch (fromGame)
                {
                    case PlayerState s:
                        if ((long)s.RaceTimeMs - _lastStateMs < _minStateGapMs) return;
                        _lastStateMs = s.RaceTimeMs;
                        var state = new OpponentState();
                        state.CopyFrom(s);
                        _events.Add(new KeyValuePair<int, Message>(s.RaceTimeMs, state));
                        break;
                    case SplitReached s:
                        _events.Add(new KeyValuePair<int, Message>(s.RaceTimeMs,
                            new OpponentSplit { Attempt = s.Attempt, Index = s.Index, Level = s.Level, Region = s.Region, RaceTimeMs = s.RaceTimeMs }));
                        break;
                    case AttemptStarted a:
                        _events.Add(new KeyValuePair<int, Message>(a.RaceTimeMs, new OpponentAttempt { Attempt = a.Attempt, RaceTimeMs = a.RaceTimeMs }));
                        break;
                    case PlayerFinished f:
                        if (f.Cheated) Cheated = true;
                        break;
                    case PlayerDied d:
                        if (DiedMs < 0) DiedMs = Math.Max(0, d.RaceTimeMs);
                        break;
                }
            }
        }

        // The run as it ended at endMs: finished (it then ends with its finish), or not (died, or cut short).
        public RunRecording Finish(int seed, int endMs, bool finished, bool fromRace)
        {
            lock (_gate)
            {
                var events = _events.Where(e => e.Key <= endMs).ToList();
                // Stable by race time: a split and a state can share a time, and their order must not change.
                events = events.Select((e, i) => new { e, i }).OrderBy(x => x.e.Key).ThenBy(x => x.i).Select(x => x.e).ToList();
                if (finished)
                {
                    int attempt = 1;
                    foreach (KeyValuePair<int, Message> e in events)
                        if (e.Value is OpponentAttempt a) attempt = Math.Max(attempt, a.Attempt);
                    events.Add(new KeyValuePair<int, Message>(endMs, new OpponentFinished { Attempt = attempt, RaceTimeMs = endMs }));
                }
                return new RunRecording { Seed = seed, EndMs = endMs, Finished = finished, RecordedUtc = DateTime.UtcNow, FromRace = fromRace, Events = events };
            }
        }
    }
}
