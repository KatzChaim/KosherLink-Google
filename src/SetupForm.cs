using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KosherLink
{
    /// <summary>Step-by-step setup: install components, register with a voice call, link WhatsApp for Windows.</summary>
    class SetupForm : Form
    {
        public event Action Finished;

        readonly Label[] _steps = new Label[5];
        readonly CheckBox _license;
        readonly Button _btnInstall, _btnRestore, _btnPhone, _btnDesktop, _btnFinish;
        readonly Label _help;
        readonly ProgressBar _progress;
        readonly TextBox _log;
        bool _running;

        static readonly string[] StepNames =
        {
            "בדיקת המחשב",
            "הורדת רכיבים רשמיים (פעם אחת, כ־1.2GB)",
            "הפעלת טלפון וירטואלי קל ברקע",
            "התקנת וואטסאפ הרשמי",
            "רישום עם שיחה קולית וחיבור וואטסאפ למחשב",
        };

        public SetupForm()
        {
            Text = Program.AppName + " – התקנה";
            Icon = Program.MakeIcon(Color.FromArgb(26, 115, 232));
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 720);
            MinimumSize = new Size(640, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Text = "וואטסאפ במחשב – לבעלי טלפון כשר",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                AutoSize = true, Margin = new Padding(0, 0, 0, 4)
            });
            root.Controls.Add(new Label
            {
                Text = "התוכנה מריצה ברקע את וואטסאפ הרשמי בתוך טלפון וירטואלי קטן. האימות נעשה בשיחה קולית לטלפון הכשר. כל המידע נשמר רק במחשב שלך:\n" + Paths.Root,
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
            _btnPhone = Btn("ב. רישום (שיחה קולית)");
            _btnDesktop = Btn("ג. חיבור וואטסאפ למחשב");
            _btnFinish = Btn("סיום");
            _btnRestore = Btn("שחזור גיבוי");
            buttons.Controls.AddRange(new Control[] { _btnInstall, _btnRestore, _btnPhone, _btnDesktop, _btnFinish });
            root.Controls.Add(buttons);

            _help = new Label
            {
                AutoSize = true, MaximumSize = new Size(720, 0), BackColor = Color.FromArgb(240, 248, 243),
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
            _btnPhone.Click += (s, e) => StartRegistration();
            _btnRestore.Click += (s, e) => RestoreBackup();
            _btnDesktop.Click += (s, e) => LinkDesktop();
            _btnFinish.Click += (s, e) => Finish();

            Action<string> onLog = line => AppendLog(line);
            Log.Line += onLog;
            FormClosed += (s, e) => Log.Line -= onLog;

            bool ready = AndroidSdk.ComponentsInstalled() && AndroidSdk.AvdExists();
            if (ready) for (int i = 0; i < 4; i++) SetStep(i, "✅");
            if (Config.GetBool("SetupDone")) SetStep(4, "✅");
            _btnPhone.Enabled = ready;
            _btnRestore.Enabled = ready;
            _btnDesktop.Enabled = ready;
            _btnFinish.Enabled = ready;
            _help.Text = ready
                ? "הרכיבים כבר מותקנים. אפשר להמשיך לשלב ב׳ (רישום) או ג׳ (חיבור למחשב)."
                : "סמן את ההסכמה לתנאי הרישיון ולחץ \"א. התקנה אוטומטית\". ההורדה וההתקנה לוקחות כמה דקות, תלוי במהירות האינטרנט.";
        }

        static Button Btn(string text)
        {
            return new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 8, 0) };
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
                    if (freeGb < 15 && !AndroidSdk.ComponentsInstalled()) throw new Exception("צריך לפחות 15GB פנויים בכונן " + drive.Name);
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

                    // 3. virtual phone
                    step = 2; SetStep(2, "⏳");
                    if (!AndroidSdk.AvdExists()) AndroidSdk.CreateAvd();
                    Emulator.Start();
                    Log.Write("ממתין שהטלפון הווירטואלי יעלה (בפעם הראשונה זה יכול לקחת כמה דקות)...");
                    if (!Emulator.WaitBoot(900)) throw new Exception("הטלפון הווירטואלי לא עלה. פרטים בקובץ " + Paths.LogFile);
                    Emulator.Tune();
                    SetStep(2, "✅");

                    // 4. WhatsApp
                    step = 3; SetStep(3, "⏳");
                    WhatsAppAndroid.Update(!WhatsAppAndroid.IsInstalled, SetProgress);
                    SetStep(3, "✅");
                    SetProgress(100);

                    Log.Write("ההתקנה הושלמה!");
                    BeginInvoke((Action)(() =>
                    {
                        _btnPhone.Enabled = _btnRestore.Enabled = _btnDesktop.Enabled = _btnFinish.Enabled = true;
                        _help.Text = "ההתקנה הושלמה ✔  עכשיו לחץ \"ב. רישום (שיחה קולית)\".\n" +
                                     "יש לך גיבוי ממחשב קודם? לחץ קודם על \"שחזור גיבוי\".";
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

        void RestoreBackup()
        {
            string snap = Backup.LatestChatsSnapshot();
            if (snap == null)
            {
                Program.Msg("לא נמצא גיבוי בתיקייה:\n" + Backup.Dir +
                    "\n\nכדי לשחזר ממחשב אחר, העתק לשם את התיקייה \"KosherLink Play Backup\" מהמחשב הקודם (או בחר את התיקייה מ־Google Drive בתפריט הגיבוי).",
                    MessageBoxIcon.Information);
                return;
            }
            if (!Program.Ask("לשחזר את הגיבוי מ־" + Path.GetFileName(snap) + "?\n\nעושים את זה לפני הרישום. אחרי האימות בשיחה, וואטסאפ יציע \"שחזור\" – לחץ עליו.", MessageBoxIcon.Question, this)) return;
            _btnRestore.Enabled = false;
            Task.Run(() =>
            {
                try
                {
                    if (!Emulator.IsRunning) { Emulator.Start(); Emulator.WaitBoot(600); Emulator.Tune(); }
                    Backup.Restore();
                    SetHelp("הגיבוי הועתק לטלפון ✔  עכשיו לחץ \"ב. רישום\". אחרי האימות בשיחה וואטסאפ יציע \"שחזור\" – לחץ עליו.");
                }
                catch (Exception ex) { Log.Write("שחזור נכשל: " + ex.Message); SetHelp("השחזור נכשל: " + ex.Message); }
                finally { BeginInvoke((Action)(() => _btnRestore.Enabled = true)); }
            });
        }

        void StartRegistration()
        {
            _help.Text =
                "רישום וואטסאפ עם המספר של הטלפון הכשר:\n" +
                "1. בחלון הטלפון שנפתח לחץ \"Agree and continue\" (הסכם והמשך).\n" +
                "2. בחר מדינה Israel והקלד את המספר של הטלפון הכשר (בלי ה־0 הראשון).\n" +
                "3. כשמבקשים קוד – לא יגיע SMS. חכה שיופיע \"Call me\" / \"התקשרו אליי\" ולחץ עליו.\n" +
                "4. ענה בטלפון הכשר, שמע את הקוד בן 6 הספרות והקלד אותו בחלון.\n" +
                "5. השלם שם ותמונה (לא חובה). כשתסיים – עבור לשלב ג׳.\n\n" +
                "טיפ: להקלדה בעברית – העתק טקסט במחשב והדבק בחלון הטלפון עם Ctrl+V.";
            Task.Run(() =>
            {
                try
                {
                    bool wasRunning = Emulator.IsRunning;
                    if (!wasRunning) Emulator.Start();
                    if (!Emulator.WaitBoot(600)) throw new Exception("הטלפון הווירטואלי לא עלה");
                    if (!wasRunning) Emulator.Tune();
                    if (!PhoneViewer.IsInstalled) PhoneViewer.Install(null);
                    PhoneViewer.Show();
                    SetStep(4, "⏳");
                }
                catch (Exception ex) { Log.Write("פתיחת הטלפון נכשלה: " + ex.Message); }
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
                            "בסיום ההתקנה חזור לכאן ולחץ שוב על \"ג. חיבור וואטסאפ למחשב\".\n\n" +
                            "(אפשר גם לוותר על השלב הזה ולהשתמש רק בחלון הטלפון הווירטואלי.)";
                        WhatsAppDesktop.OpenStorePage();
                        return;
                    }
                    _help.Text =
                        "חיבור וואטסאפ ל־Windows לחשבון:\n" +
                        "1. בוואטסאפ ל־Windows שנפתח לחץ \"Log in with phone number\" (כניסה עם מספר טלפון), הקלד את המספר – יוצג קוד בן 8 תווים.\n" +
                        "2. בחלון הטלפון הווירטואלי תופיע הודעה לחיבור מכשיר – לחץ עליה והקלד את הקוד.\n" +
                        "   אם אין הודעה: ⋮ (שלוש נקודות) ← Linked devices ← Link a device ← \"Link with phone number instead\" ← הקלד את הקוד.\n" +
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
            SetStep(4, "✅");
            Program.Msg("הכל מוכן!\n\nהתוכנה תפעל ברקע (סמל ירוק ליד השעון) ותעלה אוטומטית עם Windows.\n" +
                        "לחיצה כפולה על הסמל פותחת את וואטסאפ.\nעדכונים לוואטסאפ מותקנים אוטומטית.", MessageBoxIcon.Information);
            var h = Finished; if (h != null) h();
            Close();
        }
    }
}
