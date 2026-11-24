using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace KosherLink
{
    /// <summary>
    /// Internet filters (NetFree, Netspark, Etrog, Rimon...) inspect HTTPS with their own root certificate.
    /// Nothing here runs by itself: the user picks the filter in FilterForm, and its certificate is taken from
    /// Windows (where the filter installed it), from the filter's official download, or from a file the user picks,
    /// and added to the virtual phone's system store. Android 11 reads /system/etc/security/cacerts (read-only),
    /// so a tmpfs is mounted over it with the original certificates plus ours. The quick-boot snapshot keeps it;
    /// after a rare full restart of the phone it is gone, and the window shows that so the user can install again.
    /// </summary>
    static class FilterCert
    {
        public class Filter
        {
            public string Id, Name, Pattern, Url, Embedded;
            public Filter(string id, string name, string pattern, string url, string embedded)
            { Id = id; Name = name; Pattern = pattern; Url = url; Embedded = embedded; }
            public bool Matches(X509Certificate2 c) { return Regex.IsMatch(c.Subject, Pattern, RegexOptions.IgnoreCase); }
        }

        /// <summary>
        /// Known filters: how their certificate is named in Windows, the official download (tried first, NetFree only),
        /// and the copy shipped in the program (EmbeddedCerts). Rimon, Netspark and Etrog share one certificate.
        /// </summary>
        public static readonly Filter[] Known =
        {
            new Filter("NetFree", "נטפרי", @"\bNetFree\b", "https://netfree.link/cacert/united/x2/root_ca_x2_bundle.crt", EmbeddedCerts.NetFree),
            new Filter("Netspark", "נטספארק", @"Netspark", null, EmbeddedCerts.NetsparkRim),
            new Filter("Rimon", "רימון", @"Netspark RIM|Rimon", null, EmbeddedCerts.NetsparkRim),
            new Filter("Etrog", "אתרוג", @"Netspark RIM|Etrog", null, EmbeddedCerts.NetsparkRim),
            new Filter("Noam", "נועם", @"\bNoam", null, null),
            new Filter("Nativ", "נתיב", @"Nativ|Netiv", null, null),
            new Filter("Moreshet", "מורשת", @"Moreshet", null, null),
            new Filter("Yesh", "ישרנט", @"Yasharnet|Yashar", null, null),
        };

        /// <summary>The certificates shipped in the program for this filter (empty if none).</summary>
        public static List<X509Certificate2> Builtin(Filter f)
        {
            return f.Embedded == null ? new List<X509Certificate2>() : Parse(Encoding.ASCII.GetBytes(f.Embedded)).Where(f.Matches).ToList();
        }

        /// <summary>What the user chose, shown in the window (kept next to the certificates, not in config.ini).</summary>
        public static string Choice
        {
            get { string p = Path.Combine(Dir, "choice.txt"); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
            set { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, "choice.txt"), value ?? ""); }
        }

        const string PhoneDir = "/data/local/tmp/kl-certs";
        const string SysDir = "/system/etc/security/cacerts";

        /// <summary>Certificates waiting on the PC to be put in the phone (Android file names: &lt;hash&gt;.N).</summary>
        public static string Dir { get { return Path.Combine(Paths.Root, "filter-certs"); } }

        public static Filter Find(string idOrName)
        {
            return Known.FirstOrDefault(f => f.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || f.Name == idOrName);
        }

        public static Filter Owner(X509Certificate2 c)
        {
            return Known.FirstOrDefault(f => Regex.IsMatch(c.Subject, f.Pattern, RegexOptions.IgnoreCase));
        }

        /// <summary>Filter certificates installed in Windows (both the user's and the machine's root stores).</summary>
        public static List<X509Certificate2> InWindows(Filter only = null)
        {
            var res = new List<X509Certificate2>();
            foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                var store = new X509Store(StoreName.Root, loc);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (var c in store.Certificates)
                    {
                        bool match = only != null ? only.Matches(c) : Owner(c) != null;
                        if (match && !res.Any(x => x.Thumbprint == c.Thumbprint)) res.Add(c);
                    }
                }
                catch { }
                finally { store.Close(); }
            }
            return res;
        }

        /// <summary>Reads one or more certificates from a .crt/.cer/.pem file (PEM text or binary DER).</summary>
        public static List<X509Certificate2> Parse(byte[] data)
        {
            var res = new List<X509Certificate2>();
            string text = Encoding.ASCII.GetString(data);
            var pems = Regex.Matches(text, "-----BEGIN CERTIFICATE-----(.+?)-----END CERTIFICATE-----", RegexOptions.Singleline);
            if (pems.Count > 0)
            {
                foreach (Match m in pems)
                {
                    try { res.Add(new X509Certificate2(Convert.FromBase64String(Regex.Replace(m.Groups[1].Value, @"\s", "")))); }
                    catch { }
                }
            }
            else
            {
                try { res.Add(new X509Certificate2(data)); } catch { }
            }
            return res;
        }

        /// <summary>
        /// The file name Android expects: OpenSSL's "subject_hash_old" (first 4 bytes of the MD5 of the DER-encoded
        /// subject, little-endian) followed by .0
        /// </summary>
        public static string AndroidHash(X509Certificate2 c)
        {
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(c.SubjectName.RawData);
                uint v = (uint)(h[0] | h[1] << 8 | h[2] << 16 | h[3] << 24);
                return v.ToString("x8");
            }
        }

        static string Pem(X509Certificate2 c)
        {
            string b64 = Convert.ToBase64String(c.RawData);
            var sb = new StringBuilder("-----BEGIN CERTIFICATE-----\n");
            for (int i = 0; i < b64.Length; i += 64) sb.Append(b64.Substring(i, Math.Min(64, b64.Length - i))).Append('\n');
            return sb.Append("-----END CERTIFICATE-----\n").ToString();
        }

        /// <summary>Saves certificates on the PC for the phone. Returns how many new ones were added.</summary>
        public static int Save(IEnumerable<X509Certificate2> certs)
        {
            Directory.CreateDirectory(Dir);
            int added = 0;
            foreach (var c in certs)
            {
                if (c.PublicKey.Oid.Value == "1.3.101.112") continue; // Ed25519: Android 11 cannot use it
                if (c.NotAfter < DateTime.Now) continue;
                string pem = Pem(c);
                string hash = AndroidHash(c);
                bool exists = false;
                int n = 0;
                for (; File.Exists(Path.Combine(Dir, hash + "." + n)); n++)
                    if (File.ReadAllText(Path.Combine(Dir, hash + "." + n)) == pem) { exists = true; break; }
                if (exists) continue;
                File.WriteAllText(Path.Combine(Dir, hash + "." + n), pem);
                Log.Write("תעודת סינון נשמרה: " + c.GetNameInfo(X509NameType.SimpleName, false) + " (" + hash + "." + n + ")");
                added++;
            }
            return added;
        }

        public static string[] Saved
        {
            get { return Directory.Exists(Dir) ? Directory.GetFiles(Dir).Where(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9a-f]{8}\.\d+$")).ToArray() : new string[0]; }
        }

        /// <summary>Names of the saved certificates, for the menu ("נטספארק", or the certificate's own name).</summary>
        public static string Describe()
        {
            if (Saved.Length > 0 && Choice.Length > 0) return Choice;
            var names = new List<string>();
            foreach (var f in Saved)
            {
                var c = Parse(File.ReadAllBytes(f)).FirstOrDefault();
                if (c == null) continue;
                var k = Owner(c);
                string n = k != null ? k.Name : c.GetNameInfo(X509NameType.SimpleName, false);
                if (!names.Contains(n)) names.Add(n);
            }
            return string.Join(", ", names);
        }

        /// <summary>Downloads a filter's certificate from its official site (only filters that publish one).</summary>
        public static List<X509Certificate2> Download(Filter f)
        {
            if (f.Url == null) return new List<X509Certificate2>();
            string tmp = Path.Combine(Paths.Downloads, "filter-" + f.Id + ".crt");
            Directory.CreateDirectory(Paths.Downloads);
            if (File.Exists(tmp)) File.Delete(tmp);
            Net.Download(f.Url, tmp, null, null);
            var certs = Parse(File.ReadAllBytes(tmp)).Where(f.Matches).ToList(); // only that filter's own CAs
            File.Delete(tmp);
            return certs;
        }

        /// <summary>
        /// Makes sure the saved certificates are in the running phone. Cheap when nothing changed.
        /// Returns true if the phone's store was changed (apps started before that should be restarted).
        /// </summary>
        public static bool Apply()
        {
            var files = Saved;
            if (files.Length == 0) return false;
            Proc.Root();
            string names = string.Join(" ", files.Select(Path.GetFileName));
            string check = RunScript("grep -q ' " + SysDir + " tmpfs' /proc/mounts || { echo 0; exit; }\ncd " + SysDir + " && ls " + names + " 2>/dev/null | wc -l");
            int present;
            if (int.TryParse(check.Trim(), out present) && present == files.Length) return false;

            Proc.Shell("mkdir -p " + PhoneDir);
            string o;
            foreach (var f in files)
                Proc.Run(Paths.Adb, "-s " + Proc.Serial + " push \"" + f + "\" " + PhoneDir + "/", out o, 60000);
            // the original certificates are copied aside, a tmpfs goes over the folder, and both sets are put in
            string res = RunScript(
                "set -e\n" +
                "if ! grep -q ' " + SysDir + " tmpfs' /proc/mounts; then\n" +
                "  rm -rf /data/local/tmp/kl-sys; mkdir -p /data/local/tmp/kl-sys\n" +
                "  cp -p " + SysDir + "/* /data/local/tmp/kl-sys/\n" +
                "  mount -t tmpfs tmpfs " + SysDir + "\n" +
                "  cp -p /data/local/tmp/kl-sys/* " + SysDir + "/\n" +
                "  rm -rf /data/local/tmp/kl-sys\n" +
                "fi\n" +
                "cp " + PhoneDir + "/* " + SysDir + "/\n" +
                "chown root:root " + SysDir + " " + SysDir + "/*\n" +
                "chmod 755 " + SysDir + "\nchmod 644 " + SysDir + "/*\n" +
                "chcon u:object_r:system_file:s0 " + SysDir + " " + SysDir + "/*\n" +
                "echo KL-OK");
            if (!res.Contains("KL-OK")) throw new Exception("התקנת תעודת הסינון בטלפון נכשלה: " + res.Trim());
            Log.Write("תעודות סינון הותקנו בטלפון: " + Describe());
            return true;
        }

        /// <summary>Runs a shell script on the phone (pushed as a file: quotes don't survive adb's Windows command line).</summary>
        static string RunScript(string script)
        {
            Directory.CreateDirectory(Paths.Downloads);
            string local = Path.Combine(Paths.Downloads, "kl-script.sh");
            File.WriteAllText(local, script.Replace("\r", "") + "\n", new UTF8Encoding(false));
            string o;
            Proc.Run(Paths.Adb, "-s " + Proc.Serial + " push \"" + local + "\" /data/local/tmp/kl-script.sh", out o, 60000);
            File.Delete(local);
            return Proc.Shell("sh /data/local/tmp/kl-script.sh");
        }

        /// <summary>Removes the certificates we installed, from the phone (if it is running) and from the PC.</summary>
        public static void Clear()
        {
            var files = Saved;
            if (files.Length == 0) return;
            if (AndroidSdk.AvdExists() && Emulator.IsRunning && Emulator.IsBooted)
            {
                Proc.Root();
                RunScript("for f in " + string.Join(" ", files.Select(Path.GetFileName)) + "; do rm -f " + SysDir + "/$f; done\nrm -rf " + PhoneDir);
            }
            foreach (var f in files) File.Delete(f);
            Choice = "";
            Log.Write("תעודות הסינון הוסרו");
        }

        /// <summary>
        /// Whether the chosen certificates are really in the phone right now (they disappear after a full restart
        /// of the phone; the user then installs again from the window). Null when the phone is not running.
        /// </summary>
        public static bool? OnPhone()
        {
            var files = Saved;
            if (files.Length == 0) return false;
            if (!AndroidSdk.AvdExists() || !Emulator.IsRunning || !Emulator.IsBooted) return null;
            Proc.Root();
            string n = RunScript("cd " + SysDir + " && ls " + string.Join(" ", files.Select(Path.GetFileName)) + " 2>/dev/null | wc -l");
            int present;
            return int.TryParse(n.Trim(), out present) && present == files.Length;
        }
    }
}
