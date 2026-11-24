using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KosherLink
{
    /// <summary>Keeps the official WhatsApp for Android up to date inside the virtual phone.</summary>
    static class WhatsAppAndroid
    {
        // Official direct download published by WhatsApp on whatsapp.com/android
        const string ApkUrl = "https://www.whatsapp.com/android/current/WhatsApp.apk";
        public const string Package = "com.whatsapp";

        public static bool IsInstalled
        {
            get { return Proc.Shell("pm path " + Package).Contains("package:"); }
        }

        public static string InstalledVersion
        {
            get
            {
                var m = Regex.Match(Proc.Shell("dumpsys package " + Package), @"versionName=(\S+)");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        /// <summary>Checks whatsapp.com for a newer build and installs it. Returns true if something was installed.</summary>
        /// <param name="confirm">Asked before an update (not a first install); returning false skips it until the next check.</param>
        public static bool Update(bool force, Action<int> percent, Func<bool> confirm = null)
        {
            string remote = Net.Fingerprint(ApkUrl);
            Config.Set("WaLastCheck", DateTime.UtcNow.ToString("o"));
            bool installed = IsInstalled;
            if (!force && installed && remote == Config.Get("WaFingerprint", ""))
            {
                Log.Write("וואטסאפ מעודכן (" + InstalledVersion + ")");
                return false;
            }
            if (installed && confirm != null && !confirm())
            {
                Log.Write("יש גרסת וואטסאפ חדשה – המשתמש בחר לא לעדכן עכשיו");
                return false;
            }

            string apk = Path.Combine(Paths.Downloads, "WhatsApp.apk");
            Log.Write(installed ? "נמצאה גרסת וואטסאפ חדשה, מורידה..." : "מוריד את וואטסאפ הרשמי...");
            Net.Download(ApkUrl, apk, null, (done, total) =>
            {
                if (total > 0 && percent != null) percent((int)(done * 100 / total));
            });

            CheckLooksLikeApk(apk);
            Log.Write("מתקין את וואטסאפ בטלפון הווירטואלי...");
            string o;
            Proc.Run(Paths.Adb, "-s " + Proc.Serial + " install -r -g \"" + apk + "\"", out o, 10 * 60 * 1000);
            if (!o.Contains("Success"))
                throw new Exception("התקנת וואטסאפ נכשלה: " + o.Trim());

            // Android itself only lets an update in if it is signed like the installed app, so the real risk is
            // the first install. Check who signed what Android actually installed.
            string signer = InstalledSigner();
            if (signer != OfficialSigner)
            {
                if (!installed) Proc.Shell("pm uninstall " + Package);
                File.Delete(apk);
                throw new Exception("קובץ וואטסאפ שהורד לא חתום על ידי WhatsApp (" + (signer ?? "לא ידוע") + ") – " +
                    (installed ? "בדוק את החיבור לאינטרנט" : "ההתקנה בוטלה"));
            }
            Log.Write("חתימת וואטסאפ אומתה (WhatsApp Inc.)");

            Config.Set("WaFingerprint", remote);
            File.Delete(apk);
            Log.Write("וואטסאפ הותקן: גרסה " + InstalledVersion);
            Proc.Shell("cmd deviceidle whitelist +" + Package);
            Proc.Shell("cmd appops set " + Package + " RUN_ANY_IN_BACKGROUND allow");
            return true;
        }

        /// <summary>SHA-256 of WhatsApp Inc.'s signing certificate (CN=Brian Acton, O=WhatsApp Inc.), the same for every release.</summary>
        public const string OfficialSigner = "3987d043d10aefaf5a8710b3671418fe57e0e19b653c9df82558feb5ffce5d44";

        /// <summary>Rejects obviously wrong downloads (e.g. an HTML page instead of the app) before installing.</summary>
        static void CheckLooksLikeApk(string apk)
        {
            var fi = new FileInfo(apk);
            byte[] head = new byte[2];
            using (var f = File.OpenRead(apk)) f.Read(head, 0, 2);
            if (fi.Length < 20L * 1024 * 1024 || head[0] != 'P' || head[1] != 'K')
            {
                File.Delete(apk);
                throw new Exception("מה שהורד מ־whatsapp.com הוא לא קובץ האפליקציה (" + Net.Mb(fi.Length) + "). ייתכן שסינון אינטרנט חוסם את ההורדה");
            }
        }

        /// <summary>SHA-256 of the certificate that signed the installed WhatsApp, read from Android's package database.</summary>
        public static string InstalledSigner()
        {
            Proc.Adb("-s " + Proc.Serial + " root", 30000); // the plain AOSP image allows this; the database is readable by root only
            Proc.Adb("-s " + Proc.Serial + " wait-for-device", 60000);
            string xml = Proc.Shell("cat /data/system/packages.xml");
            var pkg = Regex.Match(xml, "<package name=\"" + Regex.Escape(Package) + "\".*?</package>", RegexOptions.Singleline);
            if (!pkg.Success) return null;
            var idx = Regex.Match(pkg.Value, "<sigs[^>]*>\\s*<cert index=\"(\\d+)\"");
            if (!idx.Success) return null;
            // the certificate itself is written once, at the first package that uses this index
            var key = Regex.Match(xml, "<cert index=\"" + idx.Groups[1].Value + "\" key=\"([0-9a-fA-F]+)\"");
            if (!key.Success) return null;
            string hex = key.Groups[1].Value;
            var der = new byte[hex.Length / 2];
            for (int i = 0; i < der.Length; i++) der[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(der)).Replace("-", "").ToLowerInvariant();
        }

        public const string LaunchCommand = "monkey -p " + Package + " -c android.intent.category.LAUNCHER 1";

        public static void Launch()
        {
            Proc.Shell(LaunchCommand);
        }

        public static bool DueForCheck(TimeSpan every)
        {
            DateTime last;
            if (!DateTime.TryParse(Config.Get("WaLastCheck", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return true;
            return DateTime.UtcNow - last > every;
        }
    }

    /// <summary>The official WhatsApp app for Windows (from Microsoft Store, which updates it by itself).</summary>
    static class WhatsAppDesktop
    {
        const string StoreId = "9NKSQGP7F2NH";

        /// <summary>Store apps keep a per-user folder named after their package family.</summary>
        static string FamilyName()
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (var d in Directory.GetDirectories(packages, "*.WhatsAppDesktop_*"))
                return Path.GetFileName(d);
            return null;
        }

        public static bool IsInstalled { get { return FamilyName() != null; } }

        public static void OpenStorePage()
        {
            Process.Start("ms-windows-store://pdp/?productid=" + StoreId);
        }

        public static bool Launch()
        {
            string fn = FamilyName();
            if (fn == null) return false;
            Process.Start("explorer.exe", "shell:AppsFolder\\" + fn + "!App");
            return true;
        }
    }

    /// <summary>Shows the virtual phone's screen on demand (scrcpy, open source, very light).</summary>
    static class PhoneViewer
    {
        static Process _proc;

        public static bool IsInstalled { get { return File.Exists(Paths.Scrcpy); } }

        public static void Install(Action<int> percent)
        {
            string json = Net.GetString("https://api.github.com/repos/Genymobile/scrcpy/releases/latest");
            var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*scrcpy-win64-[^\"]*\\.zip)\"");
            if (!m.Success) throw new Exception("לא נמצא קובץ הצגת המסך להורדה");
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string dir = Path.Combine(Paths.Tools, "scrcpy");
            if (IsInstalled && AndroidSdk.InstalledRev(dir) == tag) return;

            string zip = Path.Combine(Paths.Downloads, "scrcpy.zip");
            Log.Write("מוריד את רכיב הצגת המסך (" + tag + ")...");
            if (File.Exists(zip)) File.Delete(zip);
            Net.Download(m.Groups[1].Value, zip, null, (d, t) => { if (t > 0 && percent != null) percent((int)(d * 100 / t)); });
            // GitHub publishes a SHA-256 for every release file: it sits in the same asset object, before its URL
            int prevAsset = json.LastIndexOf("\"browser_download_url\"", Math.Max(0, m.Index - 1), StringComparison.Ordinal);
            var dg = Regex.Match(json.Substring(prevAsset < 0 ? 0 : prevAsset + 1, m.Index - (prevAsset < 0 ? 0 : prevAsset + 1)),
                "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]{64})\"");
            if (!dg.Success) { File.Delete(zip); throw new Exception("לרכיב הצגת המסך אין חתימת SHA-256 ב־GitHub – לא מתקין"); }
            if (!Net.Sha256(zip).Equals(dg.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new Exception("רכיב הצגת המסך שהורד לא עבר בדיקת תקינות");
            }
            string tmp = Path.Combine(Paths.Root, "tmp-extract");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            var tops = Directory.GetDirectories(tmp);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Paths.Tools);
            Dirs.MoveRobust(tops.Length == 1 ? tops[0] : tmp, dir);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            File.WriteAllText(Path.Combine(dir, ".kosherlink-rev"), tag);
            File.Delete(zip);
        }

        public static bool IsOpen { get { return _proc != null && !_proc.HasExited; } }

        /// <param name="screen">A phone command that opens the screen to show (settings, store...); null = WhatsApp.</param>
        public static void Show(string screen = null)
        {
            if (screen == null && WhatsAppAndroid.IsInstalled) screen = WhatsAppAndroid.LaunchCommand;
            if (IsOpen)
            {
                if (screen != null) Proc.Shell(screen);
                BringToFront();
                return;
            }
            Emulator.ScreenOn();
            if (screen != null) Proc.Shell(screen);
            var psi = Proc.Psi(Paths.Scrcpy,
                "-s " + Proc.Serial + " --no-audio --max-fps 30 --video-bit-rate 4M --stay-awake"
                + " --window-title \"KosherLink Play - הטלפון הווירטואלי\"");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
            var output = new System.Collections.Generic.List<string>();
            DataReceivedEventHandler collect = (s, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
            var p = Process.Start(psi);
            p.OutputDataReceived += collect;
            p.ErrorDataReceived += collect;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) =>
            {
                try { Emulator.ScreenOff(); } catch { }
                try { Emulator.LowerPriority(); } catch { } // window closed: back to low priority
                try { lock (output) File.WriteAllLines(Path.Combine(Paths.Root, "scrcpy.log"), output); } catch { }
            };
            _proc = p;
            Emulator.LowerPriority(); // window open: normal priority, so taps react quickly
            BringToFront();
            if (p.WaitForExit(3000) && p.ExitCode != 0)
            {
                string last;
                lock (output) last = string.Join(" | ", output.FindAll(l => l.Contains("ERROR") || l.Contains("WARN")));
                throw new Exception("חלון הטלפון נסגר מיד (קוד " + p.ExitCode + ")" + (last.Length > 0 ? ": " + last : ""));
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        /// <summary>Makes sure the phone window is not hidden behind other windows.</summary>
        public static void BringToFront()
        {
            var p = _proc;
            for (int i = 0; i < 40; i++) // the window appears a few seconds after start
            {
                if (p == null || p.HasExited) return;
                p.Refresh();
                IntPtr h;
                try { h = p.MainWindowHandle; }
                catch (InvalidOperationException) { return; } // exited in the meantime
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9); // restore if minimized
                    SetForegroundWindow(h);
                    return;
                }
                System.Threading.Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// Self-update from the latest GitHub Release (config UpdateRepo, default KatzChaim/KosherLink).
    /// Two independent checks, both required:
    ///  1. KosherLink.exe matches the SHA-256 digest GitHub publishes for it (complete, uncorrupted download);
    ///  2. KosherLink.exe.sig is a valid RSA-SHA256 signature by the developer's private key, which never leaves
    ///     the developer's PC (see publish.ps1). So even someone who takes over the GitHub account cannot
    ///     push a program to the installations.
    /// UpdateUrl may instead point to a JSON like {"version":"1.0.1","url":"...exe","sha256":"...","sig":"...base64"}.
    /// </summary>
    static class AppUpdater
    {
        public const string DefaultRepo = "KatzChaim/KosherLink-Play";
        const string AssetName = "KosherLink-Play.exe";
        const string SigAssetName = "KosherLink-Play.exe.sig";

        /// <summary>Public half of the developer's update-signing key (the private half is in %USERPROFILE%\.kosherlink).</summary>
        const string PublicKey = "<RSAKeyValue><Modulus>1N8YSJcMnvmY5ACHONCtw7WHLK1IJArR2maqYR9CLoFBmjqs2hEDA0w7Xobb/KnmJqerJfXDjl6RbjOWeEGca8yRrEag7MXGqsBWZXwCMSWPB8I50qSKr+NxHzL1BmYdzzbTYvXx+V0m8LVE5m1RAffHiLWquLlN5ZfhXuEMcM0AOjZJxNKesrtlVSWFwGuxHfA9uw4r+8K42UgyuD4UuHm0DMg/v+QfkTgICKd/adc3fLaUccEuwU1zVl8l39hD1N7MDEhCRfi99awi+2StuSHUKHw8id87xmhiAyape8h9mvQmAUGoKimSZQPQTliX7rXWU/1VSU6NQfqajQr7m/K2IjIWpoCBp/FCIhHSePOAyDyZhP3nssYaq/8cUQFrz1do/ykxMI2yDrQfPFYWXx3YPQcwFd8EMPfZCtLzA8/uWKZ2zsUl94Gq2rVOnapRBDDMMmix87eh/RcPoA8VhnriYQFT81EJ9b5NIF2pCQW2zN3K8qEl6lUed+xd6g8N</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public class Info { public string Version, Url, Sha256, SigUrl, Sig; }

        public static bool SignatureValid(string file, byte[] sig)
        {
            using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider())
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                rsa.FromXmlString(PublicKey);
                return rsa.VerifyData(File.ReadAllBytes(file), sha, sig);
            }
        }

        /// <summary>Reads what the update source currently offers (null if nothing usable).</summary>
        public static Info Latest()
        {
            string custom = Config.Get("UpdateUrl", "");
            if (custom.Length > 0)
            {
                string j = Net.GetString(custom);
                return new Info
                {
                    Version = Field(j, "version"),
                    Url = Field(j, "url"),
                    Sha256 = Field(j, "sha256"),
                    Sig = Field(j, "sig")
                };
            }

            string repo = Config.Get("UpdateRepo", DefaultRepo);
            if (repo.Length == 0) return null;
            string json = Net.GetString("https://api.github.com/repos/" + repo + "/releases/latest");
            // the asset object: "name":"KosherLink.exe", ..., "digest":"sha256:...", ..., "browser_download_url":"..."
            var start = Regex.Match(json, "\"name\"\\s*:\\s*\"" + Regex.Escape(AssetName) + "\"");
            if (!start.Success) return null;
            int end = json.IndexOf("\"browser_download_url\"", start.Index, StringComparison.Ordinal);
            if (end < 0) return null;
            // from the asset's name up to (and including) its download URL
            string asset = json.Substring(start.Index, Math.Min(json.Length, end + 1000) - start.Index);
            var sigUrl = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*/" + Regex.Escape(SigAssetName) + ")\"");
            return new Info
            {
                Version = Field(json, "tag_name").TrimStart('v', 'V'),
                Url = Field(asset, "browser_download_url"),
                Sha256 = Regex.Match(asset, "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]{64})\"").Groups[1].Value,
                SigUrl = sigUrl.Success ? sigUrl.Groups[1].Value : ""
            };
        }

        static string Field(string json, string name)
        {
            return Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        }

        public static bool IsNewer(Info i)
        {
            Version remote;
            return i != null && i.Url.Length > 0 && Version.TryParse(i.Version, out remote) && remote > new Version(Program.Version);
        }

        /// <summary>Downloads the update next to <paramref name="exe"/> as .new and verifies it. Returns the .new path.</summary>
        public static string Download(Info i, string exe)
        {
            if (i.Sha256.Length != 64) throw new Exception("לעדכון אין חתימת SHA-256 – לא מתקין");
            string newExe = exe + ".new";
            if (File.Exists(newExe)) File.Delete(newExe);
            Net.Download(i.Url, newExe, null, null);
            if (!Net.Sha256(newExe).Equals(i.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newExe);
                throw new Exception("קובץ העדכון לא עבר בדיקת תקינות");
            }
            byte[] sig = null;
            try
            {
                string b64 = !string.IsNullOrEmpty(i.Sig) ? i.Sig : (i.SigUrl.Length > 0 ? Net.GetString(i.SigUrl) : "");
                if (b64.Trim().Length > 0) sig = Convert.FromBase64String(b64.Trim());
            }
            catch (FormatException) { }
            if (sig == null || !SignatureValid(newExe, sig))
            {
                File.Delete(newExe);
                throw new Exception("העדכון לא חתום בחתימה של המפתח – לא מתקין (" + (sig == null ? "אין חתימה" : "חתימה לא תקינה") + ")");
            }
            return newExe;
        }

        /// <param name="confirm">Gets the new version number; returning false skips the update until the next check.</param>
        public static bool CheckAndApply(Func<string, bool> confirm = null)
        {
            Config.Set("AppLastCheck", DateTime.UtcNow.ToString("o"));
            var i = Latest();
            if (!IsNewer(i)) return false;
            if (confirm != null && !confirm(i.Version))
            {
                Log.Write("יש גרסה חדשה של התוכנה (" + i.Version + ") – המשתמש בחר לא לעדכן עכשיו");
                return false;
            }

            Log.Write("נמצאה גרסה חדשה של התוכנה: " + i.Version + " – מוריד...");
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string newExe = Download(i, exe);
            // Windows allows renaming a running program: keep the old one aside, put the new one in place.
            string oldExe = exe + ".old";
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(exe, oldExe);
            File.Move(newExe, exe);
            Log.Write("התוכנה עודכנה לגרסה " + i.Version + " – מפעיל מחדש");
            Process.Start(exe, "--tray --after-update");
            return true; // caller exits the app; the new version deletes the .old file
        }
    }

    static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "KosherLink-Play";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && k.GetValue(Name) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) k.SetValue(Name, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
                    else k.DeleteValue(Name, false);
                }
            }
        }
    }
}
