using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace KosherLink
{
    class SdkPackage
    {
        public string Name;
        public string Url;
        public string Sha1;
        public long Size;
        public string Revision;
    }

    /// <summary>
    /// Downloads official Android components straight from Google's SDK repository
    /// (no Java / sdkmanager needed) and creates a minimal virtual device.
    /// </summary>
    static class AndroidSdk
    {
        const string RepoBase = "https://dl.google.com/android/repository/";
        public const string AvdName = "KosherLinkPlay";

        static IEnumerable<XElement> Kids(XElement e, string name)
        {
            return e.Elements().Where(x => x.Name.LocalName == name);
        }

        static XElement Kid(XElement e, string name)
        {
            return e == null ? null : Kids(e, name).FirstOrDefault();
        }

        static string Rev(XElement pkg)
        {
            var r = Kid(pkg, "revision");
            if (r == null) return "0";
            var parts = new List<string>();
            foreach (var n in new[] { "major", "minor", "micro" })
            {
                var x = Kid(r, n); if (x != null) parts.Add(x.Value);
            }
            return string.Join(".", parts);
        }

        static Version RevVersion(string s)
        {
            Version v; return Version.TryParse(s.Contains(".") ? s : s + ".0", out v) ? v : new Version(0, 0);
        }

        static string StableChannelId(XElement root)
        {
            foreach (var c in Kids(root, "channel"))
                if (c.Value.Trim() == "stable") return (string)c.Attribute("id");
            return "channel-0";
        }

        static SdkPackage Pick(XElement root, string path, string urlBase, bool needWindows)
        {
            string stable = StableChannelId(root);
            SdkPackage best = null;
            foreach (var p in Kids(root, "remotePackage"))
            {
                if ((string)p.Attribute("path") != path) continue;
                var ch = Kid(p, "channelRef");
                if (ch != null && (string)ch.Attribute("ref") != stable) continue;
                var archives = Kid(p, "archives");
                if (archives == null) continue;
                foreach (var a in Kids(archives, "archive"))
                {
                    var os = Kid(a, "host-os");
                    var arch = Kid(a, "host-arch");
                    if (needWindows && (os == null || os.Value != "windows")) continue;
                    if (arch != null && arch.Value != "x64") continue;
                    var comp = Kid(a, "complete");
                    if (comp == null) continue;
                    var pkg = new SdkPackage();
                    pkg.Name = path;
                    string url = Kid(comp, "url").Value;
                    pkg.Url = url.StartsWith("http") ? url : urlBase + url;
                    var cs = Kid(comp, "checksum");
                    pkg.Sha1 = cs != null ? cs.Value : null;
                    long size; long.TryParse(Kid(comp, "size").Value, out size); pkg.Size = size;
                    pkg.Revision = Rev(p);
                    if (best == null || RevVersion(pkg.Revision) > RevVersion(best.Revision)) best = pkg;
                }
            }
            if (best == null) throw new Exception("לא נמצא רכיב במאגר של Google: " + path);
            return best;
        }

        public static SdkPackage FindTool(string path)
        {
            var root = XElement.Parse(Net.GetString(RepoBase + "repository2-3.xml"));
            return Pick(root, path, RepoBase, true);
        }

        public static string ImagePath(int api, string tag)
        {
            return "system-images/android-" + api + "/" + tag + "/x86_64";
        }

        public static SdkPackage FindImage(int api, string tag)
        {
            string folder = tag == "default" ? "android" : tag;
            string listBase = RepoBase + "sys-img/" + folder + "/";
            var root = XElement.Parse(Net.GetString(listBase + "sys-img2-3.xml"));
            return Pick(root, "system-images;android-" + api + ";" + tag + ";x86_64", listBase, false);
        }

        static string RevFile(string dir) { return Path.Combine(dir, ".kosherlink-rev"); }

        public static string InstalledRev(string dir)
        {
            string f = RevFile(dir);
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }

        /// <summary>Downloads a package zip and places its single top-level folder at targetDir.</summary>
        public static void Install(SdkPackage pkg, string targetDir, Action<int> percent)
        {
            if (InstalledRev(targetDir) == pkg.Revision && Directory.Exists(targetDir))
            {
                Log.Write(pkg.Name + " כבר מותקן (" + pkg.Revision + ")");
                return;
            }
            string zip = Path.Combine(Paths.Downloads, Path.GetFileName(new Uri(pkg.Url).AbsolutePath));
            Log.Write("מוריד " + pkg.Name + " (" + Net.Mb(pkg.Size) + ")...");
            Net.Download(pkg.Url, zip, pkg.Sha1, (done, total) =>
            {
                long t = total > 0 ? total : pkg.Size;
                if (t > 0 && percent != null) percent((int)(done * 100 / t));
            });

            Log.Write("פורס " + pkg.Name + "...");
            string tmp = Path.Combine(Paths.Root, "tmp-extract");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            var tops = Directory.GetDirectories(tmp);
            string src = tops.Length == 1 && Directory.GetFiles(tmp).Length == 0 ? tops[0] : tmp;

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir));
            Dirs.MoveRobust(src, targetDir);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            File.WriteAllText(RevFile(targetDir), pkg.Revision);
            File.Delete(zip); // keep the disk small
            Log.Write(pkg.Name + " הותקן (" + pkg.Revision + ")");
        }

        public static bool ComponentsInstalled()
        {
            return File.Exists(Paths.Adb) && File.Exists(Paths.EmulatorExe)
                && Directory.Exists(Path.Combine(Paths.Sdk, ImagePath(Config.ApiLevel, Config.ImageTag).Replace('/', '\\')));
        }

        public static void InstallAll(Action<int> percent)
        {
            Directory.CreateDirectory(Path.Combine(Paths.Sdk, "platforms")); // emulator expects a "real" SDK layout
            Install(FindTool("platform-tools"), Path.Combine(Paths.Sdk, "platform-tools"), percent);
            Install(FindTool("emulator"), Path.Combine(Paths.Sdk, "emulator"), percent);
            Install(FindImage(Config.ApiLevel, Config.ImageTag),
                Path.Combine(Paths.Sdk, ImagePath(Config.ApiLevel, Config.ImageTag).Replace('/', '\\')), percent);
        }

        /// <summary>Returns null when hardware acceleration works, otherwise the error text.</summary>
        public static string AccelProblem()
        {
            string o;
            int code = Proc.Run(Paths.EmulatorExe, "-accel-check", out o, 60000);
            Log.Write("בדיקת האצה: " + o.Replace("\r", " ").Replace("\n", " "));
            return code == 0 ? null : o;
        }

        /// <summary>Writes a hand-tuned, minimal virtual device definition.</summary>
        public static void CreateAvd()
        {
            Directory.CreateDirectory(Paths.UserHome);
            string avdDir = Path.Combine(Paths.AvdHome, AvdName + ".avd");
            Directory.CreateDirectory(avdDir);
            int api = Config.ApiLevel; string tag = Config.ImageTag;

            File.WriteAllText(Path.Combine(Paths.AvdHome, AvdName + ".ini"),
                "avd.ini.encoding=UTF-8\r\npath=" + avdDir + "\r\ntarget=android-" + api + "\r\n", new UTF8Encoding(false));

            var cfg = new StringBuilder();
            Action<string, string> kv = (k, v) => cfg.Append(k).Append('=').Append(v).Append("\r\n");
            kv("AvdId", AvdName);
            kv("avd.ini.displayname", AvdName);
            kv("avd.ini.encoding", "UTF-8");
            kv("abi.type", "x86_64");
            kv("hw.cpu.arch", "x86_64");
            kv("hw.cpu.ncore", Config.Cores.ToString());
            kv("hw.ramSize", Config.RamMb.ToString());
            kv("vm.heapSize", "256");
            kv("disk.dataPartition.size", "6G");
            kv("image.sysdir.1", ImagePath(api, tag) + "/");
            kv("tag.id", tag);
            kv("target", "android-" + api);
            kv("PlayStore.enabled", "true"); // Google Play edition: the store, Google sign-in and Google Drive restore
            kv("tag.display", "Google Play");
            // small screen = less to render
            kv("hw.lcd.width", "720");
            kv("hw.lcd.height", "1280");
            kv("hw.lcd.density", "280");
            kv("hw.initialOrientation", "portrait");
            kv("hw.keyboard", "yes");
            kv("hw.mainKeys", "no");
            // everything WhatsApp does not need is switched off
            kv("hw.audioInput", "no");
            kv("hw.audioOutput", "no");
            kv("hw.camera.back", "none");
            kv("hw.camera.front", "none");
            kv("hw.gps", "no");
            kv("hw.accelerometer", "no");
            kv("hw.gyroscope", "no");
            kv("hw.sensors.orientation", "no");
            kv("hw.sensors.proximity", "no");
            kv("hw.sensors.magnetic_field", "no");
            kv("hw.sdCard", "no");
            kv("hw.battery", "yes");
            kv("hw.gpu.enabled", "yes");
            kv("hw.gpu.mode", "swiftshader_indirect");
            kv("showDeviceFrame", "no");
            kv("fastboot.forceColdBoot", "no");
            kv("fastboot.forceFastBoot", "yes");
            File.WriteAllText(Path.Combine(avdDir, "config.ini"), cfg.ToString(), new UTF8Encoding(false));
            Log.Write("המכשיר הווירטואלי הוגדר");
        }

        public static bool AvdExists()
        {
            return File.Exists(Path.Combine(Paths.AvdHome, AvdName + ".avd", "config.ini"));
        }
    }

    /// <summary>Starts/stops the headless virtual phone and keeps it as idle as possible.</summary>
    static class Emulator
    {
        static readonly object Sync = new object();

        static IEnumerable<Process> OurQemu()
        {
            string emuDir = Path.Combine(Paths.Sdk, "emulator");
            foreach (var p in Process.GetProcesses())
            {
                if (!p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)
                    && !p.ProcessName.Equals("emulator", StringComparison.OrdinalIgnoreCase)) continue;
                string file = null;
                try { file = p.MainModule.FileName; } catch { }
                if (file != null && file.StartsWith(emuDir, StringComparison.OrdinalIgnoreCase)) yield return p;
            }
        }

        public static bool IsRunning { get { return OurQemu().Any(); } }

        public static bool IsBooted
        {
            get { return Proc.Shell("getprop sys.boot_completed").Trim() == "1"; }
        }

        public static void Start()
        {
            lock (Sync)
            {
                if (IsRunning) return;
                Log.Write("מפעיל את הטלפון הווירטואלי ברקע...");
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    LaunchOnce();
                    // healthy = VM process still alive 20 seconds later
                    Thread.Sleep(20000);
                    if (IsRunning) { LowerPriority(); return; }
                    Log.Write("הטלפון הווירטואלי לא עלה (ניסיון " + attempt + "), מנסה שוב...");
                    if (Speed.EffectiveGpu == "host")
                    {
                        // some PCs (old drivers, remote desktop) can't give the phone the graphics card: draw in software
                        Config.SetBool("GpuFallback", true);
                        Log.Write("כרטיס המסך לא זמין לטלפון – עובר לציור בתוכנה");
                    }
                    Thread.Sleep(10000);
                }
                throw new Exception("הטלפון הווירטואלי לא מצליח לעלות. פרטים: " + Path.Combine(Paths.Root, "emulator.log"));
            }
        }

        static void LaunchOnce()
        {
            {
                // leftovers from an unclean shutdown block the next start
                string avdDir = Path.Combine(Paths.AvdHome, AndroidSdk.AvdName + ".avd");
                foreach (var f in new[] { "multiinstance.lock", "hardware-qemu.ini.lock" })
                {
                    string p = Path.Combine(avdDir, f);
                    try { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); } catch { }
                }
                // a quick-boot state saved with other cores / memory / graphics does not fit: start fresh once
                string sig = Speed.Signature;
                bool fresh = Config.Get("LastSpeed", "") != sig;
                if (fresh) Log.Write("מהירות הטלפון: " + Speed.Current.Name + " (" + sig + ") – הפעלה מלאה פעם אחת");
                Config.Set("LastSpeed", sig);
                string args = "-avd " + AndroidSdk.AvdName
                    + " -port " + Proc.ConsolePort
                    + " -no-window -no-audio -no-boot-anim -no-metrics"
                    + " -gpu " + Speed.EffectiveGpu
                    + " -memory " + Config.RamMb
                    + " -cores " + Config.Cores
                    // the Google Play image cannot be rooted, so language and time zone are handed in by the emulator
                    + " -timezone " + Config.Get("TimeZone", "Asia/Jerusalem")
                    + " -change-locale " + Config.Get("Locale", "he-IL")
                    + (fresh ? " -no-snapshot-load" : "");
                // the emulator's own output goes to a log file (helps diagnose problems)
                var psi = Proc.Psi(Paths.EmulatorExe, args);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                var log = new StreamWriter(Path.Combine(Paths.Root, "emulator.log"), false, new UTF8Encoding(false));
                log.AutoFlush = true;
                var emu = new Process { StartInfo = psi };
                DataReceivedEventHandler write = (s, e) => { if (e.Data != null) lock (log) { try { log.WriteLine(e.Data); } catch { } } };
                emu.OutputDataReceived += write;
                emu.ErrorDataReceived += write;
                emu.Start();
                emu.BeginOutputReadLine();
                emu.BeginErrorReadLine();
            }
        }

        /// <summary>
        /// Normal priority while the phone window is open (or always, at the "fast" level); otherwise the PC's own
        /// programs come first.
        /// </summary>
        public static void LowerPriority()
        {
            var prio = Speed.Current.AlwaysNormal || PhoneViewer.IsOpen ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
            foreach (var p in OurQemu())
            {
                try { p.PriorityClass = prio; } catch { }
            }
        }

        public static bool WaitBoot(int timeoutSec)
        {
            Proc.Adb("start-server", 30000);
            var start = DateTime.Now;
            var until = start.AddSeconds(timeoutSec);
            while (DateTime.Now < until)
            {
                if (IsBooted) return true;
                if (!IsRunning && DateTime.Now - start > TimeSpan.FromSeconds(30)) return false;
                Thread.Sleep(3000);
            }
            return false;
        }

        /// <summary>Runs after every boot: no animations, no doze for WhatsApp, screen off.</summary>
        public static void Tune()
        {
            LowerPriority();
            ApplyLanguageAndTime();
            foreach (var c in new[]
            {
                "settings put global window_animation_scale 0",
                "settings put global transition_animation_scale 0",
                "settings put global animator_duration_scale 0",
                "settings put global package_verifier_enable 0",
                "locksettings set-disabled true",
                "dumpsys deviceidle disable",
                "cmd deviceidle whitelist +com.whatsapp",
                "cmd appops set com.whatsapp RUN_ANY_IN_BACKGROUND allow",
                "am set-standby-bucket com.whatsapp active",
                "settings put system screen_off_timeout 60000",
            }) Proc.Shell(c);
            ScreenOff();
        }

        /// <summary>
        /// Hebrew + Israel time by default (config: Locale, TimeZone). Only restarts the phone's
        /// interface when something actually changed - normally just on the first boot.
        /// </summary>
        static void ApplyLanguageAndTime()
        {
            // Google Play edition: no root, so the emulator itself sets them at start (-change-locale, -timezone).
            // Here we only report what the phone ended up with.
            string locale = Config.Get("Locale", "he-IL");
            string tz = Config.Get("TimeZone", "Asia/Jerusalem");
            string gotLocale = Proc.Shell("getprop persist.sys.locale"), gotTz = Proc.Shell("getprop persist.sys.timezone");
            if (gotLocale != locale || gotTz != tz)
                Log.Write("שפה/אזור זמן בטלפון: " + gotLocale + " / " + gotTz + " (רצוי " + locale + " / " + tz + ")");
        }

        public static void ScreenOff() { Proc.Shell("input keyevent 223"); }
        public static void ScreenOn()
        {
            Proc.Shell("input keyevent 224");
            Proc.Shell("wm dismiss-keyguard");
        }

        public static void Stop()
        {
            lock (Sync)
            {
                if (!IsRunning) return;
                Log.Write("מכבה את הטלפון הווירטואלי (שומר מצב להפעלה מהירה)...");
                Proc.Adb("-s " + Proc.Serial + " emu kill", 30000);
                var until = DateTime.Now.AddSeconds(40);
                while (IsRunning && DateTime.Now < until) Thread.Sleep(1000);
                foreach (var p in OurQemu()) { try { p.Kill(); } catch { } }
            }
        }
    }
}
