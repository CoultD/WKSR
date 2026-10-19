using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    // The mods list: every mod is approved until one is marked; a game with a mod that is not approved gets no
    // ranked play, and only lobbies whose host allows it.
    public class ModTests
    {
        private static readonly ModEntry Speed = Mod("com.x.speed", "Speed Hack");
        private static readonly ModEntry Hud = Mod("com.y.hud", "Nice HUD");

        private static ModEntry Mod(string id, string name, string version = "1.0.0")
        {
            return new ModEntry { Id = id, Name = name, Version = version };
        }

        // The player's game reports these mods (a new list replaces the one before).
        private static void Report(Harness h, TestClient c, params ModEntry[] mods)
        {
            h.Advance(600);
            foreach (LoadedMods part in LoadedMods.Split(mods)) c.Send(part);
        }

        // Someone's game shows up with the mod, and the admin marks it.
        private static void Mark(Harness h, ModEntry mod)
        {
            TestClient seen = h.Login("Seen" + mod.Name.Replace(" ", ""));
            Report(h, seen, mod);
            Assert.Contains("NOT approved", h.Core.RunAdmin(new[] { "mods", "unapprove", mod.Id }));
            seen.Disconnect();
        }

        [Fact]
        public void EveryModIsApproved_UntilOneIsMarked()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Assert.False(a.Last<ModVerdict>().AnyMarked);   // sent on sign-in

                Report(h, a, Speed, Hud);
                ModVerdict verdict = a.Last<ModVerdict>();
                Assert.False(verdict.AnyMarked);
                Assert.False(verdict.Manager);
                Assert.Equal(0, verdict.Unapproved);
                Assert.False(h.Core.AnyModMarked);

                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
                Assert.Equal(0, a.Conn.Count<ErrorMessage>());

                string list = h.Core.RunAdmin(new[] { "mods", "list", "all" });
                Assert.Contains("2 mods seen, 0 not approved", list);
                Assert.Contains("Speed Hack", list);
                Assert.Contains("com.y.hud", list);
            }
        }

        [Fact]
        public void AMarkedMod_KeepsItsPlayerOutOfRanked_UntilItIsGone()
        {
            using (var h = new Harness())
            {
                Mark(h, Speed);
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Report(h, a, Speed, Hud);
                Report(h, b, Hud);

                ModVerdict verdict = a.Last<ModVerdict>();
                Assert.True(verdict.AnyMarked);
                Assert.Equal(1, verdict.Unapproved);
                Assert.Equal(new[] { "Speed Hack" }, verdict.Names);
                Assert.True(b.Last<ModVerdict>().AnyMarked);
                Assert.Equal(0, b.Last<ModVerdict>().Unapproved);

                a.Send(new QueueJoin());
                ErrorMessage refused = a.Last<ErrorMessage>();
                Assert.Equal(ErrorCode.ModsNotApproved, refused.Code);
                Assert.Contains("Speed Hack", refused.Text);
                Assert.False(a.Last<QueueStatus>().Queued);

                a.Send(new SoloStart());
                Assert.Equal(ErrorCode.ModsNotApproved, a.Last<ErrorMessage>().Code);
                Assert.Contains("Ranked solo runs", a.Last<ErrorMessage>().Text);
                Assert.Equal(0, h.Core.SoloRunCount);

                // The other player is not matched with them.
                b.Send(new QueueJoin());
                Assert.Equal(0, b.Conn.Count<MatchFound>());

                // Without the mod they are in again. (The mark is on the mod, whatever its version.)
                Report(h, a, Mod("COM.X.Speed", "Speed Hack", "2.0.0"));
                Assert.Equal(1, a.Last<ModVerdict>().Unapproved);
                Report(h, a, Hud);
                Assert.Equal(0, a.Last<ModVerdict>().Unapproved);
                a.Send(new QueueJoin());
                Assert.Equal(1, a.Conn.Count<MatchFound>());
                Assert.Equal(1, b.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void AMark_TakesPlayersOutOfTheQueueAndOutOfARankedSoloRun()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Report(h, a, Speed);
                Report(h, b, Speed);
                a.Send(new QueueJoin());
                b.Send(new SoloStart());
                Assert.True(a.Last<QueueStatus>().Queued);
                Assert.Equal(1, h.Core.SoloRunCount);

                h.Core.RunAdmin(new[] { "mods", "unapprove", "Speed Hack" });

                Assert.False(a.Last<QueueStatus>().Queued);
                Assert.Equal(ErrorCode.ModsNotApproved, a.Last<ErrorMessage>().Code);
                Assert.Equal(0, h.Core.SoloRunCount);
                Assert.Equal(1, b.Conn.Count<MatchResult>());
                Assert.Equal(ErrorCode.ModsNotApproved, b.Last<ErrorMessage>().Code);

                // Approved again: nothing stands in the way.
                Assert.Contains("now approved", h.Core.RunAdmin(new[] { "mods", "approve", "com.x.speed" }));
                Assert.False(h.Core.AnyModMarked);
                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
            }
        }

        [Fact]
        public void ARankedMatch_EndsForThePlayerWhoseModIsNotApproved()
        {
            using (var h = new Harness())
            {
                Mark(h, Speed);

                // Their game shows up with the mod after the match was made, before it started: no rating change.
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                Assert.Equal(1, a.Conn.Count<MatchFound>());
                Report(h, a, Speed);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Aborted, b.Result.Outcome);
                Assert.Equal(ErrorCode.ModsNotApproved, a.Last<ErrorMessage>().Code);
            }
            using (var h = new Harness())
            {
                // Marked while the match is running: that player forfeits.
                TestClient a, b;
                h.StartMatch(out a, out b);
                Report(h, a, Speed);
                h.Core.RunAdmin(new[] { "mods", "unapprove", "com.x.speed" });
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
            }
        }

        [Fact]
        public void Lobbies_TakeUnapprovedMods_OnlyWhenTheHostAllowsIt()
        {
            using (var h = new Harness())
            {
                Mark(h, Speed);
                TestClient host = h.Login("Alpha");
                Report(h, host, Speed);

                // Their own lobby needs the box.
                host.Send(new LobbyCreate { Mode = MatchMode.Party });
                Assert.Equal(ErrorCode.ModsNotApproved, host.Last<ErrorMessage>().Code);
                Assert.Contains("ALLOW UNAPPROVED MODS", host.Last<ErrorMessage>().Text);
                Assert.Equal(0, host.Conn.Count<LobbyCreated>());

                host.Send(new LobbyCreate { Mode = MatchMode.Party, AllowUnapproved = true });
                Assert.True(host.Lobby.AllowUnapproved);
                TestClient clean = h.Login("Bravo");
                clean.Send(new LobbyJoin { LobbyId = host.Lobby.LobbyId });
                Assert.Equal(2, host.Lobby.Members.Count);
                Assert.True(clean.Lobby.AllowUnapproved);

                // A lobby without the box: a player with the mod is not let in, and one inside whose game turns up
                // with it is put out.
                TestClient strict = h.Login("Charlie");
                strict.Send(new LobbyCreate { Mode = MatchMode.Party });
                Assert.False(strict.Lobby.AllowUnapproved);
                TestClient modded = h.Login("Delta");
                Report(h, modded, Speed);
                modded.Send(new LobbyJoin { LobbyId = strict.Lobby.LobbyId });
                Assert.Equal(ErrorCode.ModsNotApproved, modded.Last<ErrorMessage>().Code);
                Assert.Single(strict.Lobby.Members);

                TestClient late = h.Login("Echo");
                late.Send(new LobbyJoin { LobbyId = strict.Lobby.LobbyId });
                Assert.Equal(2, strict.Lobby.Members.Count);
                Report(h, late, Speed);
                Assert.Contains("does not allow", late.Last<LobbyClosed>().Reason);
                Assert.Single(strict.Lobby.Members);
            }
        }

        [Fact]
        public void TheManager_MarksModsFromWksr_AndNobodyElseCan()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Report(h, a, Speed, Hud);
                Assert.Contains("can now mark", h.Core.RunAdmin(new[] { "mods", "manager", "add", "Alpha" }));
                Assert.True(a.Last<ModVerdict>().Manager);
                Assert.False(b.Last<ModVerdict>().Manager);

                b.Send(new ModMark { Id = Speed.Id, Approved = false });
                Assert.Contains("manager", b.Last<ErrorMessage>().Text);
                Assert.False(h.Core.AnyModMarked);

                a.Send(new ModMark { Id = Speed.Id, Approved = false });
                Assert.True(h.Core.AnyModMarked);
                Assert.Equal(1, a.Last<ModVerdict>().Unapproved);
                Assert.True(b.Last<ModVerdict>().AnyMarked);

                // The manager sees every mod: not approved first, with who has it.
                h.Advance(300);
                a.Send(new ModsRequest());
                ModsPage page = a.Last<ModsPage>();
                Assert.Equal(2, page.Total);
                Assert.Equal(1, page.NotApproved);
                Assert.Equal(new[] { "Speed Hack", "Nice HUD" }, page.Rows.Select(r => r.Mod.Name));
                Assert.False(page.Rows[0].Approved);
                Assert.True(page.Rows[0].Yours);
                Assert.Equal(1, page.Rows[0].Players);
                Assert.True(page.Rows[1].Approved);

                // Anyone else sees the mods that are not approved, and their own.
                Report(h, b, Mod("com.z.other", "Other Mod", "3.1"));
                b.Send(new ModsRequest());
                page = b.Last<ModsPage>();
                Assert.Equal(new[] { "Speed Hack", "Other Mod" }, page.Rows.Select(r => r.Mod.Name));
                Assert.False(page.Rows[0].Yours);
                Assert.True(page.Rows[1].Yours);
                Assert.Equal("3.1", page.Rows[1].Mod.Version);
                Assert.Equal(0, page.Rows[0].Players);

                // A manager can also be named when the server starts (for a test server).
                h.Options.ModsManagers.Add("Bravo");
                b.Send(new ModMark { Id = Speed.Id, Approved = true });
                Assert.False(h.Core.AnyModMarked);

                Assert.Contains("can no longer mark", h.Core.RunAdmin(new[] { "mods", "manager", "remove", "Alpha" }));
                Assert.False(a.Last<ModVerdict>().Manager);
            }
        }

        [Fact]
        public void TheList_SurvivesARestartOfTheServer()
        {
            using (var h = new Harness())
            {
                Mark(h, Speed);
                h.Core.RunAdmin(new[] { "mods", "manager", "add", "SeenSpeedHack" });

                var again = new ServerCore(h.Options, h.Store, new MatchLog(h.Database), () => h.Now, null);
                Assert.True(again.AnyModMarked);
                var conn = new FakeConnection();
                again.OnMessage(conn, new TestClient(h, "Bravo").Hello());
                foreach (LoadedMods part in LoadedMods.Split(new[] { Mod("com.x.speed", "Renamed", "9.9") })) again.OnMessage(conn, part);
                ModVerdict verdict = conn.Last<ModVerdict>();
                Assert.Equal(1, verdict.Unapproved);
                Assert.Equal("Speed Hack", verdict.Names.Single());   // the name first reported stays
                Assert.Contains("SeenSpeedHack", again.RunAdmin(new[] { "mods", "manager", "list" }));

                // Forgetting a marked mod lifts the mark.
                Assert.Contains("Forgot Speed Hack", again.RunAdmin(new[] { "mods", "forget", "com.x.speed" }));
                Assert.False(again.AnyModMarked);
                Assert.Equal(0, conn.Last<ModVerdict>().Unapproved);
            }
        }

        [Fact]
        public void AReportedList_IsCleanedUp_AndALongOneCountsAsUnapproved()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                // WK Race itself, a doubled id and odd characters.
                Report(h, a, Mod("com.wkrace.mod", "White Knuckle Race"), Mod("com.a.b", "Na\u0007meé"), Mod("COM.A.B", "Again"), Mod("", "No id"));
                string list = h.Core.RunAdmin(new[] { "mods", "list", "all" });
                Assert.Contains("1 mod seen", list);
                Assert.Contains("Na?me?", list);
                Assert.DoesNotContain("White Knuckle Race", list);

                // More mods than a list holds: fine while nothing is marked, not approved once something is.
                var many = new List<ModEntry>();
                for (int i = 0; i < LoadedMods.MaxMods + 5; i++) many.Add(Mod("com.many.m" + i, "Many " + i));
                TestClient b = h.Login("Bravo");
                Report(h, b, many.ToArray());
                Assert.Equal(0, b.Last<ModVerdict>().Unapproved);
                Mark(h, Speed);
                ModVerdict verdict = b.Last<ModVerdict>();
                Assert.Equal(1, verdict.Unapproved);
                Assert.Contains("more mods than can be listed", verdict.Names.Single());

                // A list sent out of order is not a list.
                TestClient c = h.Login("Charlie");
                h.Advance(600);
                c.Send(new LoadedMods { Part = 1, Parts = 2, Mods = new List<ModEntry> { Speed } });
                Assert.Equal(0, c.Last<ModVerdict>().Unapproved);
            }
        }
    }
}
