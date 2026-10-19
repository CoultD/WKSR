using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // Practice runs: a solo run of the race world on any seed, with the same rules and seed guarantees as a race, on
    // this PC only (no server, no rating). WKSR stands in for the server (PracticeHost). A run ends when the player
    // finishes or dies (a race also ends for both players when either dies). The player's best run on each seed
    // (practice or race) is kept, and a later practice run on that seed replays it as the opponent: its splits for the
    // split table and HUD, and its movement as the ghost.

    // The best run per seed, one file each: %AppData%\WKRace\practice (practice.<profile> for a test profile).
    internal sealed class PracticeStore
    {
        // Oldest files beyond this are deleted.
        public const int MaxSeeds = 100;

        private readonly string _dir;

        public PracticeStore(string dir)
        {
            _dir = dir;
        }

        // Next to the settings file (client.json, or client.<profile>.json).
        public static PracticeStore For(string settingsPath)
        {
            string file = Path.GetFileNameWithoutExtension(settingsPath);
            string suffix = file.StartsWith("client.", StringComparison.Ordinal) ? file.Substring("client".Length) : "";
            return new PracticeStore(Path.Combine(Path.GetDirectoryName(settingsPath), "practice" + suffix));
        }

        // A segment's best run on a seed is kept apart from the best run of the whole climb on it.
        private string PathFor(int seed, byte segment)
        {
            string part = segment == Segments.None ? "" : "-segment" + segment.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Path.Combine(_dir, "seed" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + part + ".wkrun");
        }

        // The best run on this seed (of the whole climb, or of a segment), or null (none, or a damaged file).
        public RunRecording Load(int seed, byte segment = Segments.None)
        {
            try
            {
                string path = PathFor(seed, segment);
                if (!File.Exists(path)) return null;
                RunRecording run = RunRecording.FromBytes(File.ReadAllBytes(path));
                return run.Seed == seed && run.Segment == segment ? run : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ProtocolException)
            {
                return null;
            }
        }

        // Keeps the run if it beats the stored best on its seed (RunRecording.IsBetterThan). Returns whether it was kept.
        public bool SaveIfBest(RunRecording run)
        {
            if (!run.IsBetterThan(Load(run.Seed, run.Segment))) return false;
            Directory.CreateDirectory(_dir);
            string path = PathFor(run.Seed, run.Segment);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, run.ToBytes());
            // (Not File.Move with overwrite: the game's runtime does not have it.)
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            Prune();
            return true;
        }

        private void Prune()
        {
            FileInfo[] files = new DirectoryInfo(_dir).GetFiles("seed*.wkrun").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            for (int i = MaxSeeds; i < files.Length; i++)
            {
                try { files[i].Delete(); }
                catch (IOException) { }
            }
        }
    }
}
