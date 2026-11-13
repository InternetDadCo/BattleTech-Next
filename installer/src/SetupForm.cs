// The installer window: choose the BATTLETECH folder and a destination, then install.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReleaseGate
{
    // Fills the window's width and keeps the art's proportions. It adds no width of its own, so the form below sets the size.
    sealed class Banner : Control
    {
        readonly Image _art;

        public Banner()
        {
            _art = Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("banner.jpg"));
            Name = "Banner";
            Dock = DockStyle.Top;
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "BattleTech: Unofficial Community Update (UCU)";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public override Size GetPreferredSize(Size proposed) { return new Size(0, Height); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Height = Width * _art.Height / _art.Width;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_art, ClientRectangle);
        }
    }

    sealed class SetupForm : Form
    {
        public const string Title = "BattleTech: Unofficial Community Update (UCU) Installer";
        static readonly Color Good = Color.FromArgb(0, 110, 50), Bad = Color.FromArgb(170, 30, 30);

        readonly TextBox _retail = new TextBox { Name = "RetailPath", AccessibleName = "BATTLETECH folder", Dock = DockStyle.Fill };
        readonly TextBox _dest = new TextBox { Name = "DestPath", AccessibleName = "Install to", Dock = DockStyle.Fill };
        readonly Label _retailNote = Note("RetailNote");
        readonly TextBox _code = new TextBox { Name = "AccessCode", AccessibleName = "Access code", Dock = DockStyle.Fill, CharacterCasing = CharacterCasing.Upper };
        readonly Label _codeCaption = new Label { Text = "Access code (only if you were given one; without it, Steam confirms your copy)", AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
        readonly Label _codeNote = Note("CodeNote");
        readonly Label _destNote = Note("DestNote");
        readonly ProgressBar _bar = new ProgressBar { Name = "Progress", Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Label _status = Note("Status");
        readonly Button _go = new Button { Name = "Install", Text = "Install", AutoSize = true, Enabled = false, MinimumSize = new Size(110, 0) };
        readonly Button _close = new Button { Name = "Close", Text = "Close", AutoSize = true, MinimumSize = new Size(110, 0) };
        readonly Button _updates = new Button { Name = "CheckUpdates", Text = "Check for updates", AutoSize = true };
        readonly Button _verify = new Button { Name = "VerifyInstall", Text = "Verify install", AutoSize = true, Visible = false };
        readonly Button _browseRetail = new Button { Name = "BrowseRetail", Text = "Browse...", AutoSize = true };
        readonly Button _browseDest = new Button { Name = "BrowseDest", Text = "Browse...", AutoSize = true };
        readonly System.Windows.Forms.Timer _settle = new System.Windows.Forms.Timer { Interval = 350 };
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 150 };
        readonly System.Windows.Forms.Timer _planSettle = new System.Windows.Forms.Timer { Interval = 400 };

        RetailCheck _check;
        InstallProgress _progress;
        CancellationTokenSource _stop;
        int _checkId, _planId;
        bool _destEdited, _settingDest, _installing, _installed, _opening;
        string _source = Remote.DefaultSource;
        volatile string _phase = "Installing";

        static Label Note(string name)
        {
            return new Label { Name = name, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 8), MaximumSize = new Size(560, 0) };
        }

        /// <summary>
        /// BATTLETECH-UCU beside the retail folder. An install made before the rename, in BATTLETECH-Unity6, is offered
        /// instead while there's no BATTLETECH-UCU, so updating it doesn't download the whole game into a new folder.
        /// </summary>
        public static string DefaultDest(string retail)
        {
            string parent = Path.GetDirectoryName(retail.TrimEnd('\\', '/')) ?? retail;
            string dest = Path.Combine(parent, "BATTLETECH-UCU"), old = Path.Combine(parent, "BATTLETECH-Unity6");
            try
            {
                if (!Directory.Exists(dest) && File.Exists(Path.Combine(old, Installer.RecordName))) return old;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { }
            return dest;
        }

        /// <summary>remote: a release source to install from (Remote.Open), or null for the package beside the installer.</summary>
        public SetupForm(string detected, string remote)
        {
            SuspendLayout();
            Text = Title;
            Name = "SetupForm";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Width = 600, Padding = new Padding(14) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(590, 0),
                Margin = new Padding(3, 0, 3, 12),
                Text = "This installs BattleTech: Unofficial Community Update (UCU). It needs your own copy of BATTLETECH, which stays unchanged.",
            };
            t.Controls.Add(intro);
            t.SetColumnSpan(intro, 2);
            AddRow(t, "BATTLETECH folder", _retail, _browseRetail, _retailNote);
            // A v3 package is unlocked by the unlock server: through Steam, or for an access code (docs/keyserver-design.md).
            foreach (Control c in new Control[] { _codeCaption, _code, _codeNote })
            {
                t.Controls.Add(c);
                t.SetColumnSpan(c, 2);
            }
            AddRow(t, "Install to", _dest, _browseDest, _destNote);
            t.Controls.Add(_bar);
            t.SetColumnSpan(_bar, 2);
            t.Controls.Add(_status);
            t.SetColumnSpan(_status, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_go);
            buttons.Controls.Add(_updates);
            buttons.Controls.Add(_verify);
            t.Controls.Add(buttons);
            t.SetColumnSpan(buttons, 2);
            Controls.Add(t);
            Controls.Add(new Banner());
            AcceptButton = _go;

            _retail.TextChanged += (s, e) => QueueCheck();
            _code.TextChanged += (s, e) => QueueCheck();
            // Only typing (box focused) or Browse counts as the player's choice: Wine's WinForms raises TextChanged on its own.
            _dest.TextChanged += (s, e) => { if (!_settingDest && _dest.Focused) _destEdited = true; ShowDest(); ShowVerify(); };
            _verify.Click += (s, e) => StartVerify();
            _settle.Tick += (s, e) => { _settle.Stop(); RunCheck(); };
            _tick.Tick += (s, e) => ShowProgress();
            _planSettle.Tick += (s, e) => { _planSettle.Stop(); PlanDest(); };
            _updates.Click += (s, e) => OpenRemote(_source);
            _browseRetail.Click += (s, e) => Browse(_retail, "Choose the folder that holds BattleTech.exe");
            _browseDest.Click += (s, e) => Browse(_dest, "Choose where to install");
            _go.Click += (s, e) => GoPressed();
            _close.Click += (s, e) => Close();
            FormClosing += OnClosing;

            _code.Text = Unlock.Code ?? Unlock.LoadSaved() ?? "";
            if (remote == null) ShowLock();
            _retail.Text = detected ?? "";
            if (detected == null) Show(_retailNote, "BATTLETECH wasn't found automatically. Enter its folder, or use Browse.", Bad);
            if (remote != null)
            {
                _updates.Visible = false;
                _opening = true;
                Shown += (s, e) => OpenRemote(remote);
            }
            ResumeLayout(true);
        }

        /// <summary>Shows the access code only for a package that the unlock server unlocks.</summary>
        void ShowLock()
        {
            bool locked = Installer.PackageLock != null;
            _codeCaption.Visible = _code.Visible = _codeNote.Visible = locked;
        }

        static void AddRow(TableLayoutPanel t, string caption, TextBox box, Button browse, Label note)
        {
            var label = new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
            t.Controls.Add(label);
            t.SetColumnSpan(label, 2);
            t.Controls.Add(box);
            t.Controls.Add(browse);
            t.Controls.Add(note);
            t.SetColumnSpan(note, 2);
        }

        static void Show(Label l, string text, Color colour)
        {
            l.Text = text;
            l.ForeColor = colour;
        }

        void Browse(TextBox box, string prompt)
        {
            using (var d = new FolderBrowserDialog { Description = prompt, ShowNewFolderButton = box == _dest })
            {
                if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (box == _dest) _destEdited = true;
                box.Text = d.SelectedPath;
            }
        }

        void QueueCheck()
        {
            _check = null;
            _go.Enabled = false;
            _settle.Stop();
            _settle.Start();
        }

        void RunCheck()
        {
            if (_opening)
            {
                Show(_retailNote, "Waiting for the release...", SystemColors.ControlText);
                return;
            }
            int id = ++_checkId;
            string typed = _retail.Text;
            Unlock.Code = _code.Text;
            Show(_retailNote, "Checking...", SystemColors.ControlText);
            if (_code.Visible && Unlock.Normalize(_code.Text).Length == 0) Show(_codeNote, "Confirming your copy with Steam...", SystemColors.ControlText);
            Task.Run(() => Installer.Check(typed)).ContinueWith(t =>
            {
                if (id != _checkId || IsDisposed) return;
                _check = t.Result;
                if (_check.CodeProblem)
                {
                    Show(_retailNote, "BATTLETECH found.", Good);
                    Show(_codeNote, _check.Problem, Bad);
                }
                else if (_check.Problem != null)
                {
                    Show(_retailNote, _check.Problem, Bad);
                    Show(_codeNote, "", Good);
                }
                else
                {
                    Show(_retailNote, "BATTLETECH found.", Good);
                    if (_code.Visible && Unlock.LastRoute == "steam") Show(_codeNote, "Steam confirmed that you own BATTLETECH.", Good);
                    else if (_code.Visible)
                    {
                        Show(_codeNote, "Access code accepted.", Good);
                        Unlock.Save(_code.Text);
                    }
                    else Show(_codeNote, "", Good);
                    if (!_destEdited)
                    {
                        _settingDest = true;
                        _dest.Text = DefaultDest(_check.Folder);
                        _settingDest = false;
                    }
                    // The manifest's hash is what the release notes publish, so a player can tie this package to them.
                    string release = Installer.Remote != null ? "Release " + Installer.Remote.Version + ". " : "";
                    if (_check.ManifestSha256 != null)
                        Show(_status, release + "Public manifest SHA-256 " + _check.ManifestSha256 + " (compare it with the release notes).", SystemColors.ControlText);
                    else
                        Show(_status, release + "This package has no public manifest.", SystemColors.ControlText);
                }
                ShowDest();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowDest()
        {
            if (_check == null || _check.Problem != null)
            {
                Show(_destNote, "", Good);
                _go.Enabled = false;
                return;
            }
            long space;
            string problem = Installer.CheckDest(_dest.Text, _check, out space);
            _planSettle.Stop();
            ++_planId;
            if (problem != null) Show(_destNote, problem, Bad);
            else if (Installer.Remote != null)
            {
                Show(_destNote, "Checking what to download...", SystemColors.ControlText);
                _planSettle.Start();
            }
            else if (space < _check.Need)
                Show(_destNote, string.Format("Part of the game is already here; {0:F1} GiB more will be used.", space / 1073741824.0), Good);
            else Show(_destNote, string.Format("{0:F1} GiB will be used.", _check.Need / 1073741824.0), Good);
            _go.Enabled = problem == null && !_installing;
        }

        /// <summary>Online installs: says what this destination still needs, and names the button Update when it holds an install.</summary>
        void PlanDest()
        {
            if (_check == null || _check.Problem != null || _installing) return;
            int id = ++_planId;
            RetailCheck check = _check;
            string dest;
            try { dest = DestFull(); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return; }
            Task.Run(() => Installer.PlanInstall(check, dest)).ContinueWith(t =>
            {
                if (id != _planId || IsDisposed || _installing) return;
                if (t.IsFaulted)
                {
                    Show(_destNote, "That folder can't be read: " + t.Exception.GetBaseException().Message, Bad);
                    return;
                }
                Installer.Plan p = t.Result;
                string version = Installer.Remote.Version;
                if (p.Files == 0 && p.Remove == 0) Show(_destNote, "Up to date with release " + version + ".", Good);
                else Show(_destNote, string.Format("Release {0}: {1} files to write, {2} to download.", version, p.Files, Amount(p.FetchBytes)), Good);
                if (!_installed) _go.Text = p.Same > 0 ? "Update" : "Install";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Reads a release online. Once found, its vault replaces the local package's and both folders are checked again.</summary>
        void OpenRemote(string source)
        {
            _source = source;
            _opening = true;
            _updates.Enabled = false;
            _check = null;
            _go.Enabled = false;
            Show(_status, "Looking for the newest release...", SystemColors.ControlText);
            Task.Run(() => Remote.Open(source, CancellationToken.None)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                _opening = false;
                if (t.IsFaulted)
                {
                    Show(_status, "Couldn't get the release: " + t.Exception.GetBaseException().Message, Bad);
                    // With no package beside the installer the release is the only way on, so offer it again.
                    _updates.Text = Installer.HasPackage ? "Check for updates" : "Try again";
                    _updates.Visible = true;
                    _updates.Enabled = true;
                    QueueCheck();
                    return;
                }
                RemotePackage p = t.Result;
                if (p.NeedsNewerInstaller)
                {
                    OfferNewerInstaller(p, source);
                    return;
                }
                Installer.UseRemote(p);
                ShowLock();
                _updates.Visible = false;
                Show(_status, "Release " + p.Version + " found online.", Good);
                QueueCheck();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OfferNewerInstaller(RemotePackage p, string source)
        {
            if (Setup.Handed)
            {
                Show(_status, "Release " + p.Version + " needs a newer installer than the one it ships.", Bad);
                return;
            }
            if (MessageBox.Show(this, "Release " + p.Version + " needs a newer installer. Download it and continue there?", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Show(_status, "Release " + p.Version + " needs a newer installer.", Bad);
                _updates.Enabled = true;
                QueueCheck();
                return;
            }
            Show(_status, "Downloading the newer installer...", SystemColors.ControlText);
            Task.Run(() => Remote.StartNewerInstaller(p, Setup.ArgumentsFor(source), false, CancellationToken.None)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted)
                {
                    Show(_status, "Couldn't start the newer installer: " + t.Exception.GetBaseException().Message, Bad);
                    _updates.Enabled = true;
                    QueueCheck();
                    return;
                }
                t.Result.Dispose();
                Close();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static string Amount(long bytes)
        {
            return bytes < (1L << 30) ? string.Format("{0:F1} MiB", bytes / 1048576.0) : string.Format("{0:F2} GiB", bytes / 1073741824.0);
        }

        void GoPressed()
        {
            if (_installed)
            {
                string exe = Path.Combine(DestFull(), "BattleTech.exe");
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = DestFull(), UseShellExecute = true });
                Close();
                return;
            }
            string dest = DestFull();
            // A folder this installer filled before is updated in place, without asking.
            if (Directory.Exists(dest) && Directory.GetFileSystemEntries(dest).Length > 0 && !File.Exists(Path.Combine(dest, Installer.RecordName)) &&
                MessageBox.Show(this, "That folder isn't empty. Files with the same names will be replaced. Continue?", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            string problem = Installer.ProbeWrite(dest);
            if (problem != null)
            {
                Show(_destNote, problem, Bad);
                return;
            }
            StartInstall(dest);
        }

        string DestFull() { return Path.GetFullPath(_dest.Text.Trim().Trim('"')); }

        void StartInstall(string dest)
        {
            _installing = true;
            SetInputs(false);
            _close.Text = "Cancel";
            _progress = new InstallProgress { Total = _check.Need };
            _stop = new CancellationTokenSource();
            ++_planId;
            _planSettle.Stop();
            RetailCheck check = _check;
            InstallProgress progress = _progress;
            CancellationToken token = _stop.Token;
            RemotePackage remote = Installer.Remote;
            _phase = remote != null ? "Downloading" : "Installing";
            _tick.Start();
            string message = null;
            Task.Run(() =>
            {
                if (remote != null)
                {
                    InstallResult f = Installer.Fetch(Installer.PlanInstall(check, dest), dest, progress, token, out message);
                    if (f != InstallResult.Installed) return f;
                    _phase = "Installing";
                    Interlocked.Exchange(ref progress.Done, 0);
                }
                return Installer.Install(check, dest, progress, token, out message);
            }).ContinueWith(t =>
            {
                _tick.Stop();
                _installing = false;
                InstallResult r = t.IsFaulted ? InstallResult.Failed : t.Result;
                string text = t.IsFaulted ? "Install failed: " + t.Exception.GetBaseException().Message : message;
                if (r == InstallResult.Installed)
                {
                    _installed = true;
                    _bar.Value = _bar.Maximum;
                    if (remote != null) text = "Release " + remote.Version + ". " + text;
                    Show(_status, text + " Steam must be running when you play.", Good);
                    _go.Text = "Start game";
                    _go.Enabled = true;
                    _close.Text = "Close";
                    _verify.Enabled = true;
                    ShowVerify();
                }
                else
                {
                    Show(_status, text, r == InstallResult.Cancelled ? SystemColors.ControlText : Bad);
                    _close.Text = "Close";
                    SetInputs(true);
                    ShowDest();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetInputs(bool on)
        {
            _retail.Enabled = _code.Enabled = _dest.Enabled = _browseRetail.Enabled = _browseDest.Enabled = _go.Enabled = _updates.Enabled = _verify.Enabled = on;
        }

        /// <summary>Verify install shows when the destination holds a public manifest this installer left there.</summary>
        void ShowVerify()
        {
            bool has;
            try { has = File.Exists(Path.Combine(DestFull(), PublicManifest.FileName)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { has = false; }
            _verify.Visible = has;
            _verify.Enabled = has && !_installing;
        }

        /// <summary>Checks every installed file against the install's copy of the public manifest (Installer.Verify).</summary>
        void StartVerify()
        {
            string dest = DestFull();
            _installing = true;
            SetInputs(false);
            _close.Text = "Cancel";
            _progress = new InstallProgress();
            _stop = new CancellationTokenSource();
            InstallProgress progress = _progress;
            CancellationToken token = _stop.Token;
            _phase = "Checking";
            _tick.Start();
            string message = null;
            Task.Run(() => Installer.Verify(dest, progress, token, out message)).ContinueWith(t =>
            {
                _tick.Stop();
                _installing = false;
                InstallResult r = t.IsFaulted ? InstallResult.Failed : t.Result;
                string text = t.IsFaulted ? "Check failed: " + t.Exception.GetBaseException().Message : message;
                _bar.Value = r == InstallResult.Installed ? _bar.Maximum : 0;
                Show(_status, text, r == InstallResult.Installed ? Good : r == InstallResult.Cancelled ? SystemColors.ControlText : Bad);
                _close.Text = "Close";
                SetInputs(true);
                if (_installed) _go.Enabled = true; else ShowDest();
                ShowVerify();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowProgress()
        {
            long done = Interlocked.Read(ref _progress.Done), total = Math.Max(1, _progress.Total);
            _bar.Value = (int)Math.Min(_bar.Maximum, done * _bar.Maximum / total);
            Show(_status, string.Format("{0}... {1} of {2}", _phase, Amount(done), Amount(total)), SystemColors.ControlText);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_installing) return;
            e.Cancel = true;
            if (_stop.IsCancellationRequested) return;
            if (MessageBox.Show(this, _phase == "Checking" ? "Stop checking the install?" : "Stop installing? Run this again later to finish.", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _stop.Cancel();
                Show(_status, "Stopping...", SystemColors.ControlText);
            }
        }
    }
}
