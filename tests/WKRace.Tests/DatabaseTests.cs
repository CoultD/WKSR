using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    public class DatabaseTests : IDisposable
    {
        private const string Key = "test-key-0123456789abcdef";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wkrace-db-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static PlayerRecord Player(string name)
        {
            return new PlayerRecord { Id = Guid.NewGuid(), Name = name, TokenHash = "hash-" + name, Rating = 1234, Wins = 3, Losses = 1 };
        }

        [Fact]
        public void EncryptedDatabase_IsUnreadableWithoutTheKey()
        {
            PlayerRecord p = Player("SecretClimber");
            using (var db = new ServerDatabase(_dir, Key, null))
            {
                var store = new PlayerStore(db);
                store.Add(p);
                store.Flush();
                Assert.True(db.Encrypted);
            }
            SqliteConnection.ClearAllPools();

            // Neither the SQLite header nor any stored text is visible in the file.
            byte[] raw = File.ReadAllBytes(Path.Combine(_dir, ServerDatabase.FileName));
            string text = Encoding.ASCII.GetString(raw);
            Assert.DoesNotContain("SQLite format 3", text);
            Assert.DoesNotContain("SecretClimber", text);
            Assert.DoesNotContain("SecretClimber", Encoding.ASCII.GetString(ReadIfExists(ServerDatabase.FileName + "-wal")));

            Assert.Throws<InvalidOperationException>(() => new ServerDatabase(_dir, null, null));
            Assert.Throws<InvalidOperationException>(() => new ServerDatabase(_dir, "the-wrong-key-0123456789", null));

            using (var db = new ServerDatabase(_dir, Key, null))
            {
                PlayerRecord back = new PlayerStore(db).Find(p.Id);
                Assert.Equal("SecretClimber", back.Name);
                Assert.Equal(1234, back.Rating);
                Assert.True(back.CreatedUnixMs > 0);
            }
        }

        [Fact]
        public void Snapshot_IsACompleteEncryptedCopy()
        {
            PlayerRecord p = Player("Snap");
            using (var db = new ServerDatabase(_dir, Key, null))
            {
                var store = new PlayerStore(db);
                store.Add(p);
                store.Flush();
                db.WriteSnapshot();
            }
            SqliteConnection.ClearAllPools();

            string snapshotDir = Path.Combine(_dir, "restore");
            Directory.CreateDirectory(snapshotDir);
            File.Copy(Path.Combine(_dir, ServerDatabase.SnapshotName), Path.Combine(snapshotDir, ServerDatabase.FileName));
            Assert.DoesNotContain("Snap", Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(snapshotDir, ServerDatabase.FileName))));
            using (var restored = new ServerDatabase(snapshotDir, Key, null))
            {
                Assert.Equal("Snap", new PlayerStore(restored).Find(p.Id).Name);
            }
        }

        [Fact]
        public void LegacyJsonFiles_AreImportedOnce()
        {
            Directory.CreateDirectory(_dir);
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), match = Guid.NewGuid();
            File.WriteAllText(Path.Combine(_dir, "players.json"),
                "[{\"Id\":\"" + a + "\",\"Name\":\"Old\",\"TokenHash\":\"h\",\"Rating\":1050,\"Wins\":5,\"Losses\":2,\"BestTimeMs\":600000,\"LastSeenUnixMs\":1700000000000}," +
                "{\"Id\":\"" + b + "\",\"Name\":\"Older\",\"TokenHash\":\"h2\",\"Rating\":950,\"Wins\":0,\"Losses\":4,\"BestTimeMs\":-1,\"LastSeenUnixMs\":1700000000000}]");
            File.WriteAllText(Path.Combine(_dir, "matches.jsonl"),
                "﻿{\"matchId\":\"" + match + "\",\"seed\":1234567,\"ranked\":true,\"endedUtc\":\"2026-10-04T06:17:24Z\",\"reason\":\"Old finished first.\",\"aborted\":false," +
                "\"worldHashes\":\"0 0\",\"bans\":[{\"item\":\"Item_Artifact_Rapier\",\"by\":\"Old\"}]," +
                "\"playerA\":{\"id\":\"" + a + "\",\"name\":\"Old\",\"outcome\":\"Win\",\"timeMs\":600000,\"rating\":1050}," +
                "\"playerB\":{\"id\":\"" + b + "\",\"name\":\"Older\",\"outcome\":\"Loss\",\"timeMs\":-1,\"rating\":950}}\n");

            using (var db = new ServerDatabase(_dir, Key, null))
            {
                var store = new PlayerStore(db);
                Assert.Equal(2, store.Count);
                Assert.Equal(1050, store.Find(a).Rating);
                Assert.Equal(600000, store.Find(a).BestTimeMs);
                MatchEntry m = db.LoadMatches().Single();
                Assert.Equal(match, m.Id);
                Assert.Contains("Item_Artifact_Rapier", m.BansJson);
                Assert.Equal("Loss", m.B.Outcome);
            }
            Assert.False(File.Exists(Path.Combine(_dir, "players.json")));
            Assert.False(File.Exists(Path.Combine(_dir, "matches.jsonl")));
            Assert.Single(Directory.GetFiles(_dir, "players.json.imported-*"));

            // Opening again imports nothing twice.
            using (var db = new ServerDatabase(_dir, Key, null))
            {
                Assert.Equal(2, new PlayerStore(db).Count);
                Assert.Equal(1, db.MatchCount());
            }
        }

        [Fact]
        public void FinishedMatches_AreStoredWithTheirBans()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                h.Options.RankedRules = MatchRules.Ranked;
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Send(new BanReady { MatchId = a.Match.MatchId, Pool = new System.Collections.Generic.List<string> { "Item_Artifact_Rapier", "Item_Artifact_Remote" } });
                b.Send(new BanReady { MatchId = b.Match.MatchId, Pool = new System.Collections.Generic.List<string> { "Item_Artifact_Rapier", "Item_Artifact_Remote" } });
                TestClient first = a.Last<BanState>().YourTurn ? a : b;
                TestClient second = first == a ? b : a;
                first.Send(new BanPick { MatchId = first.Match.MatchId, Item = "Item_Artifact_Rapier" });
                second.Send(new BanPick { MatchId = second.Match.MatchId, Item = "" });
                a.Ready();
                b.Ready();
                h.Advance(h.Options.CountdownMs);
                a.Send(new PlayerForfeit());

                MatchEntry m = h.Database.LoadMatches().Single();
                Assert.Equal(a.Match.MatchId, m.Id);
                Assert.True(m.Ranked);
                Assert.Equal("Loss", m.A.Outcome == "Loss" ? m.A.Outcome : m.B.Outcome);
                Assert.Contains("Item_Artifact_Rapier", m.BansJson);
                Assert.Contains(first.Name, m.BansJson);
            }
        }

        private byte[] ReadIfExists(string name)
        {
            string path = Path.Combine(_dir, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : new byte[0];
        }
    }
}
