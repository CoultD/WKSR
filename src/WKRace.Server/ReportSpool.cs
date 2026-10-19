using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Bug reports waiting to be emailed: one folder per report under <data>/reports, holding report.json and the
    // attached files. The VPS mailer (wkrace-mail) sends each folder and deletes it. A folder appears only when it is
    // complete (written under a dot name, then renamed).
    internal sealed class ReportSpool
    {
        // Bounds the disk used if reports cannot be mailed for a while.
        public const long MaxSpoolBytes = 500L * 1024 * 1024;
        public const int MaxSpoolReports = 300;

        private readonly string _dir;

        public ReportSpool(string dataDir)
        {
            _dir = Path.Combine(dataDir, "reports");
        }

        public string Directory { get { return _dir; } }

        // Returns the report's id, or null when the spool is full.
        public string Write(BugReport report, string playerName, DateTime receivedUtc)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var existing = new DirectoryInfo(_dir).GetDirectories();
            long used = existing.Sum(d => d.EnumerateFiles().Sum(f => f.Length));
            if (existing.Length >= MaxSpoolReports || used + report.AttachmentBytes() + report.Log.Length > MaxSpoolBytes) return null;

            string id = receivedUtc.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            string temp = Path.Combine(_dir, "." + id);
            System.IO.Directory.CreateDirectory(temp);
            try
            {
                var files = new System.Collections.Generic.List<string>();
                for (int i = 0; i < report.Attachments.Count; i++)
                {
                    string name = (i + 1).ToString("00") + "-" + SafeName(report.Attachments[i].Name);
                    File.WriteAllBytes(Path.Combine(temp, name), report.Attachments[i].Data);
                    files.Add(name);
                }
                if (report.Log.Length > 0)
                {
                    File.WriteAllBytes(Path.Combine(temp, "wksr-event-log.txt"), report.Log);
                    files.Add("wksr-event-log.txt");
                }
                var meta = new
                {
                    id,
                    received = receivedUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
                    player = playerName,
                    playerId = report.PlayerId.ToString(),
                    title = OneLine(report.Title),
                    description = report.Description,
                    contact = OneLine(report.Contact),
                    info = report.Info,
                    files,
                };
                File.WriteAllText(Path.Combine(temp, "report.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                System.IO.Directory.Move(temp, Path.Combine(_dir, id));
                return id;
            }
            catch
            {
                try { System.IO.Directory.Delete(temp, true); } catch (IOException) { }
                throw;
            }
        }

        // Letters, digits, dot, dash and underscore only; the extension (already checked) is kept.
        internal static string SafeName(string name)
        {
            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(name ?? "");
            var b = new StringBuilder();
            foreach (char c in stem)
                b.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') ? c : '_');
            string s = b.ToString().Trim('.', '_');
            if (s.Length == 0) s = "file";
            if (s.Length > 60) s = s.Substring(0, 60);
            return s + ext;
        }

        private static string OneLine(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
