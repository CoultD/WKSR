using System;
using System.Collections.Generic;
using System.Net;

namespace WKRace.Server
{
    // Per-address limits applied before a connection costs anything (TLS handshake, decoding): too many connection
    // attempts in a minute, or repeated misbehaviour (rate-limit kicks, bad frames, protocol violations), earns a
    // temporary ban. Loopback is never limited. Thread-safe; times are milliseconds from any monotonic clock.
    internal sealed class ConnectionGuard
    {
        private const long WindowMs = 60000;
        private const int StrikesForBan = 3;
        private const long StrikeWindowMs = 600000;
        // Bounds memory if a flood comes from very many addresses.
        private const int MaxTracked = 50000;

        private sealed class Entry
        {
            public readonly Queue<long> Attempts = new Queue<long>();
            public readonly Queue<long> Strikes = new Queue<long>();
            public long BannedUntil;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<IPAddress, Entry> _entries = new Dictionary<IPAddress, Entry>();
        private readonly int _attemptsPerMinute;
        private readonly long _banMs;

        public ConnectionGuard(int attemptsPerMinute, int banMinutes)
        {
            _attemptsPerMinute = attemptsPerMinute;
            _banMs = banMinutes * 60000L;
        }

        public enum Verdict
        {
            Allowed,
            Banned,
            // Allowed until now; this attempt started the ban (log it once).
            NewlyBanned,
        }

        public Verdict Admit(IPAddress address, long now)
        {
            if (IPAddress.IsLoopback(address)) return Verdict.Allowed;
            lock (_gate)
            {
                Entry e = Get(address);
                if (now < e.BannedUntil) return Verdict.Banned;
                if (_attemptsPerMinute <= 0) return Verdict.Allowed;

                Trim(e.Attempts, now - WindowMs);
                e.Attempts.Enqueue(now);
                if (e.Attempts.Count <= _attemptsPerMinute) return Verdict.Allowed;
                e.Attempts.Clear();
                e.BannedUntil = now + _banMs;
                return Verdict.NewlyBanned;
            }
        }

        // Returns true when this strike started a ban.
        public bool Strike(IPAddress address, long now)
        {
            if (IPAddress.IsLoopback(address)) return false;
            lock (_gate)
            {
                Entry e = Get(address);
                Trim(e.Strikes, now - StrikeWindowMs);
                e.Strikes.Enqueue(now);
                if (e.Strikes.Count < StrikesForBan || now < e.BannedUntil) return false;
                e.Strikes.Clear();
                e.BannedUntil = now + _banMs;
                return true;
            }
        }

        // An admin block, for the given time (loopback is still never limited).
        public void BanFor(IPAddress address, long durationMs, long now)
        {
            lock (_gate)
            {
                Entry e = Get(address);
                e.BannedUntil = Math.Max(e.BannedUntil, now + durationMs);
            }
        }

        public bool IsBanned(IPAddress address, long now)
        {
            lock (_gate)
            {
                Entry e;
                return _entries.TryGetValue(address, out e) && now < e.BannedUntil;
            }
        }

        public int Tracked
        {
            get { lock (_gate) return _entries.Count; }
        }

        // Forgets addresses with nothing recent: no ban, no attempts or strikes inside their windows.
        public void Prune(long now)
        {
            lock (_gate)
            {
                var idle = new List<IPAddress>();
                foreach (KeyValuePair<IPAddress, Entry> pair in _entries)
                {
                    Entry e = pair.Value;
                    Trim(e.Attempts, now - WindowMs);
                    Trim(e.Strikes, now - StrikeWindowMs);
                    if (now >= e.BannedUntil && e.Attempts.Count == 0 && e.Strikes.Count == 0) idle.Add(pair.Key);
                }
                foreach (IPAddress address in idle) _entries.Remove(address);
            }
        }

        private Entry Get(IPAddress address)
        {
            Entry e;
            if (_entries.TryGetValue(address, out e)) return e;
            if (_entries.Count >= MaxTracked) PruneOldestHalf();
            e = new Entry();
            _entries[address] = e;
            return e;
        }

        // Under a very wide flood, drop unbanned entries rather than grow without bound.
        private void PruneOldestHalf()
        {
            var drop = new List<IPAddress>();
            foreach (KeyValuePair<IPAddress, Entry> pair in _entries)
            {
                if (pair.Value.BannedUntil == 0) drop.Add(pair.Key);
                if (drop.Count >= MaxTracked / 2) break;
            }
            foreach (IPAddress address in drop) _entries.Remove(address);
        }

        private static void Trim(Queue<long> times, long before)
        {
            while (times.Count > 0 && times.Peek() <= before) times.Dequeue();
        }
    }
}
