using System;
using System.Collections.Generic;
using System.IO;

namespace WKRace.Protocol
{
    // A bug report from WKSR. It is too big for the race connection, so the client posts it over HTTPS to
    // https://<server>/report as one binary body in this format. The player's id and token sign it: the server only
    // accepts reports from known players.
    public sealed class BugReport
    {
        public const byte FormatVersion = 1;
        public const int MaxTitle = 120;
        public const int MaxDescription = 8000;
        public const int MaxContact = 120;
        public const int MaxInfo = 2000;
        public const int MaxFileName = 100;
        public const int MaxAttachments = 10;
        // Screenshots in total. Mail servers limit messages to about 25 MB after encoding, so the server's mailer splits
        // bigger reports over several emails.
        public const int MaxAttachmentBytes = 20 * 1024 * 1024;
        // One file must fit in one email (about 25 MB once encoded).
        public const int MaxFileBytes = 15 * 1024 * 1024;
        public const int MaxLogBytes = 1024 * 1024;
        // The largest valid body, with room for the text fields.
        public const int MaxBodyBytes = MaxAttachmentBytes + MaxLogBytes + 256 * 1024;

        public static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".txt", ".log" };

        public Guid PlayerId;
        public byte[] Token = new byte[ProtocolConstants.TokenLength];
        public string Title = "";
        public string Description = "";
        // Optional: how to reach the player for follow-up questions.
        public string Contact = "";
        // Versions and build, filled in by WKSR.
        public string Info = "";
        // WKSR's event log for the session, when the player ticks it (UTF-8 text).
        public byte[] Log = new byte[0];
        public List<BugAttachment> Attachments = new List<BugAttachment>();

        public static bool IsAllowedFile(string name)
        {
            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            return Array.IndexOf(AllowedExtensions, ext) >= 0;
        }

        public byte[] ToBytes()
        {
            var w = new PacketWriter(64 * 1024);
            w.WriteByte(FormatVersion);
            w.WriteGuid(PlayerId);
            w.WriteFixedBytes(Token, ProtocolConstants.TokenLength);
            w.WriteString(Title, MaxTitle);
            w.WriteString(Description, MaxDescription);
            w.WriteString(Contact, MaxContact);
            w.WriteString(Info, MaxInfo);
            WriteBlob(w, Log);
            w.WriteByte((byte)Attachments.Count);
            foreach (BugAttachment a in Attachments)
            {
                w.WriteString(a.Name, MaxFileName);
                WriteBlob(w, a.Data);
            }
            return w.ToArray();
        }

        // Throws ProtocolException when the body is malformed or over a limit.
        public static BugReport FromBytes(byte[] data)
        {
            var r = new PacketReader(data, 0, data.Length);
            if (r.ReadByte() != FormatVersion) throw new ProtocolException("Unknown report format");
            var report = new BugReport
            {
                PlayerId = r.ReadGuid(),
                Token = r.ReadFixedBytes(ProtocolConstants.TokenLength),
                Title = r.ReadString(MaxTitle),
                Description = r.ReadString(MaxDescription),
                Contact = r.ReadString(MaxContact),
                Info = r.ReadString(MaxInfo),
                Log = ReadBlob(r, MaxLogBytes),
            };
            int count = r.ReadByte();
            if (count > MaxAttachments) throw new ProtocolException("Too many attachments");
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                var a = new BugAttachment { Name = r.ReadString(MaxFileName), Data = ReadBlob(r, MaxFileBytes) };
                total += a.Data.Length;
                if (total > MaxAttachmentBytes) throw new ProtocolException("Attachments too large");
                if (!IsAllowedFile(a.Name)) throw new ProtocolException("Attachment type not allowed: " + Path.GetExtension(a.Name));
                report.Attachments.Add(a);
            }
            if (r.Remaining != 0) throw new ProtocolException("Trailing data");
            return report;
        }

        public long AttachmentBytes()
        {
            long total = 0;
            foreach (BugAttachment a in Attachments) total += a.Data.Length;
            return total;
        }

        private static void WriteBlob(PacketWriter w, byte[] data)
        {
            data = data ?? new byte[0];
            w.WriteInt32(data.Length);
            if (data.Length > 0) w.WriteFixedBytes(data, data.Length);
        }

        private static byte[] ReadBlob(PacketReader r, int max)
        {
            int length = r.ReadInt32();
            if (length < 0 || length > max) throw new ProtocolException("Field too large");
            return length == 0 ? new byte[0] : r.ReadFixedBytes(length);
        }
    }

    public sealed class BugAttachment
    {
        public string Name = "";
        public byte[] Data = new byte[0];
    }
}
