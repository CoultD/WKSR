using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    public class BugReportTests
    {
        private static BugReport Sample(TestClient c)
        {
            var r = new BugReport
            {
                PlayerId = c.Id,
                Token = c.Token,
                Title = "Ghost stuck",
                Description = "The ghost stays at the start of the silo after a reset.",
                Contact = "someone@example.org",
                Info = "WKSR 0.6.2",
                Log = Encoding.UTF8.GetBytes("12:00:00  Connected"),
            };
            r.Attachments.Add(new BugAttachment { Name = "shot 1.png", Data = new byte[] { 1, 2, 3 } });
            return r;
        }

        [Fact]
        public void Format_RoundTrips()
        {
            var c = new TestClient(null, "Alpha");
            BugReport back = BugReport.FromBytes(Sample(c).ToBytes());
            Assert.Equal(c.Id, back.PlayerId);
            Assert.Equal(c.Token, back.Token);
            Assert.Equal("Ghost stuck", back.Title);
            Assert.Equal("someone@example.org", back.Contact);
            Assert.Equal("shot 1.png", back.Attachments.Single().Name);
            Assert.Equal(new byte[] { 1, 2, 3 }, back.Attachments.Single().Data);
            Assert.Equal("12:00:00  Connected", Encoding.UTF8.GetString(back.Log));
        }

        [Fact]
        public void Format_RefusesBadFilesAndSizes()
        {
            var c = new TestClient(null, "Alpha");
            BugReport r = Sample(c);
            r.Attachments.Add(new BugAttachment { Name = "virus.exe", Data = new byte[] { 0 } });
            Assert.Throws<ProtocolException>(() => BugReport.FromBytes(r.ToBytes()));

            r = Sample(c);
            r.Attachments[0].Data = new byte[BugReport.MaxFileBytes + 1];
            Assert.Throws<ProtocolException>(() => BugReport.FromBytes(r.ToBytes()));

            r = Sample(c);
            r.Attachments.Clear();
            for (int i = 0; i < 2; i++) r.Attachments.Add(new BugAttachment { Name = i + ".png", Data = new byte[BugReport.MaxFileBytes] });
            Assert.Throws<ProtocolException>(() => BugReport.FromBytes(r.ToBytes()));   // 30 MB in total

            byte[] body = Sample(c).ToBytes();
            Assert.Throws<ProtocolException>(() => BugReport.FromBytes(body.Take(body.Length - 1).ToArray()));
            Assert.Throws<ProtocolException>(() => BugReport.FromBytes(body.Concat(new byte[] { 0 }).ToArray()));
        }

        [Fact]
        public void Server_TakesReportsOnlyFromKnownPlayers_WithinLimits()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Assert.True(h.Core.CheckReport(a.Id, a.Token).Ok);
                Assert.Equal("Alpha", h.Core.CheckReport(a.Id, a.Token).PlayerName);

                Assert.Equal(403, h.Core.CheckReport(a.Id, new byte[ProtocolConstants.TokenLength]).Status);   // wrong token
                Assert.Equal(403, h.Core.CheckReport(Guid.NewGuid(), a.Token).Status);                          // unknown player

                for (int i = 2; i < ServerCore.ReportsPerPlayerPerDay; i++) Assert.True(h.Core.CheckReport(a.Id, a.Token).Ok);
                Assert.Equal(429, h.Core.CheckReport(a.Id, a.Token).Status);
                h.Advance(24L * 60 * 60 * 1000);
                Assert.True(h.Core.CheckReport(a.Id, a.Token).Ok);

                h.Core.RunAdmin(new[] { "ban", "Alpha" });
                Assert.Equal(403, h.Core.CheckReport(a.Id, a.Token).Status);
            }
        }

        [Fact]
        public void Spool_WritesACompleteFolderForTheMailer()
        {
            string dir = Path.Combine(Path.GetTempPath(), "wkrace-spool-" + Guid.NewGuid().ToString("N"));
            try
            {
                var c = new TestClient(null, "Alpha");
                BugReport r = Sample(c);
                r.Title = "Two\r\nlines";
                var spool = new ReportSpool(dir);
                string id = spool.Write(r, "Alpha", new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc));
                string folder = Path.Combine(dir, "reports", id);
                Assert.True(Directory.Exists(folder));
                Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "reports"), ".*"));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(folder, "01-shot_1.png")));
                Assert.True(File.Exists(Path.Combine(folder, "wksr-event-log.txt")));

                using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "report.json"))))
                {
                    JsonElement root = json.RootElement;
                    Assert.Equal("Two  lines", root.GetProperty("title").GetString());   // no header injection in the email subject
                    Assert.Equal("Alpha", root.GetProperty("player").GetString());
                    Assert.Equal(c.Id.ToString(), root.GetProperty("playerId").GetString());
                    Assert.Equal(new[] { "01-shot_1.png", "wksr-event-log.txt" }, root.GetProperty("files").EnumerateArray().Select(e => e.GetString()));
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Spool_SafeNames()
        {
            Assert.Equal("my_shot.png", ReportSpool.SafeName("my shot.PNG"));
            Assert.Equal("passwd.txt", ReportSpool.SafeName("../../etc/passwd.txt"));
            Assert.Equal("file.png", ReportSpool.SafeName("éé.png"));
        }

        [Fact]
        public void WebPage_PassesTheUploadToTheServer()
        {
            byte[] received = null;
            var web = new SteamWeb(0, "https://race.example.org", n => Task.FromResult(false),
                (n, id) => Task.FromResult(new SteamLinkOutcome()), f => Task.FromResult(false), null, null,
                body => { received = body; return Task.FromResult(new WebReply { Status = 200, Text = "Report sent." }); });
            using (var cts = new CancellationTokenSource())
            {
                web.Start(cts.Token);
                byte[] body = new byte[300000];
                new Random(1).NextBytes(body);
                string reply = Post(web.Port, "/report", body);
                Assert.StartsWith("HTTP/1.1 200", reply);
                Assert.Contains("text/plain", reply);
                Assert.EndsWith("Report sent.", reply);
                Assert.Equal(body, received);

                Assert.StartsWith("HTTP/1.1 413", Post(web.Port, "/report", null, BugReport.MaxBodyBytes + 1L));
                Assert.StartsWith("HTTP/1.1 400", Post(web.Port, "/steam/return", new byte[3]));
                cts.Cancel();
            }
        }

        // Sends a POST; with no body, only the head (claiming contentLength bytes).
        private static string Post(int port, string target, byte[] body, long contentLength = -1)
        {
            using (var tcp = new TcpClient("127.0.0.1", port))
            {
                NetworkStream s = tcp.GetStream();
                long length = body != null ? body.Length : contentLength;
                byte[] head = Encoding.ASCII.GetBytes("POST " + target + " HTTP/1.1\r\nHost: race.example.org\r\nContent-Length: " + length + "\r\n\r\n");
                s.Write(head, 0, head.Length);
                if (body != null) s.Write(body, 0, body.Length);
                var response = new StringBuilder();
                var buffer = new byte[8192];
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) response.Append(Encoding.UTF8.GetString(buffer, 0, n));
                return response.ToString();
            }
        }
    }
}
