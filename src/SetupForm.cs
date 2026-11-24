using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KosherLink
{
    /// <summary>
    /// Google Play edition setup: install components, Hebrew + Google account, WhatsApp from Google Play,
    /// register with a voice call and restore the Google Drive backup, link WhatsApp for Windows.
    /// </summary>
    class SetupForm : Form
    {
        public event Action Finished;

        readonly Label[] _steps = new Label[5];
        readonly CheckBox _license;
        readonly Button _btnInstall, _btnGoogle, _btnStore, _btnRegister, _btnDesktop, _btnFinish;
        readonly Label _help;
        readonly ProgressBar _progress;
        readonly TextBox _log;
        bool _running;

        static readonly string[] StepNames =
        {
            "בדיקת המחשב",
            "הורדת רכיבים רשמיים (פעם אחת, כ־1.8GB)",
            "הפעלת טלפון וירטואלי עם Google Play",
            "עברית, חשבון Google והתקנת וואטסאפ מהחנות",
            "רישום בשיחה קולית, שחזור הגיבוי וחיבור למחשב",
        };

        // the phone's own screens, opened in the phone window
        const string LanguageScreen = "am start -a android.settings.LOCALE_SETTINGS";
        const string AddGoogleAccount = "am start -a android.settings.ADD_ACCOUNT_SETTINGS --esa account_types com.google";
        const string WhatsAppInStore = "am start -a android.intent.action.VIEW -d 'market://details?id=com.whatsapp'";

        public SetupForm()
        {
            Text = Program.AppName + " – התקנה";
            Icon = Program.MakeIcon(Color.FromArgb(26, 115, 232));
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 740);
            MinimumSize = new Size(640, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Text = "וואטסאפ במחשב – גרסת Google Play",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                AutoSize = true, Margin = new Padding(0, 0, 0, 4)
            });
            root.Controls.Add(new Label
            {
                Text = "לבעלי טלפון כשר שהיה להם וואטסאפ וגיבו אותו ל־Google Drive. התוכנה מריצה ברקע טלפון וירטואלי עם Google Play, " +
                       "מתקינים בו את וואטסאפ מהחנות, נרשמים בשיחה קולית ומשחזרים את הגיבוי. כל המידע נשמר רק במחשב שלך:\n" + Paths.Root,
                AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 10)
            });

            for (int i = 0; i < _steps.Length; i++)
            {
                _steps[i] = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
                SetStep(i, "⬜");
                root.Controls.Add(_steps[i]);
            }

            var licRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0), WrapContents = false };
            _license = new CheckBox { Text = "אני מסכים לתנאי הרישיון של Android SDK (Google)", AutoSize = true, Checked = Config.GetBool("LicenseAccepted") };
            var licLink = new LinkLabel { Text = "קרא את התנאים", AutoSize = true, Margin = new Padding(8, 5, 0, 0) };
            licLink.LinkClicked += (s, e) => Process.Start("https://developer.android.com/studio/terms");
            licRow.Controls.Add(_license);
            licRow.Controls.Add(licLink);
            root.Controls.Add(licRow);

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 6) };
            _btnInstall = Btn("א. התקנה אוטומטית");
            _btnGoogle = Btn("ב. עברית וחשבון Google");
            _btnStore = Btn("ג. וואטסאפ מהחנות");
            _btnRegister = Btn("ד. רישום ושחזור גיבוי");
            _btnDesktop = Btn("ה. חיבור וואטסאפ למחשב");
            _btnFinish = Btn("סיום");
            buttons.Controls.AddRange(new Control[] { _btnInstall, _btnGoogle, _btnStore, _btnRegister, _btnDesktop, _btnFinish });
            root.Controls.Add(buttons);

            _help = new Label
            {
                AutoSize = true, MaximumSize = new Size(720, 0), BackColor = Color.FromArgb(232, 240, 254),
                Padding = new Padding(10), Margin = new Padding(0, 4, 0, 6)
            };
            root.Controls.Add(_help);

            _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(0, 4, 0, 4) };
            root.Controls.Add(_progress);

            _log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(248, 248, 248), Font = new Font("Segoe UI", 9f)
            };
            root.Controls.Add(_log);
            root.RowCount = root.Controls.Count;
            for (int i = 0; i < root.RowCount; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles[root.RowCount - 1] = new RowStyle(SizeType.Percent, 100);

            _btnInstall.Click += (s, e) => RunInstall();
            _btnGoogle.Click += (s, e) => GoogleAndLanguage();
            _btnStore.Click += (s, e) => WhatsAppFromStore();
            _btnRegister.Click += (s, e) => Register();
            _btnDesktop.Click += (s, e) => LinkDesktop();
            _btnFinish.Click += (s, e) => Finish();

            Action<string> onLog = line => AppendLog(line);
            Log.Line += onLog;
            FormClosed += (s, e) => Log.Line -= onLog;

            bool ready = AndroidSdk.ComponentsInstalled() && AndroidSdk.AvdExists();
            if (ready) for (int i = 0; i < 3; i++) SetStep(i, "✅");
            if (Config.GetBool("SetupDone")) { SetStep(3, "✅"); SetStep(4, "✅"); }
            _btnGoogle.Enabled = _btnStore.Enabled = _btnRegister.Enabled = _btnDesktop.Enabled = _btnFinish.Enabled = ready;

            string filter = FilterFound();
            _help.Text = (ready
                    ? "הרכיבים כבר מותקנים. ממשיכים בשלב ב׳ (עברית וחשבון Google)."
                    : "סמן את ההסכמה לתנאי הרישיון ולחץ \"א. התקנה אוטומטית\". ההורדה וההתקנה לוקחות כמה דקות, תלוי במהירות האינטרנט.")
                + (filter != null ? "\n\n⚠ " + FilterWarning(filter) : "");
            if (filter != null) _help.BackColor = Color.FromArgb(254, 243, 224);
        }

        /// <summary>Name of an internet filter whose certificate is in Windows (the Google Play phone can't take it), or null.</summary>
        static string FilterFound()
        {
            var names = new[] { new[] { "NetFree", "נטפרי" }, new[] { "Netspark", "נטספארק/רימון/אתרוג" }, new[] { "Etrog", "אתרוג" },
                                new[] { "Rimon", "רימון" }, new[] { "Noam", "נועם" }, new[] { "Nativ|Netiv", "נתיב" },
                                new[] { "Moreshet", "מורשת" }, new[] { "Yashar", "ישרנט" } };
            foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                var store = new X509Store(StoreName.Root, loc);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (var c in store.Certificates)
                        foreach (var n in names)
                            if (Regex.IsMatch(c.Subject, n[0], RegexOptions.IgnoreCase)) return n[1];
                }
                catch { }
                finally { store.Close(); }
            }
            return null;
        }

        static string FilterWarning(string filter)
        {
            return "המחשב גולש דרך סינון (" + filter + "). בגרסת Google Play אי אפשר להתקין בטלפון את תעודת הסינון, " +
                   "ולכן ייתכן שהכניסה לחשבון Google או השחזור מ־Google Drive ייחסמו. אם זה קורה: בקשו מהסינון לפתוח את Google ואת וואטסאפ, " +
                   "או השתמשו בגרסה הרגילה של KosherLink (בלי Google Play), שבה אפשר להתקין את תעודת הסינון.";
        }

        static Button Btn(string text)
        {
            return new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 8, 6) };
        }

        void SetStep(int i, string mark)
        {
            Action a = () => _steps[i].Text = mark + "  " + (i + 1) + ". " + StepNames[i];
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        void AppendLog(string line)
        {
            if (IsDisposed) return;
            Action a = () => { _log.AppendText(line + "\r\n"); };
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        void SetProgress(int p)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() => _progress.Value = Math.Max(0, Math.Min(100, p))));
        }

        void SetHelp(string text)
        {
            BeginInvoke((Action)(() => _help.Text = text));
        }

        void RunInstall()
        {
            if (_running) return;
            if (!_license.Checked)
            {
                Program.Msg("כדי להוריד את הרכיבים של Google צריך לסמן את ההסכמה לתנאי הרישיון.", MessageBoxIcon.Information);
                return;
            }
            string filter = FilterFound();
            if (filter != null && !Config.GetBool("FilterWarned"))
            {
                if (!Program.Ask(FilterWarning(filter) + "\n\nלהמשיך בהתקנה בכל זאת?", MessageBoxIcon.Warning, this)) return;
                Config.SetBool("FilterWarned", true);
            }
            Config.SetBool("LicenseAccepted", true);
            _running = true;
            _btnInstall.Enabled = false;
            Task.Run(() =>
            {
                int step = 0;
                try
                {
                    // 1. machine check
                    SetStep(0, "⏳");
                    var drive = new DriveInfo(Path.GetPathRoot(Paths.Root));
                    long freeGb = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                    Log.Write("מקום פנוי בכונן: " + freeGb + "GB");
                    if (freeGb < 16 && !AndroidSdk.ComponentsInstalled()) throw new Exception("צריך לפחות 16GB פנויים בכונן " + drive.Name);
                    SetStep(0, "✅");

                    // 2. download official components
                    step = 1; SetStep(1, "⏳");
                    AndroidSdk.InstallAll(SetProgress);
                    PhoneViewer.Install(SetProgress);
                    SetStep(1, "✅");

                    string accel = AndroidSdk.AccelProblem();
                    if (accel != null)
                    {
                        SetStep(0, "❌");
                        BeginInvoke((Action)(() => OfferEnableHypervisor(accel)));
                        return;
                    }

                    // 3. virtual phone with Google Play
                    step = 2; SetStep(2, "⏳");
                    if (!AndroidSdk.AvdExists()) AndroidSdk.CreateAvd();
                    EnsurePhone();
                    SetStep(2, "✅");
                    SetProgress(100);

                    Log.Write("ההתקנה הושלמה!");
                    BeginInvoke((Action)(() =>
                    {
                        _btnGoogle.Enabled = _btnStore.Enabled = _btnRegister.Enabled = _btnDesktop.Enabled = _btnFinish.Enabled = true;
                        _help.Text = "ההתקנה הושלמה ✔  עכשיו לחץ \"ב. עברית וחשבון Google\".";
                    }));
                }
                catch (Exception ex)
                {
                    SetStep(step, "❌");
                    Log.Write("שגיאה: " + ex.Message);
                    SetHelp("משהו נכשל: " + ex.Message + "\nאפשר ללחוץ שוב על \"התקנה אוטומטית\" – ההורדה תמשיך מאיפה שעצרה.");
                    BeginInvoke((Action)(() => _btnInstall.Enabled = true));
                }
                finally { _running = false; }
            });
        }

        void OfferEnableHypervisor(string details)
        {
            _btnInstall.Enabled = true;
            _help.Text = "כדי שהטלפון הווירטואלי ירוץ קל ומהר צריך להפעיל ב־Windows את הרכיב \"Windows Hypervisor Platform\".";
            if (!Program.Ask(
                "המחשב צריך את הרכיב \"Windows Hypervisor Platform\" (רכיב רשמי של Windows).\n\n" +
                "ללחוץ \"כן\" כדי לפתוח את חלון \"תכונות Windows\"? שם מסמנים את \"Windows Hypervisor Platform\" (פלטפורמת Hypervisor של Windows), לוחצים אישור, מפעילים מחדש את המחשב ומריצים שוב את ההתקנה.\n\n" +
                "אם זה לא עוזר, ייתכן שצריך להפעיל Virtualization (VT-x) ב־BIOS.\n\nפרטים טכניים:\n" + details.Trim(), MessageBoxIcon.Warning, this)) return;
            // the standard Windows dialog - the user ticks the feature themselves
            try { Process.Start("optionalfeatures.exe"); }
            catch (Exception ex) { Log.Write("פתיחת תכונות Windows נכשלה: " + ex.Message); }
        }

        /// <summary>Starts the phone if needed and waits until it is ready.</summary>
        static void EnsurePhone()
        {
            bool wasRunning = Emulator.IsRunning;
            if (!wasRunning) Emulator.Start();
            Log.Write("ממתין שהטלפון הווירטואלי יעלה...");
            if (!Emulator.WaitBoot(900)) throw new Exception("הטלפון הווירטואלי לא עלה. פרטים בקובץ " + Paths.LogFile);
            if (!wasRunning) Emulator.Tune();
            if (!PhoneViewer.IsInstalled) PhoneViewer.Install(null);
        }

        /// <summary>Runs phone work in the background and shows the phone window with the given screen.</summary>
        void OnPhone(Func<string> decideScreen)
        {
            Task.Run(() =>
            {
                try
                {
                    EnsurePhone();
                    PhoneViewer.Show(decideScreen());
                }
                catch (Exception ex) { Log.Write("פתיחת הטלפון נכשלה: " + ex.Message); SetHelp("פתיחת הטלפון נכשלה: " + ex.Message); }
            });
        }

        static bool HebrewSet { get { return Proc.Shell("getprop persist.sys.locale").StartsWith("he") || Proc.Shell("getprop persist.sys.locale").StartsWith("iw"); } }
        static bool GoogleSignedIn { get { return Proc.Shell("dumpsys account").Contains("type=com.google"); } }

        // ב. Hebrew first (once), then the Google account
        void GoogleAndLanguage()
        {
            SetStep(3, "⏳");
            SetHelp("פותח את הטלפון...");
            OnPhone(() =>
            {
                if (!HebrewSet)
                {
                    SetHelp(
                        "שפת הטלפון – פעם אחת:\n" +
                        "1. בחלון הטלפון שנפתח לחץ \"Add a language\" (הוסף שפה).\n" +
                        "2. למעלה, בשורת החיפוש, הקלד באנגלית Hebrew, לחץ על \"עברית\" ואז על \"ישראל\".\n" +
                        "3. לחץ \"Change\" / \"Set as default\" כדי שהיא תהיה השפה הראשית (או גרור את עברית לראש הרשימה).\n" +
                        "4. לחץ שוב על \"ב. עברית וחשבון Google\" כדי להמשיך לכניסה לחשבון.\n\n" +
                        "(אפשר לדלג על העברית – הטלפון יישאר באנגלית, ווואטסאפ במחשב יהיה בעברית בכל מקרה.)");
                    return LanguageScreen;
                }
                if (!GoogleSignedIn)
                {
                    SetHelp(
                        "כניסה לחשבון Google:\n" +
                        "1. בחלון הטלפון הקלד את כתובת ה־Gmail של החשבון שאליו גיבית את וואטסאפ, ואחר כך את הסיסמה.\n" +
                        "   את הסיסמה מקלידים רק אתם, בחלון הטלפון. התוכנה לא רואה ולא שומרת אותה.\n" +
                        "2. אם מתבקש אימות נוסף (קוד או אישור בטלפון אחר) – השלם אותו.\n" +
                        "3. אשר את התנאים של Google. גיבוי של הטלפון הווירטואלי עצמו ל־Google לא נחוץ – אפשר לכבות.\n" +
                        "4. בסיום לחץ \"ג. וואטסאפ מהחנות\".");
                    return AddGoogleAccount;
                }
                SetHelp("העברית והחשבון של Google כבר מוגדרים ✔  עכשיו לחץ \"ג. וואטסאפ מהחנות\".");
                return null;
            });
        }

        // ג. WhatsApp from Google Play (Play keeps it updated from then on)
        void WhatsAppFromStore()
        {
            SetHelp("פותח את הטלפון...");
            OnPhone(() =>
            {
                if (!GoogleSignedIn)
                {
                    SetHelp("קודם צריך להיכנס לחשבון Google – לחץ \"ב. עברית וחשבון Google\".");
                    return null;
                }
                if (WhatsAppAndroid.IsInstalled)
                {
                    SetStep(3, "✅");
                    SetHelp("וואטסאפ כבר מותקן ✔ (העדכונים יגיעו מ־Google Play)  עכשיו לחץ \"ד. רישום ושחזור גיבוי\".");
                    return null;
                }
                SetHelp(
                    "התקנת וואטסאפ מ־Google Play:\n" +
                    "1. בחלון הטלפון נפתח דף WhatsApp Messenger בחנות. לחץ \"Install\" / \"התקן\".\n" +
                    "2. חכה שההתקנה תסתיים (כמה דקות).\n" +
                    "3. לחץ \"ד. רישום ושחזור גיבוי\".\n\n" +
                    "שים לב שזה WhatsApp Messenger של WhatsApp LLC.");
                return WhatsAppInStore;
            });
        }

        // ד. register with a voice call, then restore from Google Drive
        void Register()
        {
            SetHelp("פותח את הטלפון...");
            OnPhone(() =>
            {
                if (!WhatsAppAndroid.IsInstalled)
                {
                    SetHelp("וואטסאפ עוד לא מותקן – לחץ \"ג. וואטסאפ מהחנות\".");
                    return null;
                }
                SetStep(4, "⏳");
                SetHelp(
                    "רישום וואטסאפ ושחזור הגיבוי:\n" +
                    "1. בחלון הטלפון לחץ \"הסכמה והמשך\" (Agree and continue).\n" +
                    "2. בחר ישראל והקלד את המספר של הטלפון הכשר (בלי ה־0 הראשון).\n" +
                    "3. לא יגיע SMS. חכה שיופיע \"התקשרו אליי\" (Call me) ולחץ עליו. ענה בטלפון הכשר והקלד את הקוד ששמעת.\n" +
                    "4. וואטסאפ יבקש גישה לאנשי קשר ולקבצים – אשר, כדי שיוכל למצוא את הגיבוי.\n" +
                    "5. יופיע \"שחזור גיבוי\" (Restore backup) מ־Google Drive – לחץ \"שחזור\" וחכה עד הסוף. זה יכול לקחת זמן, לפי גודל הגיבוי.\n" +
                    "   לא מופיע? ודא שנכנסת לאותו חשבון Google שאליו גיבית, ושהמספר זהה.\n" +
                    "6. בסיום לחץ \"ה. חיבור וואטסאפ למחשב\".\n\n" +
                    "טיפ: להקלדה בעברית – העתק טקסט במחשב והדבק בחלון הטלפון עם Ctrl+V.");
                return WhatsAppAndroid.LaunchCommand;
            });
        }

        void LinkDesktop()
        {
            Task.Run(() =>
            {
                bool installed = WhatsAppDesktop.IsInstalled;
                BeginInvoke((Action)(() =>
                {
                    if (!installed)
                    {
                        _help.Text =
                            "נפתח דף וואטסאפ ל־Windows בחנות של מיקרוסופט. לחץ שם \"התקן\" (Install / Get).\n" +
                            "בסיום ההתקנה חזור לכאן ולחץ שוב על \"ה. חיבור וואטסאפ למחשב\".\n\n" +
                            "(אפשר גם לוותר על השלב הזה ולהשתמש רק בחלון הטלפון הווירטואלי.)";
                        WhatsAppDesktop.OpenStorePage();
                        return;
                    }
                    _help.Text =
                        "חיבור וואטסאפ ל־Windows לחשבון:\n" +
                        "1. בוואטסאפ ל־Windows שנפתח לחץ \"כניסה עם מספר טלפון\" (Log in with phone number), הקלד את המספר – יוצג קוד בן 8 תווים.\n" +
                        "2. בחלון הטלפון הווירטואלי תופיע הודעה לחיבור מכשיר – לחץ עליה והקלד את הקוד.\n" +
                        "   אם אין הודעה: ⋮ (שלוש נקודות) ← מכשירים מקושרים ← קישור מכשיר ← \"קישור באמצעות מספר טלפון\" ← הקלד את הקוד.\n" +
                        "3. אחרי שההודעות מופיעות במחשב – לחץ \"סיום\".";
                    WhatsAppDesktop.Launch();
                    Task.Run(() => { try { PhoneViewer.Show(); } catch { } });
                }));
            });
        }

        void Finish()
        {
            Config.SetBool("SetupDone", true);
            try { AutoStart.Enabled = true; } catch { }
            SetStep(3, "✅");
            SetStep(4, "✅");
            Program.Msg("הכל מוכן!\n\nהתוכנה תפעל ברקע (סמל כחול ליד השעון) ותעלה אוטומטית עם Windows.\n" +
                        "לחיצה כפולה על הסמל פותחת את וואטסאפ.\nוואטסאפ מתעדכן דרך Google Play.", MessageBoxIcon.Information);
            var h = Finished; if (h != null) h();
            Close();
        }
    }
}
