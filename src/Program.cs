using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KosherLink
{
    static class Program
    {
        public const string Version = "1.0.10";
        public const string AppName = "KosherLink Google";

        [STAThread]
        static void Main(string[] args)
        {
            // filter-certificate window: "--filter", "--filter NetFree" (pre-selected) or "--filter file.crt"
            int fi = Array.IndexOf(args, "--filter");
            if (fi >= 0)
            {
                Application.EnableVisualStyles();
                FilterInstaller.Run(fi + 1 < args.Length ? args[fi + 1] : "");
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "KosherLinkPlay_SingleInstance", out created))
            {
                // after a self-update the previous version is still closing: wait for it
                if (!created && Array.IndexOf(args, "--after-update") >= 0)
                {
                    try { created = mutex.WaitOne(60000); } catch (AbandonedMutexException) { created = true; }
                }
                try
                {
                    string old = System.Reflection.Assembly.GetExecutingAssembly().Location + ".old";
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                }
                catch { }
                if (!created)
                {
                    Msg("התוכנה כבר פועלת – חפש את הסמל שלה ליד השעון.", MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("שגיאה: " + e.Exception);
                Log.Write("הפעלה, גרסה " + Version + ", תיקיית נתונים: " + Paths.Root);
                Application.Run(new TrayApp());
            }
        }

        public static Icon MakeIcon(Color color)
        {
            var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(color)) g.FillEllipse(b, 2, 2, 60, 60);
                using (var f = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("K", f, Brushes.White, new RectangleF(0, 2, 64, 62), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public static void Msg(string text, MessageBoxIcon icon)
        {
            Dialog(null, text, icon, "אישור", null);
        }

        /// <summary>Yes/No question with Hebrew buttons (Windows' own MessageBox uses the Windows language, often English).</summary>
        public static bool Ask(string text, MessageBoxIcon icon = MessageBoxIcon.Question, IWin32Window owner = null)
        {
            return Dialog(owner, text, icon, "כן", "לא");
        }

        static bool Dialog(IWin32Window owner, string text, MessageBoxIcon icon, string ok, string cancel)
        {
            using (var f = new Form
            {
                Text = AppName, RightToLeft = RightToLeft.Yes, RightToLeftLayout = true, Font = new Font("Segoe UI", 10f),
                AutoScaleMode = AutoScaleMode.Dpi, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = owner == null, StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, TopMost = owner == null
            })
            {
                var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 16, 16, 8) };
                Icon sys = icon == MessageBoxIcon.Error ? SystemIcons.Error : icon == MessageBoxIcon.Warning ? SystemIcons.Warning
                         : icon == MessageBoxIcon.Information ? SystemIcons.Information : SystemIcons.Question;
                grid.Controls.Add(new PictureBox { Image = sys.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 0, 0) }, 0, 0);
                grid.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(12, 4, 0, 12) }, 1, 0);
                var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4) };
                var bOk = new Button { Text = ok, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 0), Padding = new Padding(6, 2, 6, 2) };
                buttons.Controls.Add(bOk);
                f.AcceptButton = bOk;
                if (cancel != null)
                {
                    var bNo = new Button { Text = cancel, DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0), Padding = new Padding(6, 2, 6, 2) };
                    buttons.Controls.Add(bNo);
                    f.CancelButton = bNo;
                }
                else f.CancelButton = bOk;
                grid.Controls.Add(buttons, 1, 1);
                f.Controls.Add(grid);
                return (owner == null ? f.ShowDialog() : f.ShowDialog(owner)) == DialogResult.OK;
            }
        }
    }

    /// <summary>How often updates are checked (config UpdateEvery) and whether to ask first (UpdateAsk).</summary>
    static class UpdatePolicy
    {
        public static readonly string[] Modes = { "day", "week", "month", "manual" };
        public static readonly string[] Names = { "בדיקה כל יום (מומלץ)", "בדיקה פעם בשבוע", "בדיקה פעם בחודש", "רק ידנית" };

        public static string Mode
        {
            get { string m = Config.Get("UpdateEvery", "day"); return Array.IndexOf(Modes, m) >= 0 ? m : "day"; }
            set { Config.Set("UpdateEvery", value); }
        }

        public static bool Ask
        {
            get { return Config.GetBool("UpdateAsk"); }
            set { Config.SetBool("UpdateAsk", value); }
        }

        /// <summary>Update WhatsApp at once when it says its version expired (or is about to), whatever the schedule. On by default.</summary>
        public static bool OnExpiry
        {
            get { return Config.Get("UpdateOnExpiry", "1") == "1"; }
            set { Config.SetBool("UpdateOnExpiry", value); }
        }

        /// <summary>Interval between checks, or null for manual only. In daily mode WhatsApp is checked twice a day.</summary>
        static TimeSpan? Every(bool whatsapp)
        {
            switch (Mode)
            {
                case "manual": return null;
                case "week": return TimeSpan.FromDays(7);
                case "month": return TimeSpan.FromDays(30);
                default: return TimeSpan.FromHours(whatsapp ? 12 : 24);
            }
        }

        public static double DaysSince(string key)
        {
            DateTime last;
            if (!DateTime.TryParse(Config.Get(key, ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return double.MaxValue;
            return (DateTime.UtcNow - last).TotalDays;
        }

        public static bool Due(string key, bool whatsapp)
        {
            var every = Every(whatsapp);
            return every.HasValue && DaysSince(key) > every.Value.TotalDays;
        }
    }

    /// <summary>Lives next to the clock. Keeps the virtual phone running and up to date.</summary>
    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _status, _autoStart;
        readonly System.Windows.Forms.Timer _timer;
        readonly Control _ui = new Control();
        readonly Icon _iconOk = Program.MakeIcon(Color.FromArgb(26, 115, 232));
        readonly Icon _iconBusy = Program.MakeIcon(Color.FromArgb(220, 150, 30));
        readonly Icon _iconErr = Program.MakeIcon(Color.FromArgb(200, 60, 60));
        int _busy; // 1 while a background job runs
        bool _exiting;
        SetupForm _setup;

        public TrayApp()
        {
            _ui.CreateControl();
            var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
            _status = new ToolStripMenuItem("מצב: מתחיל...") { Enabled = false };
            var open = new ToolStripMenuItem("פתח וואטסאפ", null, (s, e) => OpenWhatsApp());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("הצג את הטלפון הווירטואלי", null, (s, e) => ShowPhone());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_status);
            menu.Items.Add("בדוק עדכונים עכשיו", null, (s, e) => Job("עדכון", () => DoUpdates(true)));
            var upd = new ToolStripMenuItem("הגדרות עדכון");
            upd.DropDown.RightToLeft = RightToLeft.Yes;
            var modeItems = new ToolStripMenuItem[UpdatePolicy.Modes.Length];
            for (int i = 0; i < modeItems.Length; i++)
            {
                string mode = UpdatePolicy.Modes[i];
                modeItems[i] = new ToolStripMenuItem(UpdatePolicy.Names[i], null, (s, e) =>
                {
                    UpdatePolicy.Mode = mode;
                    if (mode == "manual")
                        Program.Msg("העדכונים יותקנו רק כשתבחר \"בדוק עדכונים עכשיו\".\n\nשים לב: וואטסאפ מפסיק לעבוד בגרסאות ישנות מדי, ולכן כדאי לבדוק עדכונים לפחות פעם בחודש.", MessageBoxIcon.Information);
                });
                upd.DropDownItems.Add(modeItems[i]);
            }
            upd.DropDownItems.Add(new ToolStripSeparator());
            var ask = new ToolStripMenuItem("שאל אותי לפני התקנת עדכון", null, (s, e) => UpdatePolicy.Ask = !UpdatePolicy.Ask);
            upd.DropDownItems.Add(ask);
            var expiry = new ToolStripMenuItem("עדכן מיד כשוואטסאפ דורש עדכון (מומלץ)", null, (s, e) => UpdatePolicy.OnExpiry = !UpdatePolicy.OnExpiry);
            upd.DropDownItems.Add(expiry);
            upd.DropDown.Opening += (s, e) =>
            {
                for (int i = 0; i < modeItems.Length; i++) modeItems[i].Checked = UpdatePolicy.Modes[i] == UpdatePolicy.Mode;
                ask.Checked = UpdatePolicy.Ask;
                expiry.Checked = UpdatePolicy.OnExpiry;
            };
            menu.Items.Add(upd);
            var speed = new ToolStripMenuItem("מהירות הטלפון");
            speed.DropDown.RightToLeft = RightToLeft.Yes;
            foreach (var lvl in Speed.All)
            {
                var item = new ToolStripMenuItem(lvl.Name) { Tag = lvl };
                var chosen = lvl;
                item.Click += (s, e) => ChangeSpeed(chosen);
                speed.DropDownItems.Add(item);
            }
            speed.DropDown.Opening += (s, e) =>
            {
                foreach (ToolStripMenuItem i in speed.DropDownItems) i.Checked = ((Speed)i.Tag).Id == Speed.Current.Id;
            };
            menu.Items.Add(speed);
            var backup = new ToolStripMenuItem("גיבוי");
            backup.DropDown.RightToLeft = RightToLeft.Yes;
            backup.DropDownItems.Add("גבה עכשיו", null, (s, e) => Job("גיבוי", () => Notify(Backup.Run())));
            backup.DropDownItems.Add("גיבוי מלא של כל הטלפון...", null, (s, e) => FullBackup());
            backup.DropDownItems.Add(new ToolStripSeparator());
            backup.DropDownItems.Add("בחר תיקיית גיבוי (למשל Google Drive)...", null, (s, e) => ChooseBackupDir());
            backup.DropDownItems.Add("פתח את תיקיית הגיבויים", null, (s, e) =>
            {
                System.IO.Directory.CreateDirectory(Backup.Dir); Process.Start("explorer.exe", "\"" + Backup.Dir + "\"");
            });
            menu.Items.Add(backup);
            menu.Items.Add("תעודת סינון אינטרנט...", null, (s, e) => Sta(() => FilterInstaller.Run("")));
            menu.Items.Add("אשף התקנה ורישום", null, (s, e) => ShowSetup());
            _autoStart = new ToolStripMenuItem("הפעל אוטומטית עם Windows", null, (s, e) =>
            {
                AutoStart.Enabled = !AutoStart.Enabled; _autoStart.Checked = AutoStart.Enabled;
            });
            menu.Items.Add(_autoStart);
            menu.Items.Add("פתח את תיקיית הנתונים", null, (s, e) => Process.Start("explorer.exe", Paths.Root));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("יציאה (כיבוי הטלפון הווירטואלי)", null, (s, e) => ExitApp());
            menu.Opening += (s, e) => _autoStart.Checked = AutoStart.Enabled;

            _tray = new NotifyIcon { Icon = _iconBusy, Text = Program.AppName, ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (s, e) => OpenWhatsApp();

            SystemEvents.SessionEnding += (s, e) => { _exiting = true; try { Emulator.Stop(); } catch { } };

            _timer = new System.Windows.Forms.Timer { Interval = 2 * 60 * 1000 };
            _timer.Tick += (s, e) => Watchdog();
            _timer.Start();

            if (Config.GetBool("SetupDone")) Job("הפעלה", StartPhone);
            else
            {
                // While setup isn't finished the periodic checks don't run, so look for a fix of the program
                // itself once at startup, before the user starts installing (it takes about a second).
                if (UpdatePolicy.Due("AppLastCheck", false)) Task.Run(() => CheckAppUpdate());
                ShowSetup();
            }
        }

        void Ui(Action a)
        {
            if (_ui.IsHandleCreated) _ui.BeginInvoke(a); else a();
        }

        void SetStatus(string text, Icon icon)
        {
            Ui(() =>
            {
                _status.Text = "מצב: " + text;
                _tray.Icon = icon;
                string tip = Program.AppName + " – " + text;
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            });
        }

        /// <summary>Runs one background job at a time.</summary>
        void Job(string name, Action work)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log.Write(name + " נכשל: " + ex.Message);
                    SetStatus("שגיאה – " + ex.Message, _iconErr);
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        void StartPhone()
        {
            if (!AndroidSdk.ComponentsInstalled() || !AndroidSdk.AvdExists())
            {
                SetStatus("צריך להשלים התקנה", _iconErr);
                return;
            }
            bool wasRunning = Emulator.IsRunning;
            SetStatus("מפעיל את הטלפון הווירטואלי...", _iconBusy);
            Emulator.Start();
            if (!Emulator.WaitBoot(600)) throw new Exception("הטלפון הווירטואלי לא עלה");
            if (!wasRunning) Emulator.Tune(); else Emulator.LowerPriority();
            SetStatus("פועל", _iconOk);
            Sta(FilterInstaller.OfferOnce); // asks once ever, only if Windows has a filter certificate
            DoUpdates(false);
        }

        void DoUpdates(bool force)
        {
            if (_exiting) return;
            // no Play Store in this phone: WhatsApp comes from whatsapp.com, like in the regular edition
            if (Emulator.IsRunning && Emulator.IsBooted && (force || UpdatePolicy.Due("WaLastCheck", true)))
            {
                SetStatus("בודק עדכון לוואטסאפ...", _iconBusy);
                bool updated = WhatsAppAndroid.Update(false, null,
                    UpdatePolicy.Ask ? () => Confirm("יש גרסה חדשה של וואטסאפ. לעדכן עכשיו?\n(העדכון לוקח כדקה, ובזמן הזה וואטסאפ במחשב עלול להתנתק לרגע)") : (Func<bool>)null);
                if (updated) Notify("וואטסאפ עודכן לגרסה " + WhatsAppAndroid.InstalledVersion);
                SetStatus("פועל", _iconOk);
            }
            if (force || UpdatePolicy.Due("AppLastCheck", false)) CheckAppUpdate();
            if (force) Notify("הבדיקה הסתיימה.");
            else if (UpdatePolicy.Mode == "manual" && !_staleWarned && UpdatePolicy.DaysSince("WaLastCheck") > 30)
            {
                _staleWarned = true;
                Notify("לא נבדקו עדכונים כבר יותר מחודש. כדי שוואטסאפ ימשיך לעבוד, בחר \"בדוק עדכונים עכשיו\".");
            }
        }

        bool _staleWarned;

        DateTime _lastExpiryLook = DateTime.MinValue, _lastExpiryFix = DateTime.MinValue;

        /// <summary>Safety net: WhatsApp blocks itself when its version expires, and says so on its screen. Look at the screen
        /// (WhatsApp usually runs in the background, so open it every few hours) and update at once, even in
        /// weekly, monthly or manual mode. Called from the 2-minute watchdog.</summary>
        void CheckExpiry()
        {
            if (_exiting || !UpdatePolicy.OnExpiry || !Emulator.IsBooted || !WhatsAppAndroid.IsInstalled) return;
            bool viewer = PhoneViewer.IsOpen;
            bool expired;
            if (!viewer && DateTime.UtcNow - _lastExpiryLook > TimeSpan.FromHours(6))
            {
                _lastExpiryLook = DateTime.UtcNow;
                Emulator.ScreenOn();
                WhatsAppAndroid.Launch();
                Thread.Sleep(10000);
                expired = WhatsAppAndroid.ShowsExpiry();
                if (!PhoneViewer.IsOpen) Emulator.ScreenOff();
            }
            else expired = viewer && WhatsAppAndroid.ShowsExpiry();
            if (!expired || DateTime.UtcNow - _lastExpiryFix < TimeSpan.FromHours(1)) return;

            _lastExpiryFix = DateTime.UtcNow;
            Log.Write("וואטסאפ מבקש עדכון גרסה – מעדכן עכשיו");
            SetStatus("וואטסאפ דורש עדכון – מעדכן...", _iconBusy);
            if (WhatsAppAndroid.Update(false, null))
            {
                Notify("וואטסאפ דרש עדכון, ועודכן לגרסה " + WhatsAppAndroid.InstalledVersion);
                if (PhoneViewer.IsOpen) WhatsAppAndroid.Launch();
            }
            else Notify("וואטסאפ מבקש עדכון, אבל באתר של וואטסאפ עוד אין גרסה חדשה יותר. ננסה שוב בעוד שעה.");
            SetStatus("פועל", _iconOk);
        }

        /// <summary>Runs work that shows dialogs (file picker) on its own STA thread, so the tray stays responsive.</summary>
        static void Sta(Action a)
        {
            var t = new Thread(() => a()) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        /// <summary>Yes/No question on the UI thread (safe to call from a background job).</summary>
        bool Confirm(string text)
        {
            bool yes = false;
            _ui.Invoke((Action)(() => yes = Program.Ask(text)));
            return yes;
        }

        /// <summary>Updates KosherLink itself from GitHub; on success the new version is started and this one exits.</summary>
        void CheckAppUpdate()
        {
            try
            {
                if (AppUpdater.CheckAndApply(UpdatePolicy.Ask
                    ? v => Confirm("יש גרסה חדשה של KosherLink (" + v + "). לעדכן עכשיו?\n(התוכנה תיסגר ותיפתח מחדש תוך כדקה)")
                    : (Func<string, bool>)null))
                {
                    _exiting = true;
                    if (Emulator.IsRunning) Emulator.Stop();
                    Ui(() => { _tray.Visible = false; Application.Exit(); });
                }
            }
            catch (Exception ex) { Log.Write("בדיקת עדכון לתוכנה נכשלה: " + ex.Message); }
        }

        void Watchdog()
        {
            if (_exiting || !Config.GetBool("SetupDone") || _busy != 0) return;
            Job("בדיקה תקופתית", () =>
            {
                if (!Emulator.IsRunning) { Log.Write("הטלפון הווירטואלי לא פעל – מפעיל מחדש"); StartPhone(); }
                else
                {
                    Emulator.LowerPriority();
                    DoUpdates(false);
                    CheckExpiry();
                    if (Backup.Due && Emulator.IsBooted)
                    {
                        try { Backup.Run(); }
                        catch (Exception ex) { Log.Write("גיבוי נכשל: " + ex.Message); Notify("הגיבוי נכשל: " + ex.Message); }
                    }
                }
            });
        }

        void ChooseBackupDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "בחר תיקייה לגיבויים. כדי לגבות לענן, בחר תיקייה בתוך Google Drive או OneDrive.";
                dlg.SelectedPath = Backup.Dir;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                Backup.Dir = System.IO.Path.Combine(dlg.SelectedPath, "KosherLink Google Backup");
                Config.Set("BackupLast", ""); // back up to the new place soon
                Notify("הגיבויים יישמרו ב: " + Backup.Dir);
            }
        }

        void ChangeSpeed(Speed lvl)
        {
            if (lvl.Id == Speed.Current.Id) return;
            bool restart = Emulator.IsRunning;
            if (restart && !Program.Ask("להעביר את הטלפון למהירות \"" + lvl.Name + "\"?\n\n" +
                    "הטלפון הווירטואלי יופעל מחדש (כדקה). וואטסאפ במחשב יתנתק לרגע ויחזור לבד.")) return;
            Config.Set("Speed", lvl.Id);
            Log.Write("מהירות הטלפון שונתה ל־" + lvl.Name);
            if (!restart) { Notify("מהירות הטלפון: " + lvl.Name + ". תחול בהפעלה הבאה."); return; }
            Job("שינוי מהירות", () =>
            {
                SetStatus("מפעיל מחדש במהירות החדשה...", _iconBusy);
                Emulator.Stop();
                StartPhone();
                Notify("הטלפון פועל במהירות: " + lvl.Name);
            });
        }

        void FullBackup()
        {
            if (!Program.Ask(
                    "גיבוי מלא מעתיק את כל הטלפון הווירטואלי (כ־8GB) אל:\n" + Backup.Dir +
                    "\n\nבזמן הגיבוי (כמה דקות) הטלפון כבוי, אבל וואטסאפ במחשב ממשיך לעבוד.\nלהמשיך?")) return;
            Job("גיבוי מלא", () =>
            {
                SetStatus("מגבה את כל הטלפון...", _iconBusy);
                Emulator.Stop();
                try { Notify(Backup.Full()); }
                finally { StartPhone(); }
            });
        }

        void Notify(string text)
        {
            Ui(() => _tray.ShowBalloonTip(4000, Program.AppName, text, ToolTipIcon.Info));
        }

        void OpenWhatsApp()
        {
            if (!Config.GetBool("SetupDone")) { ShowSetup(); return; }
            Task.Run(() =>
            {
                if (!WhatsAppDesktop.Launch())
                    Ui(() => ShowPhone()); // no Windows app installed: show the phone screen instead
            });
        }

        void ShowPhone()
        {
            Task.Run(() =>
            {
                try
                {
                    if (!Emulator.IsRunning || !Emulator.IsBooted) { Notify("הטלפון הווירטואלי עדיין עולה, נסה שוב בעוד רגע."); return; }
                    if (!PhoneViewer.IsInstalled) PhoneViewer.Install(null);
                    PhoneViewer.Show();
                }
                catch (Exception ex) { Log.Write("הצגת המסך נכשלה: " + ex.Message); Notify("הצגת המסך נכשלה: " + ex.Message); }
            });
        }

        void ShowSetup()
        {
            if (_setup != null && !_setup.IsDisposed) { _setup.Activate(); return; }
            _setup = new SetupForm();
            _setup.Finished += () => Job("הפעלה", StartPhone);
            _setup.Show();
        }

        void ExitApp()
        {
            _exiting = true;
            _timer.Stop();
            SetStatus("נסגר...", _iconBusy);
            Task.Run(() =>
            {
                try { Emulator.Stop(); } catch { }
                Ui(() => { _tray.Visible = false; Application.Exit(); });
            });
        }
    }
}
