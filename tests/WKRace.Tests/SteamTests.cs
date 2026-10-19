using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    public class SteamOpenIdTests
    {
        private const string Public = "https://race.example.org";
        private const ulong SteamId = 76561198000000042;

        // A positive assertion as Steam sends it back, for the given nonce.
        internal static Dictionary<string, string> Assertion(string nonce, ulong steamId = SteamId, string returnTo = null)
        {
            string claimed = "https://steamcommunity.com/openid/id/" + steamId;
            return new Dictionary<string, string>
            {
                { "n", nonce },
                { "openid.ns", "http://specs.openid.net/auth/2.0" },
                { "openid.mode", "id_res" },
                { "openid.op_endpoint", "https://steamcommunity.com/openid/login" },
                { "openid.claimed_id", claimed },
                { "openid.identity", claimed },
                { "openid.return_to", returnTo ?? SteamOpenId.ReturnUrl(Public, nonce) },
                { "openid.response_nonce", "2026-10-07T18:00:00Zabcdef" },
                { "openid.assoc_handle", "1234567890" },
                { "openid.signed", "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle" },
                { "openid.sig", "c2lnbmF0dXJl" },
            };
        }

        [Fact]
        public void LoginUrl_SendsPlayersToSteamAndBackToUs()
        {
            string url = SteamOpenId.LoginUrl(Public, "abc");
            Assert.StartsWith("https://steamcommunity.com/openid/login?", url);
            Dictionary<string, string> q = SteamOpenId.ParseQuery(new Uri(url).Query);
            Assert.Equal("checkid_setup", q["openid.mode"]);
            Assert.Equal("https://race.example.org/steam/return?n=abc", q["openid.return_to"]);
            Assert.Equal("https://race.example.org/", q["openid.realm"]);
        }

        [Fact]
        public void Validate_AcceptsAProperAssertion()
        {
            string error;
            Assert.Equal(SteamId, SteamOpenId.Validate(Assertion("abc"), SteamOpenId.ReturnUrl(Public, "abc"), out error));
            Assert.Null(error);
        }

        [Fact]
        public void Validate_RejectsAnythingElse()
        {
            string expected = SteamOpenId.ReturnUrl(Public, "abc");
            string error;

            // An assertion made for another site, or for another sign-in.
            Assert.Equal(0ul, SteamOpenId.Validate(Assertion("abc", returnTo: "https://evil.example/steam/return?n=abc"), expected, out error));
            Assert.Equal(0ul, SteamOpenId.Validate(Assertion("abc", returnTo: SteamOpenId.ReturnUrl(Public, "other")), expected, out error));

            Dictionary<string, string> q = Assertion("abc");
            q["openid.op_endpoint"] = "https://evil.example/openid/login";
            Assert.Equal(0ul, SteamOpenId.Validate(q, expected, out error));

            q = Assertion("abc");
            q["openid.claimed_id"] = q["openid.identity"] = "https://evil.example/openid/id/76561198000000042";
            Assert.Equal(0ul, SteamOpenId.Validate(q, expected, out error));

            q = Assertion("abc");
            q["openid.signed"] = "signed,op_endpoint,identity,return_to";   // claimed_id not signed
            Assert.Equal(0ul, SteamOpenId.Validate(q, expected, out error));

            q = Assertion("abc");
            q["openid.mode"] = "cancel";
            Assert.Equal(0ul, SteamOpenId.Validate(q, expected, out error));
            Assert.Contains("cancel", error);
        }

        [Fact]
        public void CheckAuthentication_ResendsTheFieldsWithTheModeChanged()
        {
            List<KeyValuePair<string, string>> form = SteamOpenId.CheckAuthenticationForm(Assertion("abc"));
            Assert.DoesNotContain(form, p => p.Key == "n");
            Assert.Contains(form, p => p.Key == "openid.mode" && p.Value == "check_authentication");
            Assert.Contains(form, p => p.Key == "openid.sig" && p.Value == "c2lnbmF0dXJl");
            Assert.True(SteamOpenId.IsValidResponse("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n"));
            Assert.False(SteamOpenId.IsValidResponse("ns:http://specs.openid.net/auth/2.0\nis_valid:false\n"));
        }
    }

    public class SteamLinkTests
    {
        private const ulong SteamA = 76561198000000001;
        private const ulong SteamB = 76561198000000002;

        private static Harness SteamHarness()
        {
            var h = new Harness();
            h.Options.SteamPublicUrl = "https://race.example.org";
            return h;
        }

        private static string StartLink(TestClient c)
        {
            c.Send(new SteamLinkStart());
            string url = c.Last<SteamLinkUrl>().Url;
            string returnTo = SteamOpenId.ParseQuery(new Uri(url).Query)["openid.return_to"];
            return SteamOpenId.ParseQuery(new Uri(returnTo).Query)["n"];
        }

        // Stores the new token the way the client does.
        private static SteamLinkResult Accept(TestClient c)
        {
            SteamLinkResult r = c.Last<SteamLinkResult>();
            if (r.Ok)
            {
                c.Id = r.PlayerId;
                c.Token = r.Token;
            }
            return r;
        }

        [Fact]
        public void Welcome_SaysWhetherSteamIsLinkedAndRequired()
        {
            using (Harness h = SteamHarness())
            {
                ServerWelcome w = h.Login("Alpha").Last<ServerWelcome>();
                Assert.True(w.SteamAvailable);
                Assert.False(w.SteamLinked);
                Assert.True(w.SteamRequiredForRanked);
            }
        }

        [Fact]
        public void Ranked_NeedsALinkedAccount_LobbiesDoNot()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new QueueJoin());
                Assert.Equal(ErrorCode.SteamLinkRequired, a.Last<ErrorMessage>().Code);
                Assert.False(h.Core.IsSteamLinkPending("nothing"));

                a.Send(new LobbyCreate());
                Assert.Equal(1, a.Conn.Count<LobbyCreated>());
            }
        }

        [Fact]
        public void Linking_StoresAHashAndRenewsTheToken()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                byte[] oldToken = a.Token;
                string nonce = StartLink(a);
                Assert.True(h.Core.IsSteamLinkPending(nonce));

                SteamLinkOutcome outcome = h.Core.CompleteSteamLink(nonce, SteamA);
                Assert.True(outcome.Ok);
                SteamLinkResult r = Accept(a);
                Assert.True(r.Ok);
                Assert.Equal(ProtocolConstants.TokenLength, r.Token.Length);

                PlayerRecord record = h.Store.Find(a.Id);
                Assert.NotNull(record.SteamHash);
                Assert.DoesNotContain(SteamA.ToString(), record.SteamHash);
                Assert.False(h.Core.IsSteamLinkPending(nonce));   // one use only

                // Ranked works now, and only the new token signs in.
                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
                var stolen = new TestClient(h, "Alpha") { Id = a.Id, Token = oldToken };
                stolen.Login();
                Assert.Equal(ErrorCode.BadToken, stolen.Last<ErrorMessage>().Code);
                a.Disconnect();
                a.Reconnect();
                Assert.True(a.Last<ServerWelcome>().SteamLinked);
            }
        }

        [Fact]
        public void TheGamesSteamAccount_LinksThePlayerWithoutAButton()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                byte[] token = a.Token;
                a.Send(new QueueJoin());
                Assert.Equal(ErrorCode.SteamLinkRequired, a.Last<ErrorMessage>().Code);

                // The game says which Steam account it runs under: linked, and the key on this PC stays as it is.
                a.Send(new SteamIdentity { SteamId = SteamA });
                SteamLinkResult r = a.Last<SteamLinkResult>();
                Assert.True(r.Ok);
                Assert.Empty(r.Token);
                Assert.NotNull(h.Store.Find(a.Id).SteamHash);
                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
                a.Send(new QueueLeave());

                // Said again (every start of the game): nothing happens, nothing is sent.
                int results = a.Conn.Count<SteamLinkResult>();
                h.Advance(5000);
                a.Send(new SteamIdentity { SteamId = SteamA });
                Assert.Equal(results, a.Conn.Count<SteamLinkResult>());
                a.Disconnect();
                a.Reconnect();
                Assert.True(a.Last<ServerWelcome>().SteamLinked);
                Assert.Equal(token, a.Token);

                // Another Steam account does not take a player that is linked already.
                h.Advance(5000);
                a.Send(new SteamIdentity { SteamId = SteamB });
                Assert.False(a.Last<SteamLinkResult>().Ok);
            }
        }

        [Fact]
        public void TheGamesSteamAccount_BringsThePlayerBackOnANewPc()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new SteamIdentity { SteamId = SteamA });
                h.Store.Find(a.Id).Rating = 1234;
                Guid original = a.Id;

                // The same Steam account on a new PC: its fresh, empty player is replaced by the real one.
                TestClient pc2 = h.Login("NewPc");
                Guid empty = pc2.Id;
                pc2.Send(new SteamIdentity { SteamId = SteamA });
                SteamLinkResult r = Accept(pc2);
                Assert.True(r.Ok);
                Assert.Equal(original, r.PlayerId);
                Assert.Equal("Alpha", r.Name);
                Assert.Equal(ProtocolConstants.TokenLength, r.Token.Length);
                Assert.Null(h.Store.Find(empty));
                pc2.Reconnect();
                Assert.Equal(1234, pc2.Last<ServerWelcome>().Rating);

                // The first PC's key no longer signs in (it starts over and comes back through Steam the same way).
                Assert.Equal(ErrorCode.BadToken, a.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void WithoutSteamOnTheServer_TheGamesAccountIsIgnored()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new SteamIdentity { SteamId = SteamA });
                Assert.Equal(0, a.Conn.Count<SteamLinkResult>());
                Assert.Null(h.Store.Find(a.Id).SteamHash);
            }
        }

        [Fact]
        public void SigningInOnANewPc_RestoresTheLinkedPlayer()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                h.Core.CompleteSteamLink(StartLink(a), SteamA);
                Accept(a);
                h.Store.Find(a.Id).Rating = 1234;
                Guid original = a.Id;

                // Same person, new PC: a fresh identity, then Steam sign-in with the same account.
                TestClient pc2 = h.Login("NewPc");
                Guid empty = pc2.Id;
                SteamLinkOutcome outcome = h.Core.CompleteSteamLink(StartLink(pc2), SteamA);
                Assert.True(outcome.Ok);
                SteamLinkResult r = Accept(pc2);
                Assert.Equal(original, r.PlayerId);
                Assert.Equal("Alpha", r.Name);
                Assert.Equal(1234, r.Rating);
                Assert.True(pc2.Conn.Closed);                 // reconnects as the restored player
                Assert.Null(h.Store.Find(empty));             // the empty identity is gone
                Assert.Equal(ErrorCode.BadToken, a.Last<ErrorMessage>().Code);   // the other PC is signed out

                pc2.Reconnect();
                ServerWelcome w = pc2.Last<ServerWelcome>();
                Assert.Equal(original, w.PlayerId);
                Assert.Equal(1234, w.Rating);
            }
        }

        [Fact]
        public void APlayerWithRankedResults_IsNeverReplaced()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                h.Core.CompleteSteamLink(StartLink(a), SteamA);
                Accept(a);

                TestClient b = h.Login("Bravo");
                h.Store.Find(b.Id).Wins = 2;
                SteamLinkOutcome outcome = h.Core.CompleteSteamLink(StartLink(b), SteamA);
                Assert.False(outcome.Ok);
                Assert.False(b.Last<SteamLinkResult>().Ok);
                Assert.NotNull(h.Store.Find(b.Id));
                Assert.Contains("Alpha", outcome.Message);
            }
        }

        [Fact]
        public void APlayerKeepsTheSteamAccountItWasLinkedTo()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                h.Core.CompleteSteamLink(StartLink(a), SteamA);
                Accept(a);
                SteamLinkOutcome outcome = h.Core.CompleteSteamLink(StartLink(a), SteamB);
                Assert.False(outcome.Ok);
                Assert.Contains("different Steam account", outcome.Message);
            }
        }

        [Fact]
        public void SignInLinks_Expire()
        {
            using (Harness h = SteamHarness())
            {
                TestClient a = h.Login("Alpha");
                string nonce = StartLink(a);
                h.Advance(11 * 60 * 1000);
                Assert.False(h.Core.CompleteSteamLink(nonce, SteamA).Ok);
                Assert.Null(h.Store.Find(a.Id).SteamHash);
            }
        }

        [Fact]
        public void SteamOptional_LetsUnlinkedPlayersRaceRanked()
        {
            using (Harness h = SteamHarness())
            {
                h.Options.SteamOptional = true;
                TestClient a = h.Login("Alpha");
                Assert.False(a.Last<ServerWelcome>().SteamRequiredForRanked);
                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
            }
        }

        [Fact]
        public void SignInPage_ChecksWithSteamAndShowsTheOutcome()
        {
            bool verified = false;
            var web = new SteamWeb(0, "https://race.example.org",
                nonce => Task.FromResult(nonce == "good"),
                (nonce, steamId) => Task.FromResult(new SteamLinkOutcome { Ok = true, Message = "Linked <" + steamId + ">" }),
                form => { verified = form.Any(p => p.Key == "openid.mode" && p.Value == "check_authentication"); return Task.FromResult(true); },
                null);
            using (var cts = new CancellationTokenSource())
            {
                web.Start(cts.Token);
                Dictionary<string, string> q = SteamOpenIdTests.Assertion("good", returnTo: "https://race.example.org/steam/return?n=good");
                string query = string.Join("&", q.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

                string page = Get(web.Port, "/steam/return?" + query);
                Assert.StartsWith("HTTP/1.1 200", page);
                Assert.True(verified);
                Assert.Contains("Linked &lt;76561198000000042&gt;", page);   // escaped

                Assert.Contains("expired", Get(web.Port, "/steam/return?n=unknown"));
                Assert.StartsWith("HTTP/1.1 404", Get(web.Port, "/admin"));
                cts.Cancel();
            }
        }

        [Fact]
        public void PrivacyPage_IsServedWithTheEscapedContact()
        {
            var web = new SteamWeb(0, "https://race.example.org", n => Task.FromResult(false),
                (n, id) => Task.FromResult(new SteamLinkOutcome()), f => Task.FromResult(false), null,
                PrivacyNotice.Html("ops <at> example.org", true));
            using (var cts = new CancellationTokenSource())
            {
                web.Start(cts.Token);
                string page = Get(web.Port, "/privacy");
                Assert.StartsWith("HTTP/1.1 200", page);
                Assert.Contains("21 days", page);
                Assert.Contains("ops &lt;at&gt; example.org", page);
                Assert.Contains("which ranked matches require", page);
                cts.Cancel();
            }
        }

        private static string Get(int port, string target)
        {
            using (var tcp = new TcpClient("127.0.0.1", port))
            {
                NetworkStream s = tcp.GetStream();
                byte[] request = Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\nHost: race.example.org\r\n\r\n");
                s.Write(request, 0, request.Length);
                var response = new StringBuilder();
                var buffer = new byte[8192];
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) response.Append(Encoding.UTF8.GetString(buffer, 0, n));
                return response.ToString();
            }
        }
    }
}
