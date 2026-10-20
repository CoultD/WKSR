using WKRace.Protocol;

namespace WKRace.Server
{
    // The counts a profile shows (ServerCore.Players): runs finished to the end of the climb, ranked and unranked
    // apart, and restarts. The server counts the runs it hosts or referees; an unranked solo run on the player's own
    // PC is reported by their client (RunNote). Segment runs are not finished climbs and are not counted as runs.
    public sealed partial class ServerCore
    {
        // An unranked finish reported by a client: no climb is shorter than this.
        private const int RunNoteFinishGapMs = 60000;
        // A restart builds a new world, which takes seconds.
        private const int RunNoteRestartGapMs = 1000;

        private void CountFinishedRun(Player player, bool ranked, byte segment)
        {
            if (segment != Segments.None) return;
            if (ranked) player.Record.RankedRuns++;
            else player.Record.UnrankedRuns++;
            _store.MarkDirty(player.Record);
        }

        private void CountReset(Player player)
        {
            player.Record.Resets++;
            _store.MarkDirty(player.Record);
        }

        private void HandleRunNote(Player player, RunNote note)
        {
            long now = _now();
            if (note.Kind == RunNote.Finished)
            {
                if (now - player.LastRunNoteFinishMs < RunNoteFinishGapMs) return;
                player.LastRunNoteFinishMs = now;
                CountFinishedRun(player, false, note.Segment);
            }
            else if (note.Kind == RunNote.Restarted)
            {
                if (now - player.LastRunNoteRestartMs < RunNoteRestartGapMs) return;
                player.LastRunNoteRestartMs = now;
                CountReset(player);
            }
        }
    }
}
