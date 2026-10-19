using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace WKRace.Server
{
    // One mod the server has seen in a player's game. Approved until someone marks it otherwise; the mark is on the
    // mod's id, so it covers every version.
    public sealed class ModRecord
    {
        public string Id = "";
        public string Name = "";
        // The version last reported.
        public string Version = "";
        public bool Approved = true;
        public long FirstSeenMs;
        public long LastSeenMs;
    }

    // The mods list's tables: the mods seen (not who had them), and the players who may mark them from WKSR.
    public sealed partial class ServerDatabase
    {
        private void CreateVersion5(SqliteTransaction tx)
        {
            Execute(@"
CREATE TABLE IF NOT EXISTS mods (
    -- The plugin's GUID in lower case.
    id            TEXT PRIMARY KEY,
    shown_id      TEXT NOT NULL,
    name          TEXT NOT NULL,
    version       TEXT NOT NULL,
    approved      INTEGER NOT NULL DEFAULT 1,
    first_seen_ms INTEGER NOT NULL,
    last_seen_ms  INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS mod_managers (
    player_id     TEXT PRIMARY KEY
);", tx);
        }

        // Every mod seen, by its id in lower case.
        public Dictionary<string, ModRecord> LoadMods()
        {
            var mods = new Dictionary<string, ModRecord>(StringComparer.Ordinal);
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, shown_id, name, version, approved, first_seen_ms, last_seen_ms FROM mods";
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            mods[r.GetString(0)] = new ModRecord
                            {
                                Id = r.GetString(1),
                                Name = r.GetString(2),
                                Version = r.GetString(3),
                                Approved = r.GetInt64(4) != 0,
                                FirstSeenMs = r.GetInt64(5),
                                LastSeenMs = r.GetInt64(6),
                            };
                        }
                    }
                }
            }
            return mods;
        }

        public void SaveMod(string key, ModRecord mod)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO mods (id, shown_id, name, version, approved, first_seen_ms, last_seen_ms)
VALUES ($id, $shown, $name, $version, $approved, $first, $last)
ON CONFLICT (id) DO UPDATE SET shown_id = $shown, name = $name, version = $version, approved = $approved, last_seen_ms = $last";
                    cmd.Parameters.AddWithValue("$id", key);
                    cmd.Parameters.AddWithValue("$shown", mod.Id ?? "");
                    cmd.Parameters.AddWithValue("$name", mod.Name ?? "");
                    cmd.Parameters.AddWithValue("$version", mod.Version ?? "");
                    cmd.Parameters.AddWithValue("$approved", mod.Approved ? 1 : 0);
                    cmd.Parameters.AddWithValue("$first", mod.FirstSeenMs);
                    cmd.Parameters.AddWithValue("$last", mod.LastSeenMs);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void DeleteMod(string key)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM mods WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", key);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public HashSet<Guid> LoadModManagers()
        {
            var managers = new HashSet<Guid>();
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT player_id FROM mod_managers";
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            Guid id;
                            if (Guid.TryParse(r.GetString(0), out id)) managers.Add(id);
                        }
                    }
                }
            }
            return managers;
        }

        public void SetModManager(Guid player, bool manager)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = manager ? "INSERT OR IGNORE INTO mod_managers (player_id) VALUES ($id)" : "DELETE FROM mod_managers WHERE player_id = $id";
                    cmd.Parameters.AddWithValue("$id", player.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
