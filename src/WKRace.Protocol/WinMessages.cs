using System;

namespace WKRace.Protocol
{
    // Keep running after a win (0.9.7 on). A 1v1 won because the opponent died arrives as a MatchResult with
    // KeepRunning set; the last player standing in a party gets LastStanding. Either way the run goes on until the
    // player finishes, dies or ends it with EndRun. A lobby's host decides whether it is allowed (LobbyOptions);
    // ranked always allows it.

    // Client -> server: end the run that goes on after the win.
    public sealed class EndRun : Message
    {
        public Guid MatchId;

        public override MessageType Type { get { return MessageType.EndRun; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
        }

        public static EndRun Read(PacketReader r)
        {
            return new EndRun { MatchId = r.ReadGuid() };
        }
    }

    // Server -> client: everyone else in this party match is out and nobody has finished; this player is first
    // whatever happens now, and may keep running or end the run (EndRun).
    public sealed class LastStanding : Message
    {
        public Guid MatchId;

        public override MessageType Type { get { return MessageType.LastStanding; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
        }

        public static LastStanding Read(PacketReader r)
        {
            return new LastStanding { MatchId = r.ReadGuid() };
        }
    }

    // Client (the lobby's host) -> server: the lobby's options.
    public sealed class LobbyOptions : Message
    {
        // The last player still running may finish their run (keep running after a win).
        public bool LetLastFinish = true;

        public override MessageType Type { get { return MessageType.LobbyOptions; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(LetLastFinish);
        }

        public static LobbyOptions Read(PacketReader r)
        {
            return new LobbyOptions { LetLastFinish = r.ReadBool() };
        }
    }
}
