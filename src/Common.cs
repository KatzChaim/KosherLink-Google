using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KosherLink
{
    /// <summary>All on-disk locations. Everything lives under one local folder.</summary>
    static class Paths
    {
        public static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Root;

        static Paths()
        {
            // Portable mode: a file named "portable.txt" next to the exe keeps all data beside it.
            if (File.Exists(Path.Combine(ExeDir, "portable.txt")))
                Root = Path.Combine(ExeDir, "data");
            else
                Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KosherLink-Play");
            Directory.CreateDirectory(Root);
        }

        public static string Sdk { get { return Path.Combine(Root, "sdk"); } }
        public static string AvdHome { get { return Path.Combine(Root, "avd"); } }
        public static string UserHome { get { return Path.Combine(Root, "android-home"); } }
        public static string Downloads { get { return Path.Combine(Root, "downloads"); } }
        public static string Tools { get { return Path.Combine(Root, "tools"); } }
        public static string ConfigFile { get { return Path.Combine(Root, "config.ini"); } }
        public static string LogFile { get { return Path.Combine(Root, "log.txt"); } }
        public static string Adb { get { return Path.Combine(Sdk, "platform-tools", "adb.exe"); } }
        public static string EmulatorExe { get { return Path.Combine(Sdk, "emulator", "emulator.exe"); } }
        public static string Scrcpy { get { return Path.Combine(Tools, "scrcpy", "scrcpy.exe"); } }
    }

    /// <summary>Tiny key=value settings file.</summary>
    static class Config
    {
        static readonly object Sync = new object();
        static Dictionary<string, string> _values;

        static Dictionary<string, string> Values
        {
            get
            {
                if (_values == null)
                {
                    _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(Paths.ConfigFile))
                    {
                        foreach (var line in File.ReadAllLines(Paths.ConfigFile, Encoding.UTF8))
                        {
                            int i = line.IndexOf('=');
                            if (i > 0 && !line.StartsWith("#")) _values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                        }
                    }
                }
                return _values;
            }
        }

        public static string Get(string key, string def)
        {
            lock (Sync) { string v; return Values.TryGetValue(key, out v) && v.Length > 0 ? v : def; }
        }

        public static int GetInt(string key, int def)
        {
            int v; return int.TryParse(Get(key, ""), out v) ? v : def;
        }

        public static bool GetBool(string key) { return Get(key, "0") == "1"; }

        public static void Set(string key, string value)
        {
            lock (Sync)
            {
                Values[key] = value ?? "";
                var sb = new StringBuilder();
                foreach (var kv in Values) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(Paths.ConfigFile, sb.ToString(), Encoding.UTF8);
            }
        }

        public static void SetBool(string key, bool v) { Set(key, v ? "1" : "0"); }

        // Defaults chosen to be as light as possible while still running WhatsApp comfortably.
        public static int ApiLevel { get { return GetInt("ApiLevel", 30); } }
        // "default" = plain AOSP without Google services. ("aosp_atd" is smaller but does not draw app screens.)
        public static string ImageTag { get { return Get("ImageTag", "google_apis_playstore"); } }
        // RAM / cores come from the speed level; RamMb / Cores in config.ini still override them (advanced).
        public static int RamMb { get { return GetInt("RamMb", Speed.Current.RamMb); } }
        public static int Cores { get { return GetInt("Cores", Speed.Current.Cores); } }
    }

    /// <summary>
    /// The phone's speed level (menu → "מהירות הטלפון"). Measured on an i9 / UHD 630 (2026-11-24): 4 cores with the
    /// graphics card was clearly the smoothest (scroll jank 12% vs 23-36%, WhatsApp opens in 0.9s vs 1.0-1.1s) and even
    /// used less PC memory than software drawing; 6 cores was slower than 4, so "fast" stays at 4.
    /// </summary>
    class Speed
    {
        public string Id, Name;
        public int Cores, RamMb;
        public string Gpu;          // "host" = the PC's graphics card ("auto" picks software drawing when there is no window)
        public bool AlwaysNormal;   // false = normal priority only while the phone window is open

        public static readonly Speed[] All =
        {
            new Speed { Id = "eco", Name = "חסכוני – הכי קל למחשב", Cores = 2, RamMb = 2048, Gpu = "swiftshader_indirect", AlwaysNormal = false },
            new Speed { Id = "balanced", Name = "מאוזן (מומלץ)", Cores = 4, RamMb = 3072, Gpu = "host", AlwaysNormal = false },
            new Speed { Id = "fast", Name = "מהיר – למחשב חזק", Cores = 4, RamMb = 4096, Gpu = "host", AlwaysNormal = true },
        };

        public static Speed Current
        {
            get
            {
                string id = Config.Get("Speed", "balanced");
                foreach (var s in All) if (s.Id == id) return s;
                return All[1];
            }
        }

        /// <summary>The graphics mode actually used: software drawing if the graphics card failed on this PC before.</summary>
        public static string EffectiveGpu
        {
            get { return Current.Gpu == "host" && Config.GetBool("GpuFallback") ? "swiftshader_indirect" : Current.Gpu; }
        }

        /// <summary>What the phone is started with; when it changes, the saved quick-boot state no longer fits.</summary>
        public static string Signature { get { return Config.Cores + "/" + Config.RamMb + "/" + EffectiveGpu; } }
    }

    static class Log
    {
        static readonly object Sync = new object();
        public static event Action<string> Line;

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (Sync)
            {
                try
                {
                    var fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(Paths.LogFile);
                    File.AppendAllText(Paths.LogFile, line + "\r\n", Encoding.UTF8);
                }
                catch { }
            }
            var h = Line; if (h != null) h(msg);
        }
    }

    /// <summary>Runs the Android tools with an environment that keeps everything inside our folder.</summary>
    static class Proc
    {
        public const string Serial = "emulator-5586";
        public const int ConsolePort = 5586;
        public const string AdbServerPort = "5139"; // separate adb server so we never clash with other Android tools

        public static void ApplyEnv(ProcessStartInfo psi)
        {
            psi.EnvironmentVariables["ANDROID_SDK_ROOT"] = Paths.Sdk;
            psi.EnvironmentVariables["ANDROID_HOME"] = Paths.Sdk;
            psi.EnvironmentVariables["ANDROID_AVD_HOME"] = Paths.AvdHome;
            psi.EnvironmentVariables["ANDROID_USER_HOME"] = Paths.UserHome;
            psi.EnvironmentVariables["ANDROID_EMULATOR_HOME"] = Paths.UserHome;
            psi.EnvironmentVariables["ANDROID_ADB_SERVER_PORT"] = AdbServerPort;
            psi.EnvironmentVariables["ADB"] = Paths.Adb; // used by scrcpy
        }

        public static ProcessStartInfo Psi(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            ApplyEnv(psi);
            return psi;
        }

        /// <summary>Runs a process and returns exit code; output contains stdout+stderr.</summary>
        public static int Run(string exe, string args, out string output, int timeoutMs)
        {
            var psi = Psi(exe, args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            var sb = new StringBuilder();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    lock (sb) output = sb.ToString();
                    return -999;
                }
                p.WaitForExit();
                lock (sb) output = sb.ToString();
                return p.ExitCode;
            }
        }

        public static string Adb(string args, int timeoutMs)
        {
            string o; Run(Paths.Adb, args, out o, timeoutMs); return o.Trim();
        }

        public static string Shell(string cmd)
        {
            return Adb("-s " + Serial + " shell " + cmd, 60000);
        }

        /// <summary>Switches adb to root (the plain AOSP image allows it); needed for system files and settings.</summary>
        public static void Root()
        {
            if (Shell("id -u") == "0") return;
            Adb("-s " + Serial + " root", 30000);
            System.Threading.Thread.Sleep(2000);
            Adb("-s " + Serial + " wait-for-device", 60000);
        }
    }

    static class Net
    {
        public const string UserAgent = "KosherLink-Play/" + Program.Version;

        static Net()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
        }

        static HttpWebRequest Req(string url, string method)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method;
            r.UserAgent = UserAgent;
            r.AllowAutoRedirect = true;
            r.Timeout = 60000;
            r.ReadWriteTimeout = 120000;
            return r;
        }

        public static string GetString(string url)
        {
            var r = Req(url, "GET");
            r.Accept = "*/*";
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>Returns a short fingerprint of the remote file (last-modified + size) without downloading it.</summary>
        public static string Fingerprint(string url)
        {
            var r = Req(url, "HEAD");
            using (var resp = (HttpWebResponse)r.GetResponse())
                return resp.Headers["Last-Modified"] + "|" + resp.ContentLength;
        }

        /// <summary>Downloads with resume support and optional SHA-1 verification.</summary>
        public static void Download(string url, string dest, string sha1, Action<long, long> progress)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            string part = dest + ".part";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    var r = Req(url, "GET");
                    if (have > 0) r.AddRange(have);
                    using (var resp = (HttpWebResponse)r.GetResponse())
                    {
                        bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                        if (!append) have = 0;
                        long total = resp.ContentLength > 0 ? resp.ContentLength + have : -1;
                        using (var input = resp.GetResponseStream())
                        using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                        {
                            var buf = new byte[256 * 1024];
                            int n; long done = have; var last = DateTime.MinValue;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                output.Write(buf, 0, n);
                                done += n;
                                if (progress != null && (DateTime.Now - last).TotalMilliseconds > 300)
                                {
                                    progress(done, total); last = DateTime.Now;
                                }
                            }
                            if (progress != null) progress(done, total);
                        }
                    }
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt >= 5) throw;
                    Log.Write("הורדה נכשלה (" + ex.Message + "), מנסה שוב...");
                    Thread.Sleep(3000 * attempt);
                }
            }

            if (!string.IsNullOrEmpty(sha1))
            {
                string actual = Sha1(part);
                if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    throw new Exception("בדיקת תקינות הקובץ נכשלה: " + Path.GetFileName(dest));
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }

        public static string Sha1(string file)
        {
            using (var s = File.OpenRead(file))
            using (var h = SHA1.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        public static string Sha256(string file)
        {
            using (var s = File.OpenRead(file))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        public static string Mb(long bytes) { return (bytes / 1024.0 / 1024.0).ToString("0") + "MB"; }
    }

    static class Dirs
    {
        /// <summary>
        /// Moves a freshly extracted folder into place. Antivirus software often holds new files open for a moment
        /// while scanning them, so retry, and as a last resort copy instead of moving.
        /// </summary>
        public static void MoveRobust(string src, string dst)
        {
            for (int i = 0; ; i++)
            {
                try { Directory.Move(src, dst); return; }
                catch (IOException) { if (i >= 5) break; }
                catch (UnauthorizedAccessException) { if (i >= 5) break; }
                System.Threading.Thread.Sleep(2000);
            }
            Copy(src, dst);
            try { Directory.Delete(src, true); } catch { }
        }

        static void Copy(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) Copy(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
