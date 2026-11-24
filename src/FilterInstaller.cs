using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KosherLink
{
    /// <summary>
    /// Installing the internet-filter certificate is the user's choice: this window lists the filters, the user
    /// picks one (or a certificate file) once, and it is installed. Opened from the setup wizard, the tray menu,
    /// or the stand-alone download "--filter" (this edition has no separate download; the window is in the wizard and the menu).
    /// Works whether or not the tray program runs: certificates go to the shared data folder and, if the virtual
    /// phone is up, into it right away.
    /// </summary>
    static class FilterInstaller
    {
        /// <param name="what">A filter to pre-select (NetFree, נטפרי...), a certificate file to install, or empty.</param>
        public static void Run(string what)
        {
            if (File.Exists(what))
            {
                try { Program.Msg(Install(null, what, null), MessageBoxIcon.Information); }
                catch (Exception ex) { Program.Msg("התקנת התעודה נכשלה:\n" + ex.Message, MessageBoxIcon.Error); }
                return;
            }
            using (var f = new FilterForm(FilterCert.Find(what ?? ""))) f.ShowDialog();
        }

        /// <summary>
        /// Installs the chosen filter's certificate (from Windows, else its official download, else a file the user
        /// picks) in place of any previous one. Returns the message for the user, or null if the user cancelled.
        /// </summary>
        public static string Install(FilterCert.Filter filter, string file, Func<string, string> askForFile)
        {
            // nothing is installed later by itself, so the phone has to be up now
            if (!AndroidSdk.AvdExists()) throw new Exception("KosherLink עוד לא הותקן במחשב הזה. קודם מריצים את ההתקנה האוטומטית, ואחריה את תעודת הסינון");
            if (!Emulator.IsRunning || !Emulator.IsBooted) throw new Exception("הטלפון הווירטואלי לא פועל כרגע. הפעל את KosherLink, חכה שהסמל ליד השעון יהיה ירוק, ונסה שוב");
            List<X509Certificate2> certs;
            if (file != null) certs = FilterCert.Parse(File.ReadAllBytes(file));
            else
            {
                // 1. the certificate the filter installed in Windows  2. the filter's official download
                // 3. the copy shipped in the program  4. a file the user picks
                certs = FilterCert.InWindows(filter);
                if (certs.Count == 0 && filter.Url != null)
                {
                    Log.Write("תעודת " + filter.Name + " לא נמצאה ב־Windows – מוריד מהאתר הרשמי " + filter.Url);
                    try { certs = FilterCert.Download(filter); }
                    catch (Exception ex) { Log.Write("ההורדה נכשלה (" + ex.Message + ") – משתמש בעותק שבתוכנה"); }
                }
                if (certs.Count == 0)
                {
                    certs = FilterCert.Builtin(filter);
                    if (certs.Count > 0) Log.Write("משתמש בתעודת " + filter.Name + " שמובנית בתוכנה");
                }
                if (certs.Count == 0)
                {
                    string picked = askForFile == null ? null : askForFile(
                        "לא נמצאה במחשב תעודת אבטחה של " + filter.Name + ".\n\n" +
                        "אם הורדת קובץ תעודה מאתר הסינון (‎.crt / .cer / .pem‏), אפשר לבחור אותו עכשיו.\n\nלבחור קובץ?");
                    if (picked == null) return null;
                    certs = FilterCert.Parse(File.ReadAllBytes(picked));
                }
            }
            if (certs.Count == 0) throw new Exception("הקובץ שנבחר אינו תעודת אבטחה");

            FilterCert.Clear();          // one filter at a time: the new choice replaces the old one
            if (FilterCert.Save(certs) == 0) throw new Exception("התעודה לא מתאימה לטלפון הווירטואלי (פג תוקף או סוג לא נתמך)");
            FilterCert.Choice = filter != null ? filter.Name : certs[0].GetNameInfo(X509NameType.SimpleName, false);
            string names = FilterCert.Describe();

            if (FilterCert.Apply() && WhatsAppAndroid.IsInstalled)
            {
                Proc.Shell("am force-stop " + WhatsAppAndroid.Package); // it read the old list when it started
                WhatsAppAndroid.Launch();
                if (!PhoneViewer.IsOpen) Emulator.ScreenOff();
            }
            return "תעודת האבטחה הותקנה בטלפון הווירטואלי ✅ (" + names + ")";
        }

        /// <summary>
        /// Asks once, ever: if Windows has a filter's certificate and none was installed in the phone yet, offer to
        /// open the window. The answer is remembered and the question never comes back (the window stays in the menu).
        /// Call when the phone is up, from a thread that may show dialogs (STA).
        /// </summary>
        public static void OfferOnce()
        {
            try
            {
                if (Config.GetBool("FilterOffered") || FilterCert.Saved.Length > 0) return;
                var found = FilterCert.InWindows();
                if (found.Count == 0) return;
                var filters = FilterCert.Known.Where(f => found.Any(f.Matches)).ToList();
                if (filters.Count == 0) return;
                Config.SetBool("FilterOffered", true);
                Log.Write("נמצאה במחשב תעודת סינון (" + string.Join(" / ", filters.Select(f => f.Name)) + ") – שואל את המשתמש פעם אחת");

                bool yes = Program.Ask(
                    "נמצאה במחשב תעודת סינון אינטרנט של " + string.Join(" / ", filters.Select(f => f.Name)) + ".\n\n" +
                    "אם המחשב גולש דרך הסינון, צריך להתקין אותה גם בטלפון הווירטואלי, אחרת וואטסאפ עלול לא להתחבר.\n\n" +
                    "לפתוח את חלון ההתקנה?\n\n(השאלה לא תופיע שוב. אפשר תמיד להגיע לחלון מהתפריט ← \"תעודת סינון אינטרנט\")");
                if (!yes) return;
                using (var form = new FilterForm(filters[0])) form.ShowDialog();
            }
            catch (Exception ex) { Log.Write("הצעת תעודת סינון: " + ex.Message); }
        }

        public static string PickFile()
        {
            using (var d = new OpenFileDialog
            {
                Title = "בחר את קובץ תעודת האבטחה של הסינון",
                Filter = "תעודת אבטחה (*.crt;*.cer;*.pem;*.der)|*.crt;*.cer;*.pem;*.der|כל הקבצים|*.*"
            })
                return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
        }
    }

    /// <summary>The filter chooser window.</summary>
    class FilterForm : Form
    {
        readonly List<RadioButton> _choices = new List<RadioButton>();
        readonly RadioButton _other;
        readonly Label _current, _status;
        readonly Button _install, _remove;

        public FilterForm(FilterCert.Filter preselect)
        {
            Text = Program.AppName + " – תעודת סינון אינטרנט";
            Icon = Program.MakeIcon(Color.FromArgb(26, 115, 232));
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                Padding = new Padding(18), Dock = DockStyle.Fill
            };
            Controls.Add(root);

            root.Controls.Add(new Label { Text = "תעודת סינון אינטרנט", Font = new Font("Segoe UI", 15f, FontStyle.Bold), AutoSize = true });
            root.Controls.Add(new Label
            {
                Text = "צריך רק אם המחשב גולש דרך סינון (נטפרי, נטספארק, אתרוג וכו'). בלי התעודה, וואטסאפ בטלפון הווירטואלי עלול לא להתחבר.\n" +
                       "בוחרים את הסינון שלך ולוחצים \"התקן\". התעודה מותקנת בטלפון הווירטואלי ונשארת בו.",
                AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 10)
            });

            var inWindows = FilterCert.InWindows();
            foreach (var f in FilterCert.Known)
            {
                bool found = inWindows.Any(f.Matches);
                var rb = new RadioButton
                {
                    Text = f.Name + (found ? "   ✓ נמצאה במחשב" : f.Url == null && f.Embedded == null ? "   (צריך קובץ תעודה מאתר הסינון)" : ""),
                    Tag = f, AutoSize = true, Margin = new Padding(8, 2, 0, 2),
                    ForeColor = found ? Color.FromArgb(25, 120, 60) : SystemColors.ControlText,
                    Checked = preselect == f || (preselect == null && found && !_choices.Any(c => c.Checked))
                };
                _choices.Add(rb);
                root.Controls.Add(rb);
            }
            _other = new RadioButton { Text = "סינון אחר – אבחר קובץ תעודה מהמחשב...", AutoSize = true, Margin = new Padding(8, 2, 0, 2) };
            root.Controls.Add(_other);

            _current = new Label { AutoSize = true, Margin = new Padding(0, 12, 0, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            root.Controls.Add(_current);

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            _install = new Button { Text = "התקן", AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
            _remove = new Button { Text = "הסר את התעודה", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
            var close = new Button { Text = "סגור", AutoSize = true, Padding = new Padding(8, 4, 8, 4), DialogResult = DialogResult.Cancel };
            buttons.Controls.AddRange(new Control[] { _install, _remove, close });
            root.Controls.Add(buttons);
            AcceptButton = _install;
            CancelButton = close;

            _status = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 10, 0, 0) };
            root.Controls.Add(_status);

            _install.Click += (s, e) => DoInstall();
            _remove.Click += (s, e) => DoRemove();
            RefreshCurrent();
        }

        void RefreshCurrent()
        {
            _current.Text = "בודק מה מותקן בטלפון...";
            Task.Run(() =>
            {
                string d = FilterCert.Describe(), text;
                bool? on = null;
                try { on = FilterCert.OnPhone(); } catch { }
                if (d.Length == 0) text = "מותקנת עכשיו: אין תעודת סינון";
                else if (on == true) text = "מותקנת עכשיו: " + d + " ✓";
                else if (on == false) text = "נבחרה: " + d + ", אבל היא לא נמצאת כרגע בטלפון (הוא הופעל מחדש). לחץ \"התקן\" כדי להתקין שוב.";
                else text = "נבחרה: " + d + " (הטלפון הווירטואלי כבוי כרגע)";
                try { BeginInvoke((Action)(() => { _current.Text = text; _remove.Enabled = d.Length > 0; })); } catch { }
            });
        }

        void Busy(bool busy, string text)
        {
            _install.Enabled = !busy;
            _remove.Enabled = !busy && FilterCert.Describe().Length > 0;
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = text;
            UseWaitCursor = busy;
        }

        void DoInstall()
        {
            var chosen = _choices.FirstOrDefault(r => r.Checked);
            string file = null;
            if (_other.Checked)
            {
                file = FilterInstaller.PickFile();
                if (file == null) return;
            }
            else if (chosen == null) { _status.Text = "בחר את הסינון שלך מהרשימה."; return; }
            var filter = chosen != null && !_other.Checked ? (FilterCert.Filter)chosen.Tag : null;

            Busy(true, "מתקין את התעודה...");
            Func<string, string> ask = question => (string)Invoke((Func<string>)(() =>
                Program.Ask(question, MessageBoxIcon.Question, this) ? FilterInstaller.PickFile() : null));
            Task.Run(() =>
            {
                string msg; bool ok = true;
                try { msg = FilterInstaller.Install(filter, file, ask) ?? ""; }
                catch (Exception ex) { ok = false; msg = "ההתקנה נכשלה: " + ex.Message; Log.Write("תעודת סינון: " + ex.Message); }
                BeginInvoke((Action)(() =>
                {
                    Busy(false, msg);
                    _status.ForeColor = ok ? Color.FromArgb(25, 120, 60) : Color.FromArgb(180, 40, 40);
                    RefreshCurrent();
                }));
            });
        }

        void DoRemove()
        {
            Busy(true, "מסיר את התעודה...");
            Task.Run(() =>
            {
                string msg;
                try { FilterCert.Clear(); msg = "תעודת הסינון הוסרה מהטלפון הווירטואלי."; }
                catch (Exception ex) { msg = "ההסרה נכשלה: " + ex.Message; }
                BeginInvoke((Action)(() => { Busy(false, msg); RefreshCurrent(); }));
            });
        }
    }
}
