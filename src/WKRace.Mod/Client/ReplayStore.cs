using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // One replay the player chose to keep.
    internal sealed class SavedReplay
    {
        public string Path;
        public RunRecording Run;
    }

    // The replays a player saved with SAVE REPLAY: one file each in %AppData%\WKRace\replays (replays.<profile> for a
    // test profile). They stay on this PC and are never uploaded. (The best run per seed, which unranked runs replay
    // by themselves, is kept apart in the practice folder.)
    internal sealed class ReplayStore
    {
        private readonly string _dir;

        public ReplayStore(string dir)
        {
            _dir = dir;
        }

        public string Folder { get { return _dir; } }

        // Next to the settings file (client.json, or client.<profile>.json).
        public static ReplayStore For(string settingsPath)
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(settingsPath);
            string suffix = file.StartsWith("client.", StringComparison.Ordinal) ? file.Substring("client".Length) : "";
            return new ReplayStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath), "replays" + suffix));
        }

        // Writes the run to a new file and returns its path.
        public string Save(RunRecording run)
        {
            Directory.CreateDirectory(_dir);
            string stamp = run.RecordedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string name = stamp + "-seed" + run.Seed.ToString(CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(_dir, name + ".wkrun");
            for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(_dir, name + "-" + n + ".wkrun");
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, run.ToBytes());
            // (Not File.Move with overwrite: the game's runtime does not have it.)
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            return path;
        }

        // Every saved replay that can still be read, newest first.
        public List<SavedReplay> List()
        {
            var replays = new List<SavedReplay>();
            if (!Directory.Exists(_dir)) return replays;
            foreach (string path in Directory.GetFiles(_dir, "*.wkrun"))
            {
                try
                {
                    replays.Add(new SavedReplay { Path = path, Run = RunRecording.FromBytes(File.ReadAllBytes(path)) });
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ProtocolException)
                {
                    // A damaged file, or one from a version this WKSR cannot read: left where it is, not listed.
                }
            }
            return replays.OrderByDescending(r => r.Run.RecordedUtc).ToList();
        }

        public void Delete(string path)
        {
            // Only ever a file of this folder.
            if (string.IsNullOrEmpty(path) || !string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), System.IO.Path.GetFullPath(_dir), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
