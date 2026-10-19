using System;
using System.Diagnostics;
using UnityEngine;

namespace WKRace.Mod
{
    // What the mod costs, written to the log every few seconds when Debug/ProfileLog is on: frame times, the time
    // spent drawing the overlay and seeding level contents, and how often the game collected garbage.
    internal static class Perf
    {
        private const float ReportSeconds = 5f;

        private static int _frames;
        private static float _total, _worst, _since;
        private static long _hudTicks, _seedTicks, _otherTicks;
        private static int _seedCalls;
        private static int _collections = -1;

        public static bool On { get { return RaceConfig.ProfileLog != null && RaceConfig.ProfileLog.Value; } }

        public static long Start() { return On ? Stopwatch.GetTimestamp() : 0; }
        public static void Hud(long started) { if (started != 0) _hudTicks += Stopwatch.GetTimestamp() - started; }
        public static void Update(long started) { if (started != 0) _otherTicks += Stopwatch.GetTimestamp() - started; }

        public static void Seed(long started)
        {
            if (started == 0) return;
            _seedTicks += Stopwatch.GetTimestamp() - started;
            _seedCalls++;
        }

        public static void Frame()
        {
            if (!On) { _frames = 0; return; }
            float dt = Time.unscaledDeltaTime;
            _frames++;
            _total += dt;
            _since += dt;
            if (dt > _worst) _worst = dt;
            if (_since < ReportSeconds) return;

            int collections = GC.CollectionCount(0);
            double perMs = 1000.0 / Stopwatch.Frequency;
            Plugin.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Perf: {0} frames in {1:0.0} s ({2:0} fps, worst frame {3:0.0} ms). Per frame: overlay {4:0.000} ms, rest of the mod {5:0.000} ms. Level seeding: {6:0.0} ms in {7} calls. Garbage collections: {8}.",
                _frames, _total, _frames / Math.Max(0.001f, _total), _worst * 1000f,
                _hudTicks * perMs / _frames, _otherTicks * perMs / _frames, _seedTicks * perMs, _seedCalls,
                _collections < 0 ? 0 : collections - _collections));
            _collections = collections;
            _frames = 0;
            _total = _worst = _since = 0f;
            _hudTicks = _seedTicks = _otherTicks = 0;
            _seedCalls = 0;
        }
    }
}
