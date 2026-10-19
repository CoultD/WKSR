using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace WKRace.Server
{
    // One player's row on a time board: their fastest finished run of that kind.
    public sealed class BoardRun
    {
        // BoardKind.DuelTime or BoardKind.SoloTime.
        public int Board;
        public Guid PlayerId;
        public int TimeMs;
        public int Seed;
        public int Rules;
        // The artifacts banned in the run, comma separated ("" for none).
        public string Bans = "";
        // The WKRace version the run was played on, and the build tag the player's client sent.
        public string ModVersion = "";
        public string GameBuild = "";
        public long SetUnixMs;
        // The run's replay is kept (the 20 fastest of a board).
        public bool HasReplay;
    }

    // The leaderboards' tables: the time boards' rows (with the replays of the fastest), and which players have
    // played a ranked match since the boards exist (only they are listed on the rating board).
    public sealed partial class ServerDatabase
    {
        private void CreateVersion4(SqliteTransaction tx)
        {
            Execute(@"
CREATE TABLE IF NOT EXISTS board_runs (
    board        INTEGER NOT NULL,
    player_id    TEXT NOT NULL,
    time_ms      INTEGER NOT NULL,
    seed         INTEGER NOT NULL,
    rules        INTEGER NOT NULL,
    bans         TEXT NOT NULL,
    mod_version  TEXT NOT NULL,
    game_build   TEXT NOT NULL,
    set_ms       INTEGER NOT NULL,
    -- The run's recording (a packed run file), kept only for the fastest runs of a board.
    replay       BLOB,
    PRIMARY KEY (board, player_id)
);
CREATE TABLE IF NOT EXISTS board_players (
    player_id    TEXT PRIMARY KEY,
    since_ms     INTEGER NOT NULL
);", tx);
        }

        public List<BoardRun> LoadBoardRuns()
        {
            var runs = new List<BoardRun>();
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT board, player_id, time_ms, seed, rules, bans, mod_version, game_build, set_ms, replay IS NOT NULL FROM board_runs";
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            runs.Add(new BoardRun
                            {
                                Board = r.GetInt32(0),
                                PlayerId = Guid.Parse(r.GetString(1)),
                                TimeMs = r.GetInt32(2),
                                Seed = r.GetInt32(3),
                                Rules = r.GetInt32(4),
                                Bans = r.GetString(5),
                                ModVersion = r.GetString(6),
                                GameBuild = r.GetString(7),
                                SetUnixMs = r.GetInt64(8),
                                HasReplay = r.GetInt64(9) != 0,
                            });
                        }
                    }
                }
            }
            return runs;
        }

        // Writes a player's row (replacing the one they had). replay: the packed recording to keep with it, or null.
        public void SaveBoardRun(BoardRun run, byte[] replay)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO board_runs (board, player_id, time_ms, seed, rules, bans, mod_version, game_build, set_ms, replay)
VALUES ($board, $id, $time, $seed, $rules, $bans, $mod, $build, $set, $replay)
ON CONFLICT (board, player_id) DO UPDATE SET time_ms = $time, seed = $seed, rules = $rules, bans = $bans,
    mod_version = $mod, game_build = $build, set_ms = $set, replay = $replay";
                    cmd.Parameters.AddWithValue("$board", run.Board);
                    cmd.Parameters.AddWithValue("$id", run.PlayerId.ToString());
                    cmd.Parameters.AddWithValue("$time", run.TimeMs);
                    cmd.Parameters.AddWithValue("$seed", run.Seed);
                    cmd.Parameters.AddWithValue("$rules", run.Rules);
                    cmd.Parameters.AddWithValue("$bans", run.Bans ?? "");
                    cmd.Parameters.AddWithValue("$mod", run.ModVersion ?? "");
                    cmd.Parameters.AddWithValue("$build", run.GameBuild ?? "");
                    cmd.Parameters.AddWithValue("$set", run.SetUnixMs);
                    cmd.Parameters.AddWithValue("$replay", (object)replay ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Drops the replay kept with a row (the run is no longer among a board's fastest).
        public void ClearBoardReplay(int board, Guid player)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "UPDATE board_runs SET replay = NULL WHERE board = $board AND player_id = $id";
                    cmd.Parameters.AddWithValue("$board", board);
                    cmd.Parameters.AddWithValue("$id", player.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // The packed replay kept with a row, or null.
        public byte[] LoadBoardReplay(int board, Guid player)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT replay FROM board_runs WHERE board = $board AND player_id = $id";
                    cmd.Parameters.AddWithValue("$board", board);
                    cmd.Parameters.AddWithValue("$id", player.ToString());
                    object value = cmd.ExecuteScalar();
                    return value as byte[];
                }
            }
        }

        // board < 0: the player's rows on every board (the player is being deleted).
        public void DeleteBoardRuns(int board, Guid player)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = board < 0 ? "DELETE FROM board_runs WHERE player_id = $id" : "DELETE FROM board_runs WHERE board = $board AND player_id = $id";
                    cmd.Parameters.AddWithValue("$board", board);
                    cmd.Parameters.AddWithValue("$id", player.ToString());
                    cmd.ExecuteNonQuery();
                }
                if (board >= 0) return;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM board_players WHERE player_id = $id";
                    cmd.Parameters.AddWithValue("$id", player.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public HashSet<Guid> LoadBoardPlayers()
        {
            var players = new HashSet<Guid>();
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT player_id FROM board_players";
                    using (SqliteDataReader r = cmd.ExecuteReader())
                        while (r.Read()) players.Add(Guid.Parse(r.GetString(0)));
                }
            }
            return players;
        }

        public void AddBoardPlayer(Guid player, long sinceUnixMs)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO board_players (player_id, since_ms) VALUES ($id, $since)";
                    cmd.Parameters.AddWithValue("$id", player.ToString());
                    cmd.Parameters.AddWithValue("$since", sinceUnixMs);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
