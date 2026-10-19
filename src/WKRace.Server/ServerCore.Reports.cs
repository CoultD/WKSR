using System;
using System.Collections.Generic;
using WKRace.Protocol;

namespace WKRace.Server
{
    public struct ReportCheck
    {
        public bool Ok;
        // HTTP status for a refusal.
        public int Status;
        public string Message;
        public string PlayerName;
    }

    // Bug reports (posted by WKSR to /report): only known players with their token, a few per player per day and a cap
    // for the whole server, so the mailbox cannot be flooded.
    public sealed partial class ServerCore
    {
        public const int ReportsPerPlayerPerDay = 5;
        public const int ReportsPerDay = 200;
        private const long DayMs = 24L * 60 * 60 * 1000;

        private readonly Dictionary<Guid, Queue<long>> _reportsByPlayer = new Dictionary<Guid, Queue<long>>();
        private readonly Queue<long> _reports = new Queue<long>();

        public ReportCheck CheckReport(Guid playerId, byte[] token)
        {
            PlayerRecord record = _store.Find(playerId);
            if (record == null || !PlayerStore.TokenMatches(record.TokenHash, PlayerStore.HashToken(token)))
                return new ReportCheck { Status = 403, Message = "This player is not known to the server. Connect to the server once, then try again." };
            if (record.Banned)
                return new ReportCheck { Status = 403, Message = "This player is banned from the server." };

            long now = _now();
            Queue<long> mine;
            if (!_reportsByPlayer.TryGetValue(playerId, out mine)) _reportsByPlayer[playerId] = mine = new Queue<long>();
            while (mine.Count > 0 && mine.Peek() <= now - DayMs) mine.Dequeue();
            while (_reports.Count > 0 && _reports.Peek() <= now - DayMs) _reports.Dequeue();
            if (mine.Count >= ReportsPerPlayerPerDay)
                return new ReportCheck { Status = 429, Message = "You have sent " + ReportsPerPlayerPerDay + " reports today. Try again tomorrow." };
            if (_reports.Count >= ReportsPerDay)
                return new ReportCheck { Status = 429, Message = "The server has received too many reports today. Try again tomorrow." };
            mine.Enqueue(now);
            _reports.Enqueue(now);
            return new ReportCheck { Ok = true, PlayerName = record.Name };
        }
    }
}
