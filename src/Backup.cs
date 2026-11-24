using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KosherLink
{
    /// <summary>
    /// Copies WhatsApp's own daily local backup (encrypted chats) and the media files from the
    /// virtual phone to a folder on the PC. Point the folder at Google Drive / OneDrive to get an
    /// off-site copy. Also offers a full copy of the whole virtual phone.
    /// </summary>
    static class Backup
    {
        const string WaDir = "/sdcard/Android/media/com.whatsapp/WhatsApp";

        public static string Dir
        {
            get
            {
                return Config.Get("BackupDir",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KosherLink Google Backup"));
            }
            set { Config.Set("BackupDir", value); }
        }

        static string ChatsDir { get { return Path.Combine(Dir, "Chats"); } }
        static string MediaDir { get { return Path.Combine(Dir, "Media"); } }
        static string FullDir { get { return Path.Combine(Dir, "Full"); } }

        public static bool Due
        {
            get
            {
                DateTime last;
                if (!DateTime.TryParse(Config.Get("BackupLast", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return true;
                return DateTime.UtcNow - last > TimeSpan.FromHours(20);
            }
        }

        class RemoteFile { public string Path; public long Size; }

        /// <summary>Lists files under a folder of the phone (size + path), empty if the folder does not exist.</summary>
        static List<RemoteFile> ListFiles(string dir)
        {
            var res = new List<RemoteFile>();
            string o = Proc.Shell("find '" + dir + "' -type f -exec stat -c '%s %n' {} + 2>/dev/null");
            foreach (var raw in o.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int sp = line.IndexOf(' ');
                long size;
                if (sp <= 0 || !long.TryParse(line.Substring(0, sp), out size)) continue;
                string path = line.Substring(sp + 1);
                if (!path.StartsWith(dir)) continue;
                res.Add(new RemoteFile { Path = path, Size = size });
            }
            return res;
        }

        static bool Pull(string remote, string local)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            string o;
            Proc.Run(Paths.Adb, "-s " + Proc.Serial + " pull \"" + remote + "\" \"" + local + "\"", out o, 10 * 60 * 1000);
            return File.Exists(local);
        }

        /// <summary>Daily backup. Returns a short summary for the user.</summary>
        public static string Run()
        {
            Directory.CreateDirectory(Dir);
            int chats = 0, media = 0;

            // 1. chats: WhatsApp writes an encrypted backup every night (msgstore*.crypt*).
            var dbs = ListFiles(WaDir + "/Databases");
            if (dbs.Count > 0)
            {
                string snap = Path.Combine(ChatsDir, DateTime.Now.ToString("yyyy-MM-dd"));
                foreach (var f in dbs)
                    if (Pull(f.Path, Path.Combine(snap, f.Path.Substring(f.Path.LastIndexOf('/') + 1)))) chats++;
                Prune(ChatsDir, Config.GetInt("BackupKeepDays", 14));
            }
            else Log.Write("גיבוי: וואטסאפ עוד לא יצר גיבוי שיחות (נוצר אוטומטית כל לילה, או ידנית: הגדרות ← צ'אטים ← גיבוי צ'אטים)");

            // 2. media (photos, voice notes, documents): only new or changed files are copied.
            if (Config.Get("BackupMedia", "1") == "1")
            {
                foreach (var f in ListFiles(WaDir + "/Media"))
                {
                    if (f.Path.EndsWith("/.nomedia")) continue; // empty marker files that WhatsApp recreates by itself
                    string rel = f.Path.Substring((WaDir + "/Media/").Length).Replace('/', '\\');
                    string local = Path.Combine(MediaDir, rel);
                    var fi = new FileInfo(local);
                    if (fi.Exists && fi.Length == f.Size) continue;
                    if (Pull(f.Path, local)) media++;
                }
            }

            Config.Set("BackupLast", DateTime.UtcNow.ToString("o"));
            string summary = dbs.Count > 0
                ? "גיבוי הושלם: " + chats + " קבצי שיחות, " + media + " קבצי מדיה חדשים"
                : "גובו " + media + " קבצי מדיה חדשים. גיבוי השיחות עוד לא נוצר על ידי וואטסאפ.";
            Log.Write(summary + " ← " + Dir);
            return summary;
        }

        /// <summary>Keeps only the newest N dated sub-folders.</summary>
        static void Prune(string dir, int keep)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var old in Directory.GetDirectories(dir).OrderByDescending(d => d).Skip(keep))
            {
                try { Directory.Delete(old, true); } catch { }
            }
        }

        public static string LatestChatsSnapshot()
        {
            if (!Directory.Exists(ChatsDir)) return null;
            return Directory.GetDirectories(ChatsDir).OrderByDescending(d => d)
                .FirstOrDefault(d => Directory.GetFiles(d, "msgstore*").Length > 0);
        }

        /// <summary>
        /// Puts the latest backup back into the phone. Must run after WhatsApp is installed and BEFORE
        /// registering: WhatsApp then offers "Restore" right after the voice-call verification.
        /// </summary>
        public static void Restore()
        {
            string snap = LatestChatsSnapshot();
            if (snap == null) throw new Exception("לא נמצא גיבוי שיחות בתיקייה " + ChatsDir);
            Log.Write("משחזר גיבוי מ־" + snap + "...");
            Proc.Shell("mkdir -p '" + WaDir + "/Databases'");
            string o;
            foreach (var f in Directory.GetFiles(snap))
                Proc.Run(Paths.Adb, "-s " + Proc.Serial + " push \"" + f + "\" \"" + WaDir + "/Databases/\"", out o, 10 * 60 * 1000);
            if (Directory.Exists(MediaDir))
            {
                Log.Write("משחזר מדיה...");
                Proc.Run(Paths.Adb, "-s " + Proc.Serial + " push \"" + MediaDir + "\" \"" + WaDir + "/\"", out o, 60 * 60 * 1000);
            }
            Log.Write("הגיבוי הועתק לטלפון. עכשיו להירשם – וואטסאפ יציע \"שחזור\".");
        }

        /// <summary>Copy of the entire virtual phone (needs the phone switched off for a moment).</summary>
        public static string Full()
        {
            string avd = Path.Combine(Paths.AvdHome, AndroidSdk.AvdName + ".avd");
            string target = Path.Combine(FullDir, DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
            Log.Write("גיבוי מלא של הטלפון אל " + target + "...");
            CopyDir(avd, target);
            File.Copy(Path.Combine(Paths.AvdHome, AndroidSdk.AvdName + ".ini"), Path.Combine(target, AndroidSdk.AvdName + ".ini"), true);
            Prune(FullDir, Config.GetInt("BackupKeepFull", 2));
            string summary = "גיבוי מלא הושלם: " + target;
            Log.Write(summary);
            return summary;
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                if (f.EndsWith(".lock")) continue;
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                if (d.EndsWith(".lock")) continue;
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }
    }
}
