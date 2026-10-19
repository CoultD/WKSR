using System;
using System.Collections.Generic;

namespace WKRace.Server
{
    // Watches one run's reports (the game's timer and the player's position, about 15 a second) for a timer that
    // stands still while the player keeps moving.
    //
    // The time on a leaderboard is the game's own timer, which the player's PC reports. It may stand still: while the
    // game loads, and in a solo run while it is paused. In both cases the player does not move. A changed game could
    // report a stopped timer while the climb goes on; this is what gives that away.
    //
    // It is a hint for the admin, not proof: the game also slows its own timer while the player moves (slow-time
    // buffs: the Calming Buddy trinket slows time to about a twentieth while the bag is open), and the server cannot
    // tell those apart. A run that fails keeps its time and is flagged for a look ("board flagged").
    internal sealed class TimerWatch
    {
        // Looked at over windows this long, so that reports arriving in bursts (a slow connection) do not matter.
        private const int WindowMs = 4000;
        // "Standing still": the timer advanced by less than a tenth of the real time. (Its slowest real speed is 0.15.)
        private const int StoppedPerTen = 1;
        // "Moving": further than this from where the player was at the start of the window.
        private const float MovedMeters = 3f;
        // This much moving on a stopped timer, in total, fails the run.
        private const int ToleranceMs = 2500;
        // Reports come about 15 times a second. A silence this long is a gap, not a slow report.
        private const int GapMs = 1500;

        private struct Sample
        {
            public long RealMs;
            public int GameMs;
            public float X, Y, Z;
        }

        // About four seconds of reports, oldest first.
        private readonly List<Sample> _window = new List<Sample>();
        private long _lastRealMs = -1;
        private long _movedWhileStoppedMs;

        public bool Failed { get { return _movedWhileStoppedMs > ToleranceMs; } }
        public long MovedWhileStoppedMs { get { return _movedWhileStoppedMs; } }

        public void Report(long realMs, int gameMs, float x, float y, float z)
        {
            if (_lastRealMs >= 0 && realMs < _lastRealMs) return;   // the clock never runs back
            long sinceLast = _lastRealMs < 0 ? 0 : realMs - _lastRealMs;
            _lastRealMs = realMs;

            // No reports for a while (the game froze, was suspended or minimised, or the connection hung): what
            // happened in between is unknown, so the watch starts over from here. Otherwise the climbing after the
            // gap would be measured against the position before it, on a timer that did stand still during the gap.
            if (sinceLast > GapMs) _window.Clear();

            // Keep the newest sample that is at least a window old as the one to compare with.
            while (_window.Count > 1 && realMs - _window[1].RealMs >= WindowMs) _window.RemoveAt(0);
            if (_window.Count > 0)
            {
                Sample old = _window[0];
                long real = realMs - old.RealMs;
                if (real >= WindowMs)
                {
                    long game = (long)gameMs - old.GameMs;
                    float dx = x - old.X, dy = y - old.Y, dz = z - old.Z;
                    bool stopped = game * 10 < real * StoppedPerTen;
                    bool moved = dx * dx + dy * dy + dz * dz > MovedMeters * MovedMeters;
                    if (stopped && moved) _movedWhileStoppedMs += Math.Min(sinceLast, WindowMs);
                }
            }
            _window.Add(new Sample { RealMs = realMs, GameMs = gameMs, X = x, Y = y, Z = z });
        }
    }
}
