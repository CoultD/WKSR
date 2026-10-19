using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WKRace.Server
{
    // The server's data in one SQLite file (wkrace.db). With a key, the whole file is encrypted by SQLite3 Multiple
    // Ciphers (ChaCha20-Poly1305): a copy of the file, or of a backup, is unreadable without the key, which lives
    // outside the data directory. Without a key the file is plain SQLite (for local testing and self-hosting).
    //
    // One connection, used from the server's core thread; the lock only guards against stray use from elsewhere.
    public sealed partial class ServerDatabase : IDisposable
    {
        public const string FileName = "wkrace.db";
        public const string SnapshotName = "wkrace-snapshot.db";
        private const int SchemaVersion = 5;

        private readonly object _gate = new object();
        private readonly SqliteConnection _db;
        private readonly string _key;
        private readonly Action<string> _log;

        public ServerDatabase(string directory, string key, Action<string> log)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            _key = string.IsNullOrEmpty(key) ? null : key;
            _log = log ?? (s => { });
            Path = System.IO.Path.Combine(directory, FileName);

            try
            {
                // Opening with a wrong key, or the first read without one, fails for an encrypted file.
                _db = Open(Path, _key);
                Scalar("SELECT count(*) FROM sqlite_master");
            }
            catch (SqliteException e)
            {
                if (_db != null) _db.Dispose();
                throw new InvalidOperationException(
                    "Cannot open " + Path + ": " + (_key == null ? "it is encrypted and no key was given" : "wrong key, or not a WK Race database") + " (" + e.Message + ")", e);
            }
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA synchronous=NORMAL");
            Migrate();
            ImportLegacyFiles();
        }

        public string Directory { get; private set; }
        public string Path { get; private set; }
        public bool Encrypted { get { return _key != null; } }

        public void Dispose()
        {
            lock (_gate) _db.Dispose();
        }

        // ---- schema ---------------------------------------------------------------------------------------

        private void Migrate()
        {
            long version = (long)Scalar("PRAGMA user_version");
            if (version >= SchemaVersion) return;
            using (SqliteTransaction tx = _db.BeginTransaction())
            {
                if (version < 1) CreateVersion1(tx);
                if (version < 2) Execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)", tx);
                if (version < 3)
                {
                    Execute("ALTER TABLE players ADD COLUMN banned INTEGER NOT NULL DEFAULT 0", tx);
                    Execute("ALTER TABLE players ADD COLUMN ban_reason TEXT", tx);
                    Execute("ALTER TABLE players ADD COLUMN name_locked INTEGER NOT NULL DEFAULT 0", tx);
                }
                if (version < 4) CreateVersion4(tx);
                if (version < 5) CreateVersion5(tx);
                Execute("PRAGMA user_version=" + SchemaVersion, tx);
                tx.Commit();
            }
        }

        private void CreateVersion1(SqliteTransaction tx)
        {
            Execute(@"
CREATE TABLE IF NOT EXISTS players (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    token_hash    TEXT NOT NULL,
    rating        INTEGER NOT NULL,
    wins          INTEGER NOT NULL,
    losses        INTEGER NOT NULL,
    best_time_ms  INTEGER NOT NULL,
    created_ms    INTEGER NOT NULL,
    last_seen_ms  INTEGER NOT NULL,
    -- Keyed hash of the linked Steam account (Steam sign-in); never the Steam ID itself.
    steam_hash    TEXT UNIQUE
);
CREATE TABLE IF NOT EXISTS matches (
    id            TEXT PRIMARY KEY,
    seed          INTEGER NOT NULL,
    ranked        INTEGER NOT NULL,
    ended_ms      INTEGER NOT NULL,
    reason        TEXT NOT NULL,
    aborted       INTEGER NOT NULL,
    world_hashes  TEXT NOT NULL,
    bans          TEXT,
    a_id TEXT NOT NULL, a_name TEXT NOT NULL, a_outcome TEXT NOT NULL, a_time_ms INTEGER NOT NULL, a_rating INTEGER NOT NULL,
    b_id TEXT NOT NULL, b_name TEXT NOT NULL, b_outcome TEXT NOT NULL, b_time_ms INTEGER NOT NULL, b_rating INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS matches_ended ON matches (ended_ms);", tx);
        }

        // ---- settings kept with the data ---------------------------------------------------------------

        public string GetMeta(string key)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
                    cmd.Parameters.AddWithValue("$key", key);
                    return cmd.ExecuteScalar() as string;
                }
            }
        }

        public void SetMeta(string key, string value)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = $value";
                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.Parameters.AddWithValue("$value", value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ---- players --------------------------------------------------------------------------------------

        public List<PlayerRecord> LoadPlayers()
        {
            var players = new List<PlayerRecord>();
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, name, token_hash, rating, wins, losses, best_time_ms, created_ms, last_seen_ms, steam_hash, banned, ban_reason, name_locked FROM players";
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            players.Add(new PlayerRecord
                            {
                                Id = Guid.Parse(r.GetString(0)),
                                Name = r.GetString(1),
                                TokenHash = r.GetString(2),
                                Rating = r.GetInt32(3),
                                Wins = r.GetInt32(4),
                                Losses = r.GetInt32(5),
                                BestTimeMs = r.GetInt32(6),
                                CreatedUnixMs = r.GetInt64(7),
                                LastSeenUnixMs = r.GetInt64(8),
                                SteamHash = r.IsDBNull(9) ? null : r.GetString(9),
                                Banned = r.GetInt64(10) != 0,
                                BanReason = r.IsDBNull(11) ? null : r.GetString(11),
                                NameLocked = r.GetInt64(12) != 0,
                            });
                        }
                    }
                }
            }
            return players;
        }

        public void SavePlayers(IReadOnlyCollection<PlayerRecord> players)
        {
            if (players.Count == 0) return;
            lock (_gate)
            {
                using (SqliteTransaction tx = _db.BeginTransaction())
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO players (id, name, token_hash, rating, wins, losses, best_time_ms, created_ms, last_seen_ms, steam_hash, banned, ban_reason, name_locked)
VALUES ($id, $name, $token, $rating, $wins, $losses, $best, $created, $seen, $steam, $banned, $reason, $locked)
ON CONFLICT (id) DO UPDATE SET name = $name, token_hash = $token, rating = $rating, wins = $wins, losses = $losses,
    best_time_ms = $best, last_seen_ms = $seen, steam_hash = $steam, banned = $banned, ban_reason = $reason, name_locked = $locked";
                    SqliteParameter id = cmd.Parameters.Add("$id", SqliteType.Text);
                    SqliteParameter name = cmd.Parameters.Add("$name", SqliteType.Text);
                    SqliteParameter token = cmd.Parameters.Add("$token", SqliteType.Text);
                    SqliteParameter rating = cmd.Parameters.Add("$rating", SqliteType.Integer);
                    SqliteParameter wins = cmd.Parameters.Add("$wins", SqliteType.Integer);
                    SqliteParameter losses = cmd.Parameters.Add("$losses", SqliteType.Integer);
                    SqliteParameter best = cmd.Parameters.Add("$best", SqliteType.Integer);
                    SqliteParameter created = cmd.Parameters.Add("$created", SqliteType.Integer);
                    SqliteParameter seen = cmd.Parameters.Add("$seen", SqliteType.Integer);
                    SqliteParameter steam = cmd.Parameters.Add("$steam", SqliteType.Text);
                    SqliteParameter banned = cmd.Parameters.Add("$banned", SqliteType.Integer);
                    SqliteParameter reason = cmd.Parameters.Add("$reason", SqliteType.Text);
                    SqliteParameter locked = cmd.Parameters.Add("$locked", SqliteType.Integer);
                    foreach (PlayerRecord p in players)
                    {
                        id.Value = p.Id.ToString();
                        name.Value = p.Name ?? "";
                        token.Value = p.TokenHash ?? "";
                        rating.Value = p.Rating;
                        wins.Value = p.Wins;
                        losses.Value = p.Losses;
                        best.Value = p.BestTimeMs;
                        created.Value = p.CreatedUnixMs;
                        seen.Value = p.LastSeenUnixMs;
                        steam.Value = (object)p.SteamHash ?? DBNull.Value;
                        banned.Value = p.Banned ? 1 : 0;
                        reason.Value = (object)p.BanReason ?? DBNull.Value;
                        locked.Value = p.NameLocked ? 1 : 0;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        public void DeletePlayer(Guid id)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM players WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            // A deleted player is on no leaderboard any more (and their replays go with their rows).
            DeleteBoardRuns(-1, id);
            SetModManager(id, false);
        }

        // ---- matches --------------------------------------------------------------------------------------

        public void AddMatch(MatchEntry m)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    InsertMatch(cmd, m);
                }
            }
        }

        // Erasure: the player's matches stay (their opponents' history), but without their id or name.
        public void AnonymizeMatches(Guid id, string name)
        {
            lock (_gate)
            {
                using (SqliteTransaction tx = _db.BeginTransaction())
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$id", id.ToString());
                    cmd.Parameters.AddWithValue("$none", Guid.Empty.ToString());
                    cmd.Parameters.AddWithValue("$by", System.Text.Json.JsonSerializer.Serialize(name ?? ""));
                    cmd.CommandText = @"
UPDATE matches SET bans = replace(bans, '""by"":' || $by, '""by"":""deleted player""') WHERE (a_id = $id OR b_id = $id) AND bans IS NOT NULL;
UPDATE matches SET a_id = $none, a_name = 'deleted player' WHERE a_id = $id;
UPDATE matches SET b_id = $none, b_name = 'deleted player' WHERE b_id = $id;";
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }
            }
        }

        // Merging players: matches of one now belong to the other.
        public void ReassignMatches(Guid from, Guid to)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.Parameters.AddWithValue("$from", from.ToString());
                    cmd.Parameters.AddWithValue("$to", to.ToString());
                    cmd.CommandText = "UPDATE matches SET a_id = $to WHERE a_id = $from; UPDATE matches SET b_id = $to WHERE b_id = $from;";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public int MatchCount()
        {
            return (int)(long)Scalar("SELECT count(*) FROM matches");
        }

        public List<MatchEntry> LoadMatches()
        {
            var list = new List<MatchEntry>();
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = @"SELECT id, seed, ranked, ended_ms, reason, aborted, world_hashes, bans,
a_id, a_name, a_outcome, a_time_ms, a_rating, b_id, b_name, b_outcome, b_time_ms, b_rating FROM matches ORDER BY ended_ms";
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new MatchEntry
                            {
                                Id = Guid.Parse(r.GetString(0)),
                                Seed = r.GetInt32(1),
                                Ranked = r.GetInt64(2) != 0,
                                EndedUnixMs = r.GetInt64(3),
                                Reason = r.GetString(4),
                                Aborted = r.GetInt64(5) != 0,
                                WorldHashes = r.GetString(6),
                                BansJson = r.IsDBNull(7) ? null : r.GetString(7),
                                A = new MatchSide { Id = Guid.Parse(r.GetString(8)), Name = r.GetString(9), Outcome = r.GetString(10), TimeMs = r.GetInt32(11), Rating = r.GetInt32(12) },
                                B = new MatchSide { Id = Guid.Parse(r.GetString(13)), Name = r.GetString(14), Outcome = r.GetString(15), TimeMs = r.GetInt32(16), Rating = r.GetInt32(17) },
                            });
                        }
                    }
                }
            }
            return list;
        }

        private static void InsertMatch(SqliteCommand cmd, MatchEntry m)
        {
            cmd.CommandText = @"
INSERT OR IGNORE INTO matches (id, seed, ranked, ended_ms, reason, aborted, world_hashes, bans,
    a_id, a_name, a_outcome, a_time_ms, a_rating, b_id, b_name, b_outcome, b_time_ms, b_rating)
VALUES ($id, $seed, $ranked, $ended, $reason, $aborted, $hashes, $bans,
    $aid, $aname, $aout, $atime, $arating, $bid, $bname, $bout, $btime, $brating)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", m.Id.ToString());
            cmd.Parameters.AddWithValue("$seed", m.Seed);
            cmd.Parameters.AddWithValue("$ranked", m.Ranked ? 1 : 0);
            cmd.Parameters.AddWithValue("$ended", m.EndedUnixMs);
            cmd.Parameters.AddWithValue("$reason", m.Reason ?? "");
            cmd.Parameters.AddWithValue("$aborted", m.Aborted ? 1 : 0);
            cmd.Parameters.AddWithValue("$hashes", m.WorldHashes ?? "");
            cmd.Parameters.AddWithValue("$bans", (object)m.BansJson ?? DBNull.Value);
            AddSide(cmd, "$a", m.A);
            AddSide(cmd, "$b", m.B);
            cmd.ExecuteNonQuery();
        }

        private static void AddSide(SqliteCommand cmd, string prefix, MatchSide side)
        {
            cmd.Parameters.AddWithValue(prefix + "id", side.Id.ToString());
            cmd.Parameters.AddWithValue(prefix + "name", side.Name ?? "");
            cmd.Parameters.AddWithValue(prefix + "out", side.Outcome ?? "");
            cmd.Parameters.AddWithValue(prefix + "time", side.TimeMs);
            cmd.Parameters.AddWithValue(prefix + "rating", side.Rating);
        }

        // ---- backups --------------------------------------------------------------------------------------

        // A consistent copy of the whole database (encrypted with the same key) for the backup job, which must not
        // copy the live file mid-write. Written next to the database and swapped in when complete.
        public void WriteSnapshot()
        {
            string target = System.IO.Path.Combine(Directory, SnapshotName);
            string temp = target + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            lock (_gate)
            {
                using (SqliteConnection copy = Open(temp, _key))
                {
                    _db.BackupDatabase(copy);
                }
            }
            SqliteConnection.ClearAllPools();
            File.Move(temp, target, true);
        }

        // ---- the JSON files of earlier versions ---------------------------------------------------------

        // players.json and matches.jsonl from before the database are imported once, then renamed so they are not
        // imported again. They are plain text: delete them once the import has been checked.
        private void ImportLegacyFiles()
        {
            string playersPath = System.IO.Path.Combine(Directory, "players.json");
            string matchesPath = System.IO.Path.Combine(Directory, "matches.jsonl");
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            if (File.Exists(playersPath))
            {
                var players = JsonSerializer.Deserialize<List<PlayerRecord>>(File.ReadAllText(playersPath)) ?? new List<PlayerRecord>();
                foreach (PlayerRecord p in players) if (p.CreatedUnixMs == 0) p.CreatedUnixMs = p.LastSeenUnixMs;
                SavePlayers(players);
                File.Move(playersPath, playersPath + ".imported-" + stamp);
                _log("imported " + players.Count + " players from players.json (renamed; delete it once the import is checked: it is not encrypted)");
            }

            if (File.Exists(matchesPath))
            {
                int count = 0;
                lock (_gate)
                {
                    using (SqliteTransaction tx = _db.BeginTransaction())
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        foreach (string line in File.ReadLines(matchesPath))
                        {
                            MatchEntry m = ParseLegacyMatch(line);
                            if (m == null) continue;
                            InsertMatch(cmd, m);
                            count++;
                        }
                        tx.Commit();
                    }
                }
                File.Move(matchesPath, matchesPath + ".imported-" + stamp);
                _log("imported " + count + " matches from matches.jsonl (renamed; delete it once the import is checked: it is not encrypted)");
            }
        }

        private static MatchEntry ParseLegacyMatch(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(line.Trim('﻿')))
                {
                    JsonElement e = doc.RootElement;
                    JsonElement bans;
                    return new MatchEntry
                    {
                        Id = e.GetProperty("matchId").GetGuid(),
                        Seed = e.GetProperty("seed").GetInt32(),
                        Ranked = !e.TryGetProperty("ranked", out JsonElement ranked) || ranked.GetBoolean(),
                        EndedUnixMs = e.GetProperty("endedUtc").GetDateTimeOffset().ToUnixTimeMilliseconds(),
                        Reason = e.GetProperty("reason").GetString(),
                        Aborted = e.GetProperty("aborted").GetBoolean(),
                        WorldHashes = e.TryGetProperty("worldHashes", out JsonElement hashes) ? hashes.GetString() : "",
                        BansJson = e.TryGetProperty("bans", out bans) && bans.ValueKind == JsonValueKind.Array ? bans.GetRawText() : null,
                        A = LegacySide(e.GetProperty("playerA")),
                        B = LegacySide(e.GetProperty("playerB")),
                    };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MatchSide LegacySide(JsonElement e)
        {
            return new MatchSide
            {
                Id = e.GetProperty("id").GetGuid(),
                Name = e.GetProperty("name").GetString(),
                Outcome = e.GetProperty("outcome").GetString(),
                TimeMs = e.GetProperty("timeMs").GetInt32(),
                Rating = e.GetProperty("rating").GetInt32(),
            };
        }

        // ---- helpers -------------------------------------------------------------------------------------

        private static SqliteConnection Open(string path, string key)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            if (key != null) builder.Password = key;
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }

        private object Scalar(string sql)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = sql;
                    return cmd.ExecuteScalar();
                }
            }
        }

        private void Execute(string sql, SqliteTransaction tx = null)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
