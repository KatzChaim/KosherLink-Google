using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KosherLink
{
    /// <summary>
    /// KosherLink Google setup: install components + phone + WhatsApp, filter certificate (only behind a filter),
    /// Google account, register with a voice call and restore the Google Drive backup, link WhatsApp for Windows.
    /// </summary>
    class SetupForm : Form
    {
        public event Action Finished;

        readonly Label[] _steps = new Label[5];
        readonly CheckBox _license;
        readonly Button _btnInstall, _btnFilter, _btnGoogle, _btnRegister, _btnDesktop, _btnFinish;
        readonly Label _help;
        readonly ProgressBar _progress;
        readonly TextBox _log;
        bool _running;

        static readonly string[] StepNames =
        {
            "בדיקת המחשב",
            "הורדת רכיבים רשמיים (פעם אחת, כ־1.8GB)",
            "הפעלת טלפון וירטואלי עם שירותי Google והתקנת וואטסאפ",
            "תעודת סינון (רק למי שגולש דרך סינון) וכניסה לחשבון Google",
            "רישום בשיחה קולית, שחזור הגיבוי מ־Google Drive וחיבור למחשב",
        };

        const string AddGoogleAccount = "am start -a android.settings.ADD_ACCOUNT_SETTINGS --esa account_types com.google";

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
                Text = "וואטסאפ במחשב – עם שחזור מ־Google Drive",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                AutoSize = true, Margin = new Padding(0, 0, 0, 4)
            });
            root.Controls.Add(new Label
            {
                Text = "לבעלי טלפון כשר שהיה להם וואטסאפ וגיבו אותו ל־Google Drive. התוכנה מריצה ברקע טלפון וירטואלי עם שירותי Google, " +
                       "נכנסים בו לחשבון Google, נרשמים בשיחה קולית ומשחזרים את הגיבוי. כל המידע נשמר רק במחשב שלך:\n" + Paths.Root,
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
            _btnFilter = Btn("ב. תעודת סינון (למי שיש סינון)");
            _btnGoogle = Btn("ג. חשבון Google");
            _btnRegister = Btn("ד. רישום ושחזור גיבוי");
            _btnDesktop = Btn("ה. חיבור וואטסאפ למחשב");
            _btnFinish = Btn("סיום");
            buttons.Controls.AddRange(new Control[] { _btnInstall, _btnFilter, _btnGoogle, _btnRegister, _btnDesktop, _btnFinish });
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
            _btnFilter.Click += (s, e) => { using (var f = new FilterForm(null)) f.ShowDialog(this); };
            _btnGoogle.Click += (s, e) => GoogleAccount();
            _btnRegister.Click += (s, e) => Register();
            _btnDesktop.Click += (s, e) => LinkDesktop();
            _btnFinish.Click += (s, e) => Finish();

            Action<string> onLog = line => AppendLog(line);
            Log.Line += onLog;
            FormClosed += (s, e) => Log.Line -= onLog;

            bool ready = AndroidSdk.ComponentsInstalled() && AndroidSdk.AvdExists();
            if (ready) for (int i = 0; i < 3; i++) SetStep(i, "✅");
            if (Config.GetBool("SetupDone")) { SetStep(3, "✅"); SetStep(4, "✅"); }
            _btnFilter.Enabled = _btnGoogle.Enabled = _btnRegister.Enabled = _btnDesktop.Enabled = _btnFinish.Enabled = ready;
            _help.Text = ready
                ? "הרכיבים כבר מותקנים. " + NextAfterInstall()
                : "סמן את ההסכמה לתנאי הרישיון ולחץ \"א. התקנה אוטומטית\". ההורדה וההתקנה לוקחות כמה דקות, תלוי במהירות האינטרנט.";
        }

        /// <summary>Names of internet filters whose certificate is in Windows ("" if none).</summary>
        static string FilterNames()
        {
            var certs = FilterCert.InWindows();
            return string.Join(" / ", FilterCert.Known.Where(f => certs.Any(f.Matches)).Select(f => f.Name));
        }

        /// <summary>Behind a filter the Google sign-in fails until the filter's certificate is in the phone.</summary>
        static string NextAfterInstall()
        {
            string filter = FilterNames();
            if (filter.Length > 0 && FilterCert.Saved.Length == 0)
                return "המחשב גולש דרך סינון (" + filter + "). לחץ קודם \"ב. תעודת סינון\" – בלי התעודה הכניסה לחשבון Google תיכשל. אחר כך \"ג. חשבון Google\".";
            return "עכשיו לחץ \"ג. חשבון Google\"." + (filter.Length == 0 ? " (שלב ב׳ נחוץ רק למי שגולש דרך סינון.)" : "");
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

                    // 3. phone with Google services (Hebrew, extra Google apps hidden) + WhatsApp from whatsapp.com
                    step = 2; SetStep(2, "⏳");
                    if (!AndroidSdk.AvdExists()) AndroidSdk.CreateAvd();
                    EnsurePhone();
                    WhatsAppAndroid.Update(!WhatsAppAndroid.IsInstalled, SetProgress);
                    SetStep(2, "✅");
                    SetProgress(100);

                    Log.Write("ההתקנה הושלמה!");
                    string next = NextAfterInstall();
                    BeginInvoke((Action)(() =>
                    {
                        _btnFilter.Enabled = _btnGoogle.Enabled = _btnRegister.Enabled = _btnDesktop.Enabled = _btnFinish.Enabled = true;
                        _help.Text = "ההתקנה הושלמה ✔  " + next;
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

        /// <summary>Runs phone work in the background and shows the phone window with the given screen ("" = don't open).</summary>
        void OnPhone(Func<string> decideScreen)
        {
            Task.Run(() =>
            {
                try
                {
                    EnsurePhone();
                    string screen = decideScreen();
                    if (screen != "") PhoneViewer.Show(screen);
                }
                catch (Exception ex) { Log.Write("פתיחת הטלפון נכשלה: " + ex.Message); SetHelp("פתיחת הטלפון נכשלה: " + ex.Message); }
            });
        }

        static bool GoogleSignedIn
        {
            get { return Proc.Shell("dumpsys account").Split('\n').Any(l => l.Contains("Account {") && l.Contains("type=com.google")); }
        }

        // ג. Google account (behind a filter: only after its certificate is in the phone)
        void GoogleAccount()
        {
            string filter = FilterNames();
            if (filter.Length > 0 && FilterCert.Saved.Length == 0 &&
                !Program.Ask("המחשב גולש דרך סינון (" + filter + "), ותעודת הסינון עוד לא הותקנה בטלפון. בלי התעודה הכניסה לחשבון Google תיכשל.\n\n" +
                             "להמשיך בכל זאת? (מומלץ ללחוץ \"לא\" ואז \"ב. תעודת סינון\")", MessageBoxIcon.Warning, this)) return;
            SetStep(3, "⏳");
            SetHelp("פותח את הטלפון...");
            OnPhone(() =>
            {
                if (GoogleSignedIn)
                {
                    SetStep(3, "✅");
                    SetHelp("כבר נכנסת לחשבון Google ✔  עכשיו לחץ \"ד. רישום ושחזור גיבוי\".");
                    return "";
                }
                SetHelp(
                    "כניסה לחשבון Google:\n" +
                    "1. בחלון הטלפון הקלד את כתובת ה־Gmail של החשבון שאליו גיבית את וואטסאפ, ואחר כך את הסיסמה.\n" +
                    "   את הסיסמה מקלידים רק אתם, בחלון הטלפון. התוכנה לא רואה ולא שומרת אותה.\n" +
                    "2. אם מתבקש אימות נוסף (קוד או אישור בטלפון אחר) – השלם אותו.\n" +
                    "3. אשר את התנאים של Google. גיבוי של הטלפון הווירטואלי עצמו ל־Google לא נחוץ – אפשר לכבות.\n" +
                    "4. בסיום לחץ \"ד. רישום ושחזור גיבוי\".\n\n" +
                    "מופיע \"לא ניתן היה להיכנס – הייתה בעיה בתקשורת עם שרתי Google\"? כמעט תמיד זה סינון: התקן את תעודת הסינון (ב׳) ונסה שוב.");
                return AddGoogleAccount;
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
                    SetHelp("וואטסאפ עוד לא מותקן – לחץ \"א. התקנה אוטומטית\".");
                    return "";
                }
                SetStep(4, "⏳");
                SetHelp(
                    "רישום וואטסאפ ושחזור הגיבוי:\n" +
                    "1. בחלון הטלפון לחץ \"הסכמה והמשך\".\n" +
                    "2. בחר ישראל והקלד את המספר של הטלפון הכשר (בלי ה־0 הראשון).\n" +
                    "3. לא יגיע SMS. חכה שיופיע \"התקשרו אליי\" ולחץ עליו. ענה בטלפון הכשר והקלד את הקוד ששמעת.\n" +
                    "4. וואטסאפ יבקש גישה לאנשי קשר ולקבצים – אשר, כדי שיוכל למצוא את הגיבוי.\n" +
                    "5. יופיע \"שחזור גיבוי\" מ־Google Drive – לחץ \"שחזור\" וחכה עד הסוף. זה יכול לקחת זמן, לפי גודל הגיבוי.\n" +
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
                        "1. בוואטסאפ ל־Windows שנפתח לחץ \"כניסה עם מספר טלפון\", הקלד את המספר – יוצג קוד בן 8 תווים.\n" +
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
                        "לחיצה כפולה על הסמל פותחת את וואטסאפ.\nעדכונים לוואטסאפ מותקנים אוטומטית.", MessageBoxIcon.Information);
            var h = Finished; if (h != null) h();
            Close();
        }
    }
}
