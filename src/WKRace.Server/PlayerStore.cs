using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WKRace.Server
{
    // All players, kept in memory and written to the database as soon as they change, so a crash loses nothing.
    public sealed class PlayerStore
    {
        private readonly object _gate = new object();
        private readonly ServerDatabase _db;
        private readonly Dictionary<Guid, PlayerRecord> _players = new Dictionary<Guid, PlayerRecord>();
        // Records whose save failed, retried by Flush.
        private readonly HashSet<Guid> _unsaved = new HashSet<Guid>();

        public PlayerStore(ServerDatabase db)
        {
            _db = db;
            foreach (PlayerRecord p in db.LoadPlayers()) _players[p.Id] = p;
        }

        public ServerDatabase Database { get { return _db; } }

        // Names are unique ignoring case and spacing. Returns the player holding this name, other than `except`.
        public PlayerRecord FindByName(string name, PlayerRecord except = null)
        {
            string key = NameKey(name);
            lock (_gate)
            {
                foreach (PlayerRecord p in _players.Values)
                    if (p != except && NameKey(p.Name) == key) return p;
                return null;
            }
        }

        public static string NameKey(string name)
        {
            return string.Join(" ", (name ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public List<PlayerRecord> All()
        {
            lock (_gate) return new List<PlayerRecord>(_players.Values);
        }

        public PlayerRecord FindBySteam(string steamHash)
        {
            if (string.IsNullOrEmpty(steamHash)) return null;
            lock (_gate)
            {
                foreach (PlayerRecord p in _players.Values) if (p.SteamHash == steamHash) return p;
                return null;
            }
        }

        // Removes a player for good (an empty identity replaced by restoring a linked one).
        public void Remove(PlayerRecord record)
        {
            lock (_gate)
            {
                _players.Remove(record.Id);
                _unsaved.Remove(record.Id);
            }
            _db.DeletePlayer(record.Id);
        }

        public int Count
        {
            get { lock (_gate) return _players.Count; }
        }

        public static string HashToken(byte[] token)
        {
            return Convert.ToBase64String(SHA256.HashData(token));
        }

        public static bool TokenMatches(string expectedHash, string actualHash)
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHash ?? ""), Encoding.ASCII.GetBytes(actualHash ?? ""));
        }

        public PlayerRecord Find(Guid id)
        {
            lock (_gate)
            {
                PlayerRecord record;
                return _players.TryGetValue(id, out record) ? record : null;
            }
        }

        public PlayerRecord Add(PlayerRecord record)
        {
            lock (_gate)
            {
                if (record.CreatedUnixMs == 0) record.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _players[record.Id] = record;
            }
            Save(record);
            return record;
        }

        // Call after changing a record: it is written right away.
        public void MarkDirty(PlayerRecord record)
        {
            Save(record);
        }

        // Writes anything a failed save left behind (called periodically and at shutdown).
        public void Flush()
        {
            var retry = new List<PlayerRecord>();
            lock (_gate)
            {
                if (_unsaved.Count == 0) return;
                foreach (Guid id in _unsaved)
                {
                    PlayerRecord p;
                    if (_players.TryGetValue(id, out p)) retry.Add(p);
                }
                _unsaved.Clear();
            }
            foreach (PlayerRecord p in retry) Save(p);
        }

        private void Save(PlayerRecord record)
        {
            try
            {
                _db.SavePlayers(new[] { record });
            }
            catch (Exception)
            {
                // Disk full or similar: keep it in memory and try again at the next Flush.
                lock (_gate) _unsaved.Add(record.Id);
            }
        }
    }

    public sealed class MatchLog
    {
        private readonly ServerDatabase _db;

        public MatchLog(ServerDatabase db)
        {
            _db = db;
        }

        public void Append(MatchEntry entry)
        {
            _db.AddMatch(entry);
        }

        // [{"item":"...","by":"..."}] for a match's artifact bans.
        public static string BansJson(List<string> banned, List<string> bannedBy)
        {
            return JsonSerializer.Serialize(banned.Select((item, i) => new { item, by = bannedBy[i] }));
        }
    }
}
