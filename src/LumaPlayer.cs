using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Luma Player")]
[assembly: AssemblyDescription("A focused HDR video player for Windows")]
[assembly: AssemblyCompany("Luma Player")]
[assembly: AssemblyProduct("Luma Player")]
[assembly: AssemblyVersion("0.4.2.0")]
[assembly: AssemblyFileVersion("0.4.2.0")]

namespace LumaPlayer
{
    internal static class LumaPalette
    {
        public static readonly Color Window = Color.FromArgb(9, 11, 15);
        public static readonly Color Panel = Color.FromArgb(20, 24, 31);
        public static readonly Color PanelRaised = Color.FromArgb(27, 32, 41);
        public static readonly Color Accent = Color.FromArgb(242, 108, 76);
        public static readonly Color AccentHover = Color.FromArgb(255, 130, 96);
        public static readonly Color Border = Color.FromArgb(52, 61, 74);
        public static readonly Color Disabled = Color.FromArgb(37, 42, 51);
        public static readonly Color Muted = Color.FromArgb(151, 160, 173);
        public static readonly Color Text = Color.FromArgb(242, 245, 248);
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string initialFile = null;
            bool newWindow = false;
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (String.Equals(arg, FileAssociationSpec.NewWindowArgument, StringComparison.OrdinalIgnoreCase))
                        newWindow = true;
                    else if (initialFile == null && File.Exists(arg))
                        initialFile = Path.GetFullPath(arg);
                }
            }

            // In single-window mode, double-clicking another video reuses the open
            // window. "--new-window" (or turning the mode off) always opens a new one.
            bool isFirstInstance;
            using (Mutex instanceMutex = new Mutex(false, SingleInstance.MutexName, out isFirstInstance))
            {
                if (!isFirstInstance && !newWindow && UserSettings.LoadSingleWindow() &&
                    SingleInstance.TrySendToPrimary(initialFile))
                    return;
                Application.Run(new PlayerForm(initialFile));
            }
        }
    }

    internal static class SingleInstance
    {
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        private const int AnyProcess = -1;
        private static readonly string InstanceKey = BuildInstanceKey();

        public static string MutexName
        {
            get { return "Local\\LumaPlayer-" + InstanceKey; }
        }

        private static string PipeName
        {
            get { return "LumaPlayer-Open-" + InstanceKey; }
        }

        private static string BuildInstanceKey()
        {
            string user;
            try { user = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; }
            catch { user = Environment.UserName; }
            return user + "-" + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TrySendToPrimary(string path)
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(1500);
                    AllowSetForegroundWindow(AnyProcess);
                    using (StreamWriter writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(path ?? "");
                        writer.Flush();
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Listen(Action<string> onOpen)
        {
            Thread listener = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (StreamReader reader = new StreamReader(server, new UTF8Encoding(false)))
                                onOpen(reader.ReadLine() ?? "");
                        }
                    }
                    catch (IOException)
                    {
                        // Another Luma window already owns the pipe; take over once it closes.
                        Thread.Sleep(1000);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
            listener.IsBackground = true;
            listener.Name = "LumaPlayer single instance";
            listener.Start();
        }
    }

    internal sealed class PlayerForm : Form
    {
        private static readonly Color WindowColor = LumaPalette.Window;
        private static readonly Color PanelColor = LumaPalette.Panel;
        private static readonly Color MutedColor = LumaPalette.Muted;
        private static readonly Color TextColor = LumaPalette.Text;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        private readonly string _initialFile;
        private readonly Panel _videoSurface;
        private readonly Panel _emptyState;
        private readonly LumaMark _emptyMark;
        private readonly Label _emptyTitle;
        private readonly Label _emptyHint;
        private readonly Panel _controls;
        private readonly SeekBar _seekBar;
        private readonly Label _elapsedLabel;
        private readonly Label _remainingLabel;
        private readonly Label _totalLabel;
        private readonly Label _statusLabel;
        private readonly Button _openButton;
        private readonly Button _backButton;
        private readonly Button _playButton;
        private readonly Button _forwardButton;
        private readonly Button _muteButton;
        private readonly VolumeSlider _volumeBar;
        private readonly Button _audioButton;
        private readonly Button _subtitleButton;
        private readonly Button _speedButton;
        private readonly Button _settingsButton;
        private readonly Button _fullScreenButton;
        private readonly ToolTip _toolTip;

        private MpvProcess _mpv;
        private string _mpvPath;
        private bool _mpvPathResolved;
        private bool _isPaused = true;
        private bool _isMuted;
        private bool _hasFile;
        private bool _isSeeking;
        private bool _isFullscreen;
        private bool _cursorHidden;
        private readonly System.Windows.Forms.Timer _fullscreenMouseTimer;
        private Point _lastCursorPosition;
        private int _lastMouseMoveTick;
        private int _openMenus;
        private bool _eofReached;
        private bool _fitWindowPending;
        private int _subPos = 100;
        private int _subScale = 100;
        private readonly List<string> _pendingSubtitles = new List<string>();

        private static readonly string[] SubtitleExtensions = new string[]
        {
            ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi", ".sami", ".txt"
        };
        private double _duration;
        private double _position;
        private double _speed = 1;
        private string _hwdec;
        private double _pendingPosition;
        private int _positionUpdateQueued;
        private Rectangle _restoreBounds;
        private FormBorderStyle _restoreBorderStyle;
        private FormWindowState _restoreWindowState;

        public PlayerForm(string initialFile)
        {
            _initialFile = initialFile;
            SuspendLayout();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = "Luma Player";
            Opacity = 0;
            BackColor = WindowColor;
            ForeColor = TextColor;
            ClientSize = new Size(1180, 720);
            MinimumSize = new Size(900, 520);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            _videoSurface = new Panel();
            _videoSurface.BackColor = Color.Black;
            _videoSurface.Dock = DockStyle.Fill;
            _videoSurface.TabStop = false;
            _videoSurface.Cursor = Cursors.Default;
            Controls.Add(_videoSurface);

            _emptyState = new Panel();
            _emptyState.BackColor = WindowColor;
            _emptyState.Size = new Size(520, 150);
            _emptyState.Anchor = AnchorStyles.None;
            _emptyState.Cursor = Cursors.Hand;

            _emptyTitle = new Label();
            _emptyTitle.AutoSize = false;
            _emptyTitle.Text = "拖入视频即可播放";
            _emptyTitle.TextAlign = ContentAlignment.MiddleCenter;
            _emptyTitle.Font = new Font("Segoe UI", 19F, FontStyle.Bold, GraphicsUnit.Point);
            _emptyTitle.ForeColor = TextColor;
            _emptyTitle.Dock = DockStyle.Top;
            _emptyTitle.Height = 48;
            _emptyTitle.Cursor = Cursors.Hand;

            _emptyHint = new Label();
            _emptyHint.AutoSize = false;
            _emptyHint.Text = "或点击这里打开本地视频  ·  HDR 与杜比视界自动适配显示器";
            _emptyHint.TextAlign = ContentAlignment.TopCenter;
            _emptyHint.Font = new Font("Segoe UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            _emptyHint.ForeColor = MutedColor;
            _emptyHint.Dock = DockStyle.Fill;
            _emptyHint.Cursor = Cursors.Hand;

            _emptyMark = new LumaMark();
            _emptyMark.Dock = DockStyle.Top;
            _emptyMark.Height = 58;
            _emptyMark.Cursor = Cursors.Hand;

            _emptyState.Controls.Add(_emptyHint);
            _emptyState.Controls.Add(_emptyTitle);
            _emptyState.Controls.Add(_emptyMark);
            _videoSurface.Controls.Add(_emptyState);

            _controls = new BufferedPanel();
            _controls.SuspendLayout();
            _controls.BackColor = PanelColor;
            _controls.Dock = DockStyle.Bottom;
            _controls.Height = 116;
            _controls.Padding = new Padding(18, 10, 18, 10);
            Controls.Add(_controls);
            // WinForms docks from the back of the z-order forward. The Fill video
            // surface must be in front so it only gets the space above the control
            // bar; otherwise it spans the whole window and the bar hides the bottom
            // of the picture.
            _videoSurface.BringToFront();

            BufferedTableLayoutPanel controlLayout = new BufferedTableLayoutPanel();
            controlLayout.SuspendLayout();
            controlLayout.Dock = DockStyle.Fill;
            controlLayout.Margin = Padding.Empty;
            controlLayout.Padding = Padding.Empty;
            controlLayout.ColumnCount = 1;
            controlLayout.RowCount = 2;
            controlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            controlLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            controlLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _controls.Controls.Add(controlLayout);

            BufferedTableLayoutPanel timeline = new BufferedTableLayoutPanel();
            timeline.SuspendLayout();
            timeline.Dock = DockStyle.Fill;
            timeline.BackColor = PanelColor;
            timeline.Margin = Padding.Empty;
            timeline.Padding = Padding.Empty;
            timeline.ColumnCount = 2;
            timeline.RowCount = 1;
            timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 366F));
            timeline.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            controlLayout.Controls.Add(timeline, 0, 0);

            BufferedTableLayoutPanel times = new BufferedTableLayoutPanel();
            times.SuspendLayout();
            times.Dock = DockStyle.Fill;
            times.BackColor = PanelColor;
            times.Margin = Padding.Empty;
            times.Padding = new Padding(12, 0, 0, 0);
            times.ColumnCount = 3;
            times.RowCount = 1;
            times.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            times.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            times.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            times.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _elapsedLabel = CreateTimeLabel("已播放 00:00");
            _remainingLabel = CreateTimeLabel("剩余 00:00");
            _totalLabel = CreateTimeLabel("总时长 00:00");
            times.Controls.Add(_elapsedLabel, 0, 0);
            times.Controls.Add(_remainingLabel, 1, 0);
            times.Controls.Add(_totalLabel, 2, 0);
            timeline.Controls.Add(times, 1, 0);

            _seekBar = new SeekBar();
            _seekBar.Dock = DockStyle.Fill;
            _seekBar.Margin = new Padding(0, 0, 10, 0);
            _seekBar.Enabled = false;
            _seekBar.ValueChanged += OnSeekRequested;
            _seekBar.SeekStarted += delegate { _isSeeking = true; };
            _seekBar.SeekEnded += delegate { _isSeeking = false; };
            _seekBar.PreviewChanged += delegate
            {
                if (_duration > 0)
                    UpdateTimeLabels(_duration * _seekBar.Value, _duration);
            };
            timeline.Controls.Add(_seekBar, 0, 0);

            BufferedTableLayoutPanel row = new BufferedTableLayoutPanel();
            row.SuspendLayout();
            row.Dock = DockStyle.Fill;
            row.BackColor = PanelColor;
            row.Margin = Padding.Empty;
            row.Padding = Padding.Empty;
            row.ColumnCount = 3;
            row.RowCount = 1;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 426F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 338F));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            controlLayout.Controls.Add(row, 0, 1);

            _toolTip = new ToolTip();

            // The video surface belongs to the mpv process, so the host never sees
            // its mouse messages. Poll the cursor while full screen instead.
            _fullscreenMouseTimer = new System.Windows.Forms.Timer();
            _fullscreenMouseTimer.Interval = 150;
            _fullscreenMouseTimer.Tick += OnFullscreenMouseTick;

            BufferedFlowLayoutPanel left = new BufferedFlowLayoutPanel();
            left.SuspendLayout();
            left.Dock = DockStyle.Fill;
            left.WrapContents = false;
            left.Padding = new Padding(0, 3, 0, 0);
            left.BackColor = PanelColor;
            row.Controls.Add(left, 0, 0);

            _openButton = CreateTextButton("打开", 56);
            _backButton = CreateTextButton("−10 秒", 60);
            _playButton = CreateTextButton("播放", 60);
            ((SkeuomorphicButton)_playButton).IsPrimary = true;
            _forwardButton = CreateTextButton("+10 秒", 60);
            _muteButton = CreateTextButton("静音", 60);

            _volumeBar = new VolumeSlider();
            _volumeBar.AutoSize = false;
            _volumeBar.Width = 64;
            _volumeBar.Height = 40;
            _volumeBar.Minimum = 0;
            _volumeBar.Maximum = 100;
            _volumeBar.Value = UserSettings.LoadVolume(80);
            _subPos = UserSettings.LoadInt("SubtitlePosition", 100, 0, 150);
            _subScale = UserSettings.LoadInt("SubtitleScale", 100, 50, 300);
            _volumeBar.BackColor = PanelColor;
            _volumeBar.Margin = new Padding(0, 6, 8, 0);
            _toolTip.SetToolTip(_volumeBar, "音量");

            Label volumeLabel = new Label();
            volumeLabel.Text = "音量";
            volumeLabel.Width = 38;
            volumeLabel.Height = 42;
            volumeLabel.Margin = Padding.Empty;
            volumeLabel.ForeColor = MutedColor;
            volumeLabel.TextAlign = ContentAlignment.MiddleCenter;

            left.Controls.Add(_openButton);
            left.Controls.Add(_backButton);
            left.Controls.Add(_playButton);
            left.Controls.Add(_forwardButton);
            left.Controls.Add(_muteButton);
            left.Controls.Add(volumeLabel);
            left.Controls.Add(_volumeBar);

            BufferedFlowLayoutPanel right = new BufferedFlowLayoutPanel();
            right.SuspendLayout();
            right.Dock = DockStyle.Fill;
            right.FlowDirection = FlowDirection.LeftToRight;
            right.WrapContents = false;
            right.Padding = new Padding(0, 3, 0, 0);
            right.BackColor = PanelColor;
            row.Controls.Add(right, 2, 0);

            _fullScreenButton = CreateTextButton("全屏", 70);
            _audioButton = CreateTextButton("音轨", 52);
            _subtitleButton = CreateTextButton("字幕", 52);
            _speedButton = CreateTextButton("倍速 1×", 68);
            _settingsButton = CreateTextButton("设置", 76);
            _toolTip.SetToolTip(_settingsButton, "窗口模式、新窗口打开和格式关联");
            right.Controls.Add(_speedButton);
            right.Controls.Add(_audioButton);
            right.Controls.Add(_subtitleButton);
            right.Controls.Add(_settingsButton);
            right.Controls.Add(_fullScreenButton);

            _statusLabel = new Label();
            _statusLabel.Text = "等待打开视频";
            _statusLabel.ForeColor = MutedColor;
            _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.AutoSize = false;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.UseCompatibleTextRendering = false;
            _statusLabel.Padding = new Padding(14, 0, 14, 0);
            row.Controls.Add(_statusLabel, 1, 0);

            _openButton.Click += delegate { OpenVideoDialog(); };
            _backButton.Click += delegate { SeekRelative(-10); };
            _playButton.Click += delegate { TogglePause(); };
            _forwardButton.Click += delegate { SeekRelative(10); };
            _muteButton.Click += delegate { ToggleMute(); };
            _fullScreenButton.Click += delegate { ToggleFullscreen(); };
            _speedButton.Click += delegate { ShowSpeedMenu(); };
            _settingsButton.Click += delegate { ShowSettingsMenu(); };
            _audioButton.Click += delegate { ShowTrackMenu(_audioButton, "audio"); };
            _subtitleButton.Click += delegate { ShowTrackMenu(_subtitleButton, "sub"); };
            _volumeBar.ValueChanged += OnVolumeChanged;

            right.ResumeLayout(false);
            left.ResumeLayout(false);
            row.ResumeLayout(false);
            times.ResumeLayout(false);
            timeline.ResumeLayout(false);
            controlLayout.ResumeLayout(false);
            _controls.ResumeLayout(false);

            _emptyState.Click += delegate { OpenVideoDialog(); };
            _emptyMark.Click += delegate { OpenVideoDialog(); };
            _emptyTitle.Click += delegate { OpenVideoDialog(); };
            _emptyHint.Click += delegate { OpenVideoDialog(); };
            _videoSurface.DoubleClick += delegate { ToggleFullscreen(); };
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            FormClosing += OnFormClosing;
            Shown += OnShown;
            Resize += delegate { CenterEmptyState(); };

            SetPlaybackButtons(false);
            CenterEmptyState();
            ResumeLayout(true);
            PerformLayout();
        }

        private Button CreateTextButton(string text, int width)
        {
            Button button = CreateBaseButton(width);
            button.Text = text;
            button.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            return button;
        }

        private Label CreateTimeLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.Margin = Padding.Empty;
            label.ForeColor = MutedColor;
            label.TextAlign = ContentAlignment.MiddleRight;
            label.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            return label;
        }

        private Button CreateBaseButton(int width)
        {
            Button button = new SkeuomorphicButton();
            button.Width = width;
            button.Height = 42;
            button.BackColor = PanelColor;
            button.ForeColor = TextColor;
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(2, 0, 2, 0);
            button.TabStop = true;
            return button;
        }

        private void OnShown(object sender, EventArgs e)
        {
            PerformLayout();
            CenterEmptyState();
            Opacity = 1;
            SingleInstance.Listen(OpenFromAnotherInstance);
            if (!String.IsNullOrEmpty(_initialFile))
                BeginInvoke(new MethodInvoker(delegate { LoadFile(_initialFile); }));
        }

        private void OpenFromAnotherInstance(string path)
        {
            SafeUi(delegate
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Activate();
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                    LoadFile(Path.GetFullPath(path));
            });
        }

        private bool EnsureMpv()
        {
            if (_mpv != null && _mpv.IsRunning)
                return true;

            if (_mpv != null)
            {
                // The previous engine crashed; release it before starting a new one.
                DetachMpv(_mpv);
                _mpv.Dispose();
                _mpv = null;
            }

            string mpvPath = FindMpv();
            if (mpvPath == null)
            {
                ShowEngineMissing();
                return false;
            }

            try
            {
                uint windowId = unchecked((uint)_videoSurface.Handle.ToInt64());
                _mpv = new MpvProcess(mpvPath, windowId, _volumeBar.Value, _subPos, _subScale);
                _mpv.PropertyChanged += OnMpvPropertyChanged;
                _mpv.EventReceived += OnMpvEvent;
                _mpv.ClientMessageReceived += OnMpvClientMessage;
                _mpv.EngineFailed += OnMpvFailed;
                _mpv.Start();
                ObservePlayerProperties();
                _statusLabel.Text = "播放器已就绪";
                return true;
            }
            catch (Exception ex)
            {
                if (_mpv != null)
                {
                    DetachMpv(_mpv);
                    _mpv.Dispose();
                }
                _mpv = null;
                ShowError("播放器核心启动失败", ex.Message);
                return false;
            }
        }

        private void DetachMpv(MpvProcess mpv)
        {
            mpv.PropertyChanged -= OnMpvPropertyChanged;
            mpv.EventReceived -= OnMpvEvent;
            mpv.ClientMessageReceived -= OnMpvClientMessage;
            mpv.EngineFailed -= OnMpvFailed;
        }

        private void SaveResumePosition()
        {
            if (!_hasFile || _mpv == null || !_mpv.IsRunning)
                return;
            // Finished videos start from the beginning next time.
            bool finished = _eofReached || (_duration > 0 && _position >= _duration - 5);
            _mpv.Command(finished ? "delete-watch-later-config" : "write-watch-later-config");
        }

        private string FindMpv()
        {
            if (_mpvPathResolved)
                return _mpvPath;

            _mpvPathResolved = true;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "mpv.exe"),
                Path.Combine(baseDir, "mpv", "mpv.exe"),
                Path.Combine(baseDir, "runtime", "mpv.exe")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    _mpvPath = candidate;
                    return _mpvPath;
                }
            }
            return null;
        }

        private void ObservePlayerProperties()
        {
            string[] properties = new string[]
            {
                "time-pos", "duration", "pause", "volume", "mute", "speed", "media-title", "hwdec-current", "eof-reached", "sub-pos", "sub-scale"
            };
            for (int i = 0; i < properties.Length; i++)
                _mpv.Observe(properties[i]);
        }

        private void OpenVideoDialog()
        {
            string path = PickVideoFile();
            if (path != null)
                LoadFile(path);
        }

        private string PickVideoFile()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "打开视频";
                dialog.Filter = "视频文件|*.mkv;*.mp4;*.m4v;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.mts;*.mpg;*.mpeg;*.wmv;*.flv;*.ogv|所有文件|*.*";
                dialog.Multiselect = false;
                dialog.CheckFileExists = true;
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private void LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                ShowError("文件不存在", path);
                return;
            }
            if (!EnsureMpv())
                return;

            SaveResumePosition();
            _hasFile = true;
            _isPaused = false;
            _eofReached = false;
            _fitWindowPending = true;
            _duration = 0;
            _position = 0;
            _seekBar.Value = 0;
            _seekBar.Enabled = true;
            _emptyState.Visible = false;
            _statusLabel.Text = "正在载入…";
            Text = Path.GetFileName(path) + " — Luma Player";
            SetPlaybackButtons(true);
            UpdatePauseButton();
            SetPlaybackSpeed(1);
            UpdateTimeLabels(0, 0);
            _mpv.Command("loadfile", path, "replace");
        }

        private void SetPlaybackButtons(bool enabled)
        {
            _backButton.Enabled = enabled;
            _playButton.Enabled = enabled;
            _forwardButton.Enabled = enabled;
            _muteButton.Enabled = enabled;
            _audioButton.Enabled = enabled;
            _subtitleButton.Enabled = enabled;
            _speedButton.Enabled = enabled;
            _fullScreenButton.Enabled = enabled;
            _volumeBar.Enabled = enabled;
        }

        private void TogglePause()
        {
            if (!_hasFile || _mpv == null)
                return;
            _mpv.SetProperty("pause", !_isPaused);
        }

        private void ToggleMute()
        {
            if (!_hasFile || _mpv == null)
                return;
            _mpv.SetProperty("mute", !_isMuted);
        }

        private void SeekRelative(double seconds)
        {
            if (!_hasFile || _mpv == null)
                return;
            _mpv.Command("seek", seconds, "relative+exact");
        }

        private void OnSeekRequested(object sender, EventArgs e)
        {
            if (!_hasFile || _mpv == null || _duration <= 0)
                return;
            double target = _duration * _seekBar.Value;
            _position = target;
            _mpv.Command("seek", target, "absolute+exact");
            UpdateTimeLabels(target, _duration);
        }

        private void OnVolumeChanged(object sender, EventArgs e)
        {
            if (_mpv == null || !_mpv.IsRunning)
                return;
            _mpv.SetProperty("volume", _volumeBar.Value);
        }

        private void OnMpvPropertyChanged(string name, object value)
        {
            if (name == "time-pos")
            {
                // mpv reports time-pos about once per frame. Keep only the latest
                // value and queue at most one UI update so the window thread never
                // falls behind during high frame rate playback.
                Interlocked.Exchange(ref _pendingPosition, ToDouble(value));
                if (Interlocked.Exchange(ref _positionUpdateQueued, 1) == 0)
                    SafeUi(ApplyPendingPosition);
                return;
            }

            SafeUi(delegate
            {
                if (name == "duration")
                {
                    _duration = ToDouble(value);
                    UpdateTimeLabels(_position, _duration);
                }
                else if (name == "pause")
                {
                    _isPaused = ToBool(value);
                    UpdatePauseButton();
                }
                else if (name == "volume")
                {
                    int volume = (int)Math.Round(ToDouble(value));
                    volume = Math.Max(_volumeBar.Minimum, Math.Min(_volumeBar.Maximum, volume));
                    if (_volumeBar.Value != volume)
                        _volumeBar.Value = volume;
                }
                else if (name == "mute")
                {
                    _isMuted = ToBool(value);
                    _muteButton.Text = _isMuted ? "取消静音" : "静音";
                    _toolTip.SetToolTip(_muteButton, _isMuted ? "恢复声音" : "静音");
                }
                else if (name == "speed")
                {
                    _speed = ToDouble(value);
                    UpdateSpeedButton();
                }
                else if (name == "media-title" && value != null)
                {
                    string title = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!String.IsNullOrEmpty(title))
                        Text = title + " — Luma Player";
                }
                else if (name == "hwdec-current" && value != null)
                {
                    string hw = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!String.IsNullOrEmpty(hw) && hw != "no")
                    {
                        _hwdec = hw.ToUpperInvariant();
                        if (_hasFile)
                            RefreshMediaStatus();
                    }
                }
                else if (name == "eof-reached")
                {
                    _eofReached = ToBool(value);
                }
                else if (name == "sub-pos" && value != null)
                {
                    _subPos = (int)Math.Round(ToDouble(value));
                }
                else if (name == "sub-scale" && value != null)
                {
                    _subScale = (int)Math.Round(ToDouble(value) * 100);
                }
            });
        }

        private void ApplyPendingPosition()
        {
            Interlocked.Exchange(ref _positionUpdateQueued, 0);
            double position = Interlocked.CompareExchange(ref _pendingPosition, 0, 0);
            _position = position;
            if (_isSeeking)
                return;
            if (_duration > 0)
                _seekBar.Value = Math.Max(0, Math.Min(1, position / _duration));
            UpdateTimeLabels(position, _duration);
        }

        private void OnMpvEvent(string eventName)
        {
            SafeUi(delegate
            {
                if (eventName == "file-loaded")
                {
                    _hasFile = true;
                    _emptyState.Visible = false;
                    AddPendingSubtitles();
                    RefreshMediaStatus();
                }
                else if (eventName == "end-file")
                {
                    _isPaused = true;
                    UpdatePauseButton();
                }
                else if (eventName == "shutdown")
                {
                    _hasFile = false;
                    SetPlaybackButtons(false);
                }
                else if (eventName == "video-reconfig" || eventName == "playback-restart")
                {
                    RefreshMediaStatus();
                }
            });
        }

        private void OnMpvClientMessage(string message)
        {
            SafeUi(delegate
            {
                if (message == "luma-toggle-fullscreen")
                    ToggleFullscreen();
                else if (message == "luma-exit-fullscreen" && _isFullscreen)
                    ToggleFullscreen();
            });
        }

        private void RefreshMediaStatus()
        {
            MpvProcess mpv = _mpv;
            if (mpv == null)
                return;

            mpv.GetProperty("video-params", delegate(object value)
            {
                IDictionary<string, object> parameters = value as IDictionary<string, object>;
                string transfer = GetMapString(parameters, "gamma");
                string primaries = GetMapString(parameters, "primaries");
                string colorMatrix = GetMapString(parameters, "colormatrix");
                bool isDolbyVision = String.Equals(colorMatrix, "dolbyvision", StringComparison.OrdinalIgnoreCase);
                double displayWidth = ToDouble(parameters != null && parameters.ContainsKey("dw") ? parameters["dw"] : null);
                double displayHeight = ToDouble(parameters != null && parameters.ContainsKey("dh") ? parameters["dh"] : null);
                if (displayWidth > 0 && displayHeight > 0)
                    SafeUi(delegate { FitWindowToVideo(displayWidth, displayHeight); });

                mpv.GetProperty("video-target-params", delegate(object targetValue)
                {
                    IDictionary<string, object> target = targetValue as IDictionary<string, object>;
                    string targetTransfer = GetMapString(target, "gamma");
                    bool targetIsHdr = String.Equals(targetTransfer, "pq", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(targetTransfer, "hlg", StringComparison.OrdinalIgnoreCase);

                    mpv.GetProperty("video-codec", delegate(object codecValue)
                    {
                        string codec = codecValue == null ? "" : Convert.ToString(codecValue, CultureInfo.InvariantCulture);
                        string picture;
                        if (isDolbyVision)
                            picture = targetIsHdr ? "Dolby Vision → HDR" : "Dolby Vision → SDR";
                        else if (String.Equals(transfer, "pq", StringComparison.OrdinalIgnoreCase))
                            picture = targetIsHdr ? "HDR10" : "HDR10 → SDR";
                        else if (String.Equals(transfer, "hlg", StringComparison.OrdinalIgnoreCase))
                            picture = targetIsHdr ? "HLG HDR" : "HLG → SDR";
                        else
                            picture = "SDR";

                        // These callbacks run on the IPC reader thread; touch controls only on the UI thread.
                        SafeUi(delegate
                        {
                            List<string> parts = new List<string>();
                            parts.Add(picture);
                            if (!String.IsNullOrEmpty(primaries))
                                parts.Add(primaries.ToUpperInvariant());
                            if (!String.IsNullOrEmpty(codec))
                                parts.Add(GetCompactCodecName(codec));
                            if (!String.IsNullOrEmpty(_hwdec))
                                parts.Add(_hwdec);
                            _statusLabel.Text = String.Join("  ·  ", parts.ToArray());
                        });
                    });
                });
            });
        }

        // Once per opened file, size the window to the video's aspect ratio so the
        // picture fills the video area without black bars. Keeps the current
        // video width where possible and stays inside the screen's work area.
        private void FitWindowToVideo(double videoWidth, double videoHeight)
        {
            if (!_fitWindowPending)
                return;
            _fitWindowPending = false;
            if (_isFullscreen || WindowState != FormWindowState.Normal)
                return;

            Size area = _videoSurface.ClientSize;
            if (area.Width <= 0 || area.Height <= 0)
                return;
            int extraWidth = Width - area.Width;
            int extraHeight = Height - area.Height;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            double aspect = videoWidth / videoHeight;

            double width = area.Width;
            double height = width / aspect;
            double maxWidth = work.Width - extraWidth;
            double maxHeight = work.Height - extraHeight;
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * aspect;
            }
            if (width > maxWidth)
            {
                width = maxWidth;
                height = width / aspect;
            }

            Size size = new Size(
                Math.Max(MinimumSize.Width, (int)Math.Round(width) + extraWidth),
                Math.Max(MinimumSize.Height, (int)Math.Round(height) + extraHeight));
            if (size == Size)
                return;

            Point center = new Point(Left + Width / 2, Top + Height / 2);
            int left = Math.Max(work.Left, Math.Min(work.Right - size.Width, center.X - size.Width / 2));
            int top = Math.Max(work.Top, Math.Min(work.Bottom - size.Height, center.Y - size.Height / 2));
            Bounds = new Rectangle(left, top, size.Width, size.Height);
        }

        private static string GetMapString(IDictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null)
                return "";
            return Convert.ToString(map[key], CultureInfo.InvariantCulture);
        }

        private static string GetCompactCodecName(string codec)
        {
            string value = codec.ToUpperInvariant();
            if (value.Contains("HEVC") || value.Contains("H.265"))
                return "HEVC";
            if (value.Contains("AV1"))
                return "AV1";
            if (value.Contains("H.264") || value.Contains("AVC"))
                return "H.264";
            if (value.Contains("VP9"))
                return "VP9";
            return value.Length > 16 ? value.Substring(0, 16) : value;
        }

        private void ShowTrackMenu(Control anchor, string trackType)
        {
            if (_mpv == null || !_hasFile)
                return;

            _mpv.GetProperty("track-list", delegate(object value)
            {
                SafeUi(delegate
                {
                    ContextMenuStrip menu = CreateDarkMenu();
                    IEnumerable tracks = value as IEnumerable;
                    int count = 0;
                    if (tracks != null)
                    {
                        foreach (object item in tracks)
                        {
                            IDictionary<string, object> track = item as IDictionary<string, object>;
                            if (track == null || GetMapString(track, "type") != trackType)
                                continue;

                            string id = GetMapString(track, "id");
                            string title = GetMapString(track, "title");
                            string lang = GetMapString(track, "lang");
                            bool selected = track.ContainsKey("selected") && ToBool(track["selected"]);
                            string fallback = trackType == "audio" ? "音轨 " + id : "字幕 " + id;
                            string label = !String.IsNullOrEmpty(title) ? title : fallback;
                            if (!String.IsNullOrEmpty(lang))
                                label += "  [" + lang + "]";

                            ToolStripMenuItem option = new ToolStripMenuItem(label);
                            option.Checked = selected;
                            option.Tag = id;
                            option.Click += delegate(object sender, EventArgs args)
                            {
                                ToolStripMenuItem clicked = (ToolStripMenuItem)sender;
                                _mpv.SetProperty(trackType == "audio" ? "aid" : "sid", Convert.ToString(clicked.Tag, CultureInfo.InvariantCulture));
                            };
                            menu.Items.Add(option);
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        ToolStripMenuItem none = new ToolStripMenuItem(trackType == "audio" ? "没有其他音轨" : "没有内嵌字幕");
                        none.Enabled = false;
                        menu.Items.Add(none);
                    }

                    if (trackType == "sub")
                    {
                        menu.Items.Add(new ToolStripSeparator());
                        ToolStripMenuItem disable = new ToolStripMenuItem("关闭字幕");
                        disable.Click += delegate { _mpv.SetProperty("sid", "no"); };
                        menu.Items.Add(disable);
                        ToolStripMenuItem external = new ToolStripMenuItem("加载外部字幕…");
                        external.ToolTipText = "也可以把字幕文件直接拖进播放窗口";
                        external.Click += delegate { LoadExternalSubtitle(); };
                        menu.Items.Add(external);

                        menu.Items.Add(new ToolStripSeparator());
                        AddSubtitleStyleItem(menu, "字幕放大", "Ctrl+=", delegate { AdjustSubtitleScale(10); });
                        AddSubtitleStyleItem(menu, "字幕缩小", "Ctrl+-", delegate { AdjustSubtitleScale(-10); });
                        AddSubtitleStyleItem(menu, "字幕上移", "Ctrl+↑", delegate { AdjustSubtitlePosition(-2); });
                        AddSubtitleStyleItem(menu, "字幕下移", "Ctrl+↓", delegate { AdjustSubtitlePosition(2); });
                        AddSubtitleStyleItem(menu, "恢复默认大小和位置", "Ctrl+0", delegate { ResetSubtitleStyle(); });
                    }

                    menu.Show(anchor, new Point(0, anchor.Height));
                });
            });
        }

        private ContextMenuStrip CreateDarkMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Opened += delegate { _openMenus++; };
            menu.Closed += delegate { _openMenus = Math.Max(0, _openMenus - 1); };
            menu.BackColor = PanelColor;
            menu.ForeColor = TextColor;
            menu.Font = Font;
            menu.ShowImageMargin = false;
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            return menu;
        }

        private void ShowSettingsMenu()
        {
            ContextMenuStrip menu = CreateDarkMenu();

            ToolStripMenuItem singleWindow = new ToolStripMenuItem("只用一个窗口播放");
            singleWindow.Checked = UserSettings.LoadSingleWindow();
            singleWindow.ToolTipText = "开启后，双击其他视频会在当前窗口切换播放";
            singleWindow.Click += delegate { UserSettings.SaveSingleWindow(!UserSettings.LoadSingleWindow()); };
            menu.Items.Add(singleWindow);

            ToolStripMenuItem newWindow = new ToolStripMenuItem("在新窗口打开视频…    Ctrl+N");
            newWindow.Click += delegate { OpenVideoInNewWindow(); };
            menu.Items.Add(newWindow);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem associate = new ToolStripMenuItem("关联视频格式…");
            associate.Click += delegate { RegisterFileAssociations(); };
            menu.Items.Add(associate);

            Size preferred = menu.GetPreferredSize(Size.Empty);
            menu.Show(_settingsButton, new Point(_settingsButton.Width - preferred.Width, -preferred.Height));
        }

        private void OpenVideoInNewWindow()
        {
            string path = PickVideoFile();
            if (path == null)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(
                    Application.ExecutablePath,
                    FileAssociationSpec.NewWindowArgument + " \"" + path + "\""
                ) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                ShowError("无法打开新窗口", ex.Message);
            }
        }

        private void ShowSpeedMenu()
        {
            if (_mpv == null || !_hasFile)
                return;

            ContextMenuStrip menu = CreateDarkMenu();
            double[] speeds = new double[] { 1, 2, 3, 4 };
            for (int i = 0; i < speeds.Length; i++)
            {
                double speed = speeds[i];
                string label = speed == 1 ? "1×  正常速度" : speed.ToString("0", CultureInfo.InvariantCulture) + "×";
                ToolStripMenuItem option = new ToolStripMenuItem(label);
                option.Checked = Math.Abs(_speed - speed) < 0.01;
                option.Tag = speed;
                option.Click += delegate(object sender, EventArgs args)
                {
                    ToolStripMenuItem clicked = (ToolStripMenuItem)sender;
                    SetPlaybackSpeed(Convert.ToDouble(clicked.Tag, CultureInfo.InvariantCulture));
                };
                menu.Items.Add(option);
            }

            Size preferred = menu.GetPreferredSize(Size.Empty);
            menu.Show(_speedButton, new Point(0, -preferred.Height));
        }

        private void SetPlaybackSpeed(double speed)
        {
            if (_mpv == null || !_mpv.IsRunning)
                return;
            _speed = speed;
            UpdateSpeedButton();
            _mpv.SetProperty("speed", speed);
        }

        private void UpdateSpeedButton()
        {
            string value = Math.Abs(_speed - Math.Round(_speed)) < 0.01
                ? Math.Round(_speed).ToString("0", CultureInfo.InvariantCulture)
                : _speed.ToString("0.##", CultureInfo.InvariantCulture);
            _speedButton.Text = "倍速 " + value + "×";
            _toolTip.SetToolTip(_speedButton, "播放速度：" + value + " 倍");
        }

        private void LoadExternalSubtitle()
        {
            if (_mpv == null || !_hasFile)
                return;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "加载外部字幕";
                dialog.Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.sup;*.smi;*.sami;*.txt|所有文件|*.*";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    AddSubtitles(dialog.FileNames);
            }
        }

        private static bool IsSubtitleFile(string path)
        {
            string extension = Path.GetExtension(path);
            for (int i = 0; i < SubtitleExtensions.Length; i++)
            {
                if (String.Equals(extension, SubtitleExtensions[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void AddSubtitles(IList<string> paths)
        {
            if (_mpv == null || paths.Count == 0)
                return;
            // Select the last one added so the user immediately sees it.
            for (int i = 0; i < paths.Count; i++)
                _mpv.Command("sub-add", paths[i], i == paths.Count - 1 ? "select" : "auto");
            _statusLabel.Text = paths.Count == 1
                ? "已加载字幕：" + Path.GetFileName(paths[0])
                : "已加载 " + paths.Count.ToString(CultureInfo.InvariantCulture) + " 个字幕文件";
        }

        private void AddPendingSubtitles()
        {
            if (_pendingSubtitles.Count == 0)
                return;
            List<string> subtitles = new List<string>(_pendingSubtitles);
            _pendingSubtitles.Clear();
            AddSubtitles(subtitles);
        }

        private void AddSubtitleStyleItem(ContextMenuStrip menu, string text, string shortcut, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.ShortcutKeyDisplayString = shortcut;
            item.Click += onClick;
            menu.Items.Add(item);
        }

        // sub-pos is a percentage of the video height (100 = bottom edge, up to
        // 150 to push below it); sub-scale is stored in percent.
        private void AdjustSubtitlePosition(int delta)
        {
            _subPos = Math.Max(0, Math.Min(150, _subPos + delta));
            if (_mpv != null && _mpv.IsRunning)
                _mpv.SetProperty("sub-pos", _subPos);
            _statusLabel.Text = "字幕位置：" + _subPos.ToString(CultureInfo.InvariantCulture) + "（默认 100，数值越大越靠下）";
        }

        private void AdjustSubtitleScale(int deltaPercent)
        {
            _subScale = Math.Max(50, Math.Min(300, _subScale + deltaPercent));
            if (_mpv != null && _mpv.IsRunning)
                _mpv.SetProperty("sub-scale", _subScale / 100.0);
            _statusLabel.Text = "字幕大小：" + _subScale.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void ResetSubtitleStyle()
        {
            _subPos = 100;
            _subScale = 100;
            if (_mpv != null && _mpv.IsRunning)
            {
                _mpv.SetProperty("sub-pos", _subPos);
                _mpv.SetProperty("sub-scale", 1.0);
            }
            _statusLabel.Text = "字幕已恢复默认大小和位置";
        }

        private void RegisterFileAssociations()
        {
            string executable = Application.ExecutablePath;
            string applicationName = FileAssociationSpec.ApplicationName;
            string progId = FileAssociationSpec.ProgId;
            string command = FileAssociationSpec.BuildOpenCommand(executable);
            string[] extensions = FileAssociationSpec.Extensions;

            try
            {
                using (RegistryKey prog = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + progId))
                {
                    prog.SetValue("", "Luma Player 视频");
                    using (RegistryKey icon = prog.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", FileAssociationSpec.BuildIconValue(executable));
                    using (RegistryKey open = prog.CreateSubKey("shell\\open\\command"))
                        open.SetValue("", command);
                    using (RegistryKey newWindow = prog.CreateSubKey("shell\\" + FileAssociationSpec.NewWindowVerb))
                    {
                        newWindow.SetValue("", FileAssociationSpec.NewWindowVerbLabel);
                        using (RegistryKey newWindowCommand = newWindow.CreateSubKey("command"))
                            newWindowCommand.SetValue("", FileAssociationSpec.BuildNewWindowCommand(executable));
                    }
                }

                using (RegistryKey application = Registry.CurrentUser.CreateSubKey("Software\\Classes\\Applications\\LumaPlayer.exe"))
                {
                    application.SetValue("FriendlyAppName", applicationName);
                    using (RegistryKey open = application.CreateSubKey("shell\\open\\command"))
                        open.SetValue("", command);
                    using (RegistryKey supported = application.CreateSubKey("SupportedTypes"))
                    {
                        for (int i = 0; i < extensions.Length; i++)
                            supported.SetValue(extensions[i], "");
                    }
                }

                using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey("Software\\LumaPlayer\\Capabilities"))
                {
                    capabilities.SetValue("ApplicationName", applicationName);
                    capabilities.SetValue("ApplicationDescription", "高性能 HDR 与杜比视界本地视频播放器");
                    using (RegistryKey associations = capabilities.CreateSubKey("FileAssociations"))
                    {
                        for (int i = 0; i < extensions.Length; i++)
                            associations.SetValue(extensions[i], progId);
                    }
                }

                using (RegistryKey registered = Registry.CurrentUser.CreateSubKey("Software\\RegisteredApplications"))
                    registered.SetValue(applicationName, "Software\\LumaPlayer\\Capabilities");

                for (int i = 0; i < extensions.Length; i++)
                {
                    using (RegistryKey openWith = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + extensions[i] + "\\OpenWithProgids"))
                        openWith.SetValue(progId, new byte[0], RegistryValueKind.None);
                }

                RefreshShellAssociations();

                MessageBox.Show(
                    this,
                    "Luma Player 已注册为视频播放器。接下来请在 Windows“默认应用”页面中选择 Luma Player；确认一次后，就可以双击视频直接播放。",
                    "关联视频格式",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                Process.Start(new ProcessStartInfo(
                    "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(applicationName)
                ) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("无法关联视频格式", ex.Message);
            }
        }

        private static void RefreshShellAssociations()
        {
            SHChangeNotify(0x08000000U, 0x00002000U, IntPtr.Zero, IntPtr.Zero);
        }

        private void ToggleFullscreen()
        {
            if (!_hasFile)
                return;

            if (!_isFullscreen)
            {
                _restoreBounds = Bounds;
                _restoreBorderStyle = FormBorderStyle;
                _restoreWindowState = WindowState;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                TopMost = true;
                _isFullscreen = true;
                _fullScreenButton.Text = "退出全屏";
                HideFullscreenControls();
                _lastCursorPosition = Cursor.Position;
                _fullscreenMouseTimer.Start();
            }
            else
            {
                _isFullscreen = false;
                _fullscreenMouseTimer.Stop();
                ShowFullscreenControls();
                TopMost = false;
                FormBorderStyle = _restoreBorderStyle;
                Bounds = _restoreBounds;
                WindowState = _restoreWindowState;
                _fullScreenButton.Text = "全屏";
            }
        }

        private void ShowFullscreenControls()
        {
            if (!_controls.Visible)
                _controls.Visible = true;
            if (_cursorHidden)
            {
                Cursor.Show();
                _cursorHidden = false;
            }
        }

        private void OnFullscreenMouseTick(object sender, EventArgs e)
        {
            if (!_isFullscreen)
                return;

            Point position = Cursor.Position;
            if (position != _lastCursorPosition)
            {
                _lastCursorPosition = position;
                _lastMouseMoveTick = Environment.TickCount;
                ShowFullscreenControls();
                return;
            }

            if (!_controls.Visible || _openMenus > 0)
                return;
            // Keep the bar while the pointer rests on it.
            if (_controls.RectangleToScreen(_controls.ClientRectangle).Contains(position))
                return;
            if (unchecked(Environment.TickCount - _lastMouseMoveTick) >= 2500)
                HideFullscreenControls();
        }

        private void HideFullscreenControls()
        {
            if (_controls.Visible)
                _controls.Visible = false;
            if (!_cursorHidden)
            {
                Cursor.Hide();
                _cursorHidden = true;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.O))
            {
                OpenVideoDialog();
                return true;
            }
            if (keyData == (Keys.Control | Keys.N))
            {
                OpenVideoInNewWindow();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Up) || keyData == (Keys.Control | Keys.Down))
            {
                AdjustSubtitlePosition(keyData == (Keys.Control | Keys.Up) ? -2 : 2);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Oemplus) || keyData == (Keys.Control | Keys.Add))
            {
                AdjustSubtitleScale(10);
                return true;
            }
            if (keyData == (Keys.Control | Keys.OemMinus) || keyData == (Keys.Control | Keys.Subtract))
            {
                AdjustSubtitleScale(-10);
                return true;
            }
            if (keyData == (Keys.Control | Keys.D0) || keyData == (Keys.Control | Keys.NumPad0))
            {
                ResetSubtitleStyle();
                return true;
            }
            if (keyData == Keys.Space)
            {
                TogglePause();
                return true;
            }
            if (keyData == Keys.Left)
            {
                SeekRelative(-5);
                return true;
            }
            if (keyData == Keys.Right)
            {
                SeekRelative(5);
                return true;
            }
            if (keyData == Keys.Up)
            {
                _volumeBar.Value = Math.Min(100, _volumeBar.Value + 5);
                return true;
            }
            if (keyData == Keys.Down)
            {
                _volumeBar.Value = Math.Max(0, _volumeBar.Value - 5);
                return true;
            }
            if (keyData == Keys.F || (keyData == Keys.Escape && _isFullscreen))
            {
                ToggleFullscreen();
                return true;
            }
            if (keyData == Keys.M)
            {
                ToggleMute();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
                return;

            // A video and its subtitle files can be dropped together; subtitles
            // dropped on their own are added to the video that is playing.
            string video = null;
            List<string> subtitles = new List<string>();
            foreach (string file in files)
            {
                if (IsSubtitleFile(file))
                    subtitles.Add(file);
                else if (video == null)
                    video = file;
            }

            if (video != null)
            {
                _pendingSubtitles.Clear();
                _pendingSubtitles.AddRange(subtitles);
                LoadFile(video);
            }
            else if (_hasFile)
                AddSubtitles(subtitles);
            else
                _statusLabel.Text = "请先打开视频，再拖入字幕";
        }

        private void OnMpvFailed(string message)
        {
            SafeUi(delegate
            {
                if (_isFullscreen)
                    ToggleFullscreen();
                _hasFile = false;
                _seekBar.Enabled = false;
                SetPlaybackButtons(false);
                _emptyState.Visible = true;
                _statusLabel.Text = "播放引擎已停止";
                if (!IsDisposed && !Disposing)
                    ShowError("播放引擎异常退出", message);
            });
        }

        private void ShowEngineMissing()
        {
            ShowError(
                "缺少播放引擎",
                "没有找到 mpv.exe。请运行项目根目录的 build.ps1；它会下载官方推荐的 Windows 构建并生成可直接运行的发布目录。"
            );
        }

        private void ShowError(string title, string details)
        {
            MessageBox.Show(this, details, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void UpdatePauseButton()
        {
            _playButton.Text = _isPaused ? "播放" : "暂停";
            _toolTip.SetToolTip(_playButton, _isPaused ? "播放" : "暂停");
        }

        private void UpdateTimeLabels(double current, double total)
        {
            current = Math.Max(0, current);
            total = Math.Max(0, total);
            double remaining = Math.Max(0, total - current);
            SetTextIfChanged(_elapsedLabel, "已播放 " + FormatTime(current));
            SetTextIfChanged(_remainingLabel, "剩余 " + FormatTime(remaining));
            SetTextIfChanged(_totalLabel, "总时长 " + FormatTime(total));
        }

        private static void SetTextIfChanged(Control control, string text)
        {
            if (control.Text != text)
                control.Text = text;
        }

        private static string FormatTime(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                seconds = 0;
            TimeSpan value = TimeSpan.FromSeconds(seconds);
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + value.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + value.Seconds.ToString("00", CultureInfo.InvariantCulture);
            return value.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + value.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void CenterEmptyState()
        {
            if (_emptyState == null || _videoSurface == null)
                return;
            _emptyState.Left = Math.Max(0, (_videoSurface.ClientSize.Width - _emptyState.Width) / 2);
            _emptyState.Top = Math.Max(0, (_videoSurface.ClientSize.Height - _emptyState.Height) / 2);
        }

        private void SafeUi(MethodInvoker action)
        {
            if (action == null || IsDisposed || Disposing || !IsHandleCreated)
                return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch (InvalidOperationException) { }
            }
            else
                action();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _fullscreenMouseTimer.Stop();
            UserSettings.SaveVolume(_volumeBar.Value);
            UserSettings.SaveInt("SubtitlePosition", _subPos);
            UserSettings.SaveInt("SubtitleScale", _subScale);
            if (_cursorHidden)
            {
                Cursor.Show();
                _cursorHidden = false;
            }
            if (_mpv != null)
            {
                SaveResumePosition();
                DetachMpv(_mpv);
                _mpv.Dispose();
                _mpv = null;
            }
        }

        internal static double ToDouble(object value)
        {
            if (value == null)
                return 0;
            double result;
            if (Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;
            return 0;
        }

        internal static bool ToBool(object value)
        {
            if (value is bool)
                return (bool)value;
            bool result;
            if (Boolean.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result))
                return result;
            return ToDouble(value) != 0;
        }
    }

    internal static class UserSettings
    {
        private const string KeyPath = "Software\\LumaPlayer\\Settings";

        public static int LoadVolume(int fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key == null ? null : key.GetValue("Volume");
                    if (value is int)
                        return Math.Max(0, Math.Min(100, (int)value));
                }
            }
            catch (Exception) { }
            return fallback;
        }

        public static int LoadInt(string name, int fallback, int minimum, int maximum)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key == null ? null : key.GetValue(name);
                    if (value is int)
                        return Math.Max(minimum, Math.Min(maximum, (int)value));
                }
            }
            catch (Exception) { }
            return fallback;
        }

        public static void SaveInt(string name, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }

        public static bool LoadSingleWindow()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key == null ? null : key.GetValue("SingleWindow");
                    if (value is int)
                        return (int)value != 0;
                }
            }
            catch (Exception) { }
            return true;
        }

        public static void SaveSingleWindow(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue("SingleWindow", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }

        public static void SaveVolume(int volume)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue("Volume", volume, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }
    }

    internal sealed class MpvProcess : IDisposable
    {
        private readonly string _executable;
        private readonly uint _windowId;
        private readonly int _initialVolume;
        private readonly int _subPos;
        private readonly int _subScale;
        private readonly string _pipeName;
        private readonly JavaScriptSerializer _json;
        private readonly object _writeLock;
        private readonly object _callbackLock;
        private readonly Dictionary<int, Action<object>> _callbacks;

        private Process _process;
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _readerThread;
        private int _nextRequestId;
        private int _failureRaised;
        private bool _disposing;

        public event Action<string, object> PropertyChanged;
        public event Action<string> EventReceived;
        public event Action<string> ClientMessageReceived;
        public event Action<string> EngineFailed;

        public bool IsRunning
        {
            get { return _process != null && !_process.HasExited && _pipe != null && _pipe.IsConnected; }
        }

        public MpvProcess(string executable, uint windowId, int initialVolume, int subPos, int subScale)
        {
            _initialVolume = initialVolume;
            _subPos = subPos;
            _subScale = subScale;
            _executable = executable;
            _windowId = windowId;
            _pipeName = "LumaPlayer-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 1024 * 1024 * 8;
            _writeLock = new object();
            _callbackLock = new object();
            _callbacks = new Dictionary<int, Action<object>>();
        }

        public void Start()
        {
            string pipePath = "\\\\.\\pipe\\" + _pipeName;
            string inputConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "input.conf");
            string watchLaterDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaPlayer", "watch_later");
            try { Directory.CreateDirectory(watchLaterDir); } catch { }
            string arguments = String.Join(" ", new string[]
            {
                "--wid=" + _windowId.ToString(CultureInfo.InvariantCulture),
                "--idle=yes",
                "--keep-open=yes",
                "--force-window=yes",
                "--no-terminal",
                "--config=no",
                "--load-scripts=no",
                "--input-default-bindings=no",
                "--input-vo-keyboard=yes",
                "--input-conf=\"" + inputConfig + "\"",
                "--osc=no",
                "--input-ipc-server=\"" + pipePath + "\"",
                "--vo=gpu-next",
                "--gpu-api=d3d11",
                "--gpu-context=d3d11",
                "--hwdec=auto-safe",
                "--target-colorspace-hint=auto",
                "--target-colorspace-hint-mode=target",
                "--video-sync=display-resample",
                "--audio-pitch-correction=yes",
                "--sub-auto=fuzzy",
                "--sub-file-paths=\"sub;subs;subtitles;Subs;Subtitles;字幕\"",
                // Put subtitles into black bars when the picture has them (e.g. a
                // wide film in full screen), for plain-text and ASS subtitles alike.
                "--sub-use-margins=yes",
                "--sub-ass-force-margins=yes",
                "--sub-pos=" + _subPos.ToString(CultureInfo.InvariantCulture),
                "--sub-scale=" + (_subScale / 100.0).ToString("0.##", CultureInfo.InvariantCulture),
                "--audio-file-auto=fuzzy",
                "--osd-level=0",
                "--volume=" + _initialVolume.ToString(CultureInfo.InvariantCulture),
                // Resume where the user stopped. The host writes the resume point
                // explicitly when switching files or closing; only the position and
                // track choices are restored, not volume, speed or pause.
                "--resume-playback=yes",
                "--watch-later-dir=\"" + watchLaterDir + "\"",
                "--watch-later-options=aid,sid,sub-delay,audio-delay"
            });

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = _executable;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = Path.GetDirectoryName(_executable);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            _process = new Process();
            _process.StartInfo = startInfo;
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            if (!_process.Start())
                throw new InvalidOperationException("无法启动 mpv.exe");

            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            Exception lastError = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException("mpv 在建立控制连接前退出，退出代码：" + _process.ExitCode.ToString(CultureInfo.InvariantCulture));
                try
                {
                    _pipe.Connect(100);
                    lastError = null;
                    break;
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                }
            }
            if (!_pipe.IsConnected)
                throw new InvalidOperationException("无法连接播放引擎。", lastError);

            _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 8192, true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 8192, true);
            _writer.NewLine = "\n";
            _writer.AutoFlush = true;

            _readerThread = new Thread(ReadLoop);
            _readerThread.IsBackground = true;
            _readerThread.Name = "LumaPlayer mpv IPC";
            _readerThread.Start();
        }

        public void Observe(string property)
        {
            Send(new Dictionary<string, object>
            {
                { "command", new object[] { "observe_property", NextRequestId(), property } }
            });
        }

        public void Command(params object[] command)
        {
            Send(new Dictionary<string, object> { { "command", command } });
        }

        public void SetProperty(string name, object value)
        {
            Command("set_property", name, value);
        }

        public void GetProperty(string name, Action<object> callback)
        {
            int requestId = NextRequestId();
            lock (_callbackLock)
                _callbacks[requestId] = callback;
            Send(new Dictionary<string, object>
            {
                { "command", new object[] { "get_property", name } },
                { "request_id", requestId }
            });
        }

        private int NextRequestId()
        {
            return Interlocked.Increment(ref _nextRequestId);
        }

        private void Send(IDictionary<string, object> message)
        {
            if (_disposing || _writer == null)
                return;
            string line = _json.Serialize(message);
            try
            {
                lock (_writeLock)
                {
                    if (!_disposing && _writer != null)
                        _writer.WriteLine(line);
                }
            }
            catch (IOException ex) { if (!_disposing) RaiseFailed(ex.Message); }
            catch (ObjectDisposedException ex) { if (!_disposing) RaiseFailed(ex.Message); }
            catch (InvalidOperationException ex) { if (!_disposing) RaiseFailed(ex.Message); }
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while (!_disposing && _reader != null && (line = _reader.ReadLine()) != null)
                {
                    object parsed = _json.DeserializeObject(line);
                    IDictionary<string, object> message = parsed as IDictionary<string, object>;
                    if (message == null)
                        continue;
                    try
                    {
                        HandleMessage(message);
                    }
                    catch (Exception ex)
                    {
                        // A failing UI handler must not be reported as an engine crash.
                        Trace.WriteLine("Luma Player: " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposing)
                    RaiseFailed(ex.Message);
            }
        }

        private void HandleMessage(IDictionary<string, object> message)
        {
            object requestValue;
            if (message.TryGetValue("request_id", out requestValue))
            {
                int requestId = (int)PlayerForm.ToDouble(requestValue);
                Action<object> callback = null;
                lock (_callbackLock)
                {
                    if (_callbacks.TryGetValue(requestId, out callback))
                        _callbacks.Remove(requestId);
                }
                if (callback != null)
                {
                    object data;
                    message.TryGetValue("data", out data);
                    callback(data);
                }
            }

            object eventValue;
            if (!message.TryGetValue("event", out eventValue) || eventValue == null)
                return;
            string eventName = Convert.ToString(eventValue, CultureInfo.InvariantCulture);

            if (eventName == "property-change")
            {
                object nameValue;
                object dataValue;
                if (message.TryGetValue("name", out nameValue))
                {
                    message.TryGetValue("data", out dataValue);
                    Action<string, object> handler = PropertyChanged;
                    if (handler != null)
                        handler(Convert.ToString(nameValue, CultureInfo.InvariantCulture), dataValue);
                }
            }

            if (eventName == "client-message")
            {
                object argsValue;
                object[] args = message.TryGetValue("args", out argsValue) ? argsValue as object[] : null;
                if (args == null)
                {
                    ArrayList list = argsValue as ArrayList;
                    if (list != null)
                        args = list.ToArray();
                }
                if (args != null && args.Length > 0 && args[0] != null)
                {
                    Action<string> clientHandler = ClientMessageReceived;
                    if (clientHandler != null)
                        clientHandler(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                }
            }

            Action<string> eventHandler = EventReceived;
            if (eventHandler != null)
                eventHandler(eventName);
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            if (!_disposing)
            {
                int exitCode = -1;
                try { if (_process != null) exitCode = _process.ExitCode; } catch { }
                RaiseFailed("mpv 已退出，代码：" + exitCode.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void RaiseFailed(string message)
        {
            if (Interlocked.Exchange(ref _failureRaised, 1) != 0)
                return;
            Action<string> handler = EngineFailed;
            if (handler != null)
                handler(message);
        }

        public void Dispose()
        {
            if (_disposing)
                return;
            _disposing = true;

            try
            {
                if (_writer != null && _pipe != null && _pipe.IsConnected)
                {
                    lock (_writeLock)
                    {
                        string quit = _json.Serialize(new Dictionary<string, object>
                        {
                            { "command", new object[] { "quit" } }
                        });
                        _writer.WriteLine(quit);
                    }
                }
            }
            catch { }

            try
            {
                if (_process != null && !_process.HasExited && !_process.WaitForExit(900))
                {
                    try
                    {
                        if (!_process.HasExited)
                            _process.Kill();
                    }
                    catch { }
                }
            }
            catch { }

            try { if (_reader != null) _reader.Dispose(); } catch { }
            try { if (_writer != null) _writer.Dispose(); } catch { }
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            try
            {
                if (_readerThread != null && _readerThread.IsAlive && Thread.CurrentThread != _readerThread)
                    _readerThread.Join(300);
            }
            catch { }
            try { if (_process != null) _process.Dispose(); } catch { }
        }
    }

    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
    {
        public BufferedTableLayoutPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public BufferedFlowLayoutPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    internal sealed class LumaMark : Control
    {
        public LumaMark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = LumaPalette.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Parent == null ? BackColor : Parent.BackColor);

            int size = Math.Min(52, Math.Max(1, Math.Min(Width - 4, Height - 6)));
            Rectangle tile = new Rectangle((Width - size) / 2, 3, size, size);
            using (GraphicsPath tilePath = CreateRoundedPath(tile, Math.Max(6, size / 5)))
            using (SolidBrush tileBrush = new SolidBrush(Color.FromArgb(31, 37, 48)))
            using (Pen tileOutline = new Pen(Color.FromArgb(70, 81, 98)))
            {
                graphics.FillPath(tileBrush, tilePath);
                graphics.DrawPath(tileOutline, tilePath);
            }

            int circleSize = Math.Max(1, size - 18);
            Rectangle circle = new Rectangle(tile.Left + (size - circleSize) / 2, tile.Top + (size - circleSize) / 2, circleSize, circleSize);
            using (SolidBrush accent = new SolidBrush(LumaPalette.Accent))
                graphics.FillEllipse(accent, circle);

            PointF[] triangle = new PointF[]
            {
                new PointF(circle.Left + circle.Width * 0.40F, circle.Top + circle.Height * 0.28F),
                new PointF(circle.Left + circle.Width * 0.40F, circle.Bottom - circle.Height * 0.28F),
                new PointF(circle.Right - circle.Width * 0.28F, circle.Top + circle.Height * 0.50F)
            };
            using (SolidBrush play = new SolidBrush(Color.FromArgb(255, 249, 244)))
                graphics.FillPolygon(play, triangle);
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rectangle, int radius)
        {
            int diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SkeuomorphicButton : Button
    {
        private bool _hovered;
        private bool _pressed;
        private bool _isPrimary;

        public bool IsPrimary
        {
            get { return _isPrimary; }
            set { _isPrimary = value; Invalidate(); }
        }

        public SkeuomorphicButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics graphics = pevent.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(BackColor);

            int offset = _pressed ? 1 : 0;
            Rectangle faceRect = new Rectangle(1, 1 + offset, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            Color fill;
            Color border;
            Color text;

            if (!Enabled)
            {
                fill = LumaPalette.Disabled;
                border = Color.FromArgb(47, 54, 65);
                text = Color.FromArgb(111, 120, 133);
            }
            else if (_isPrimary)
            {
                fill = _hovered ? LumaPalette.AccentHover : LumaPalette.Accent;
                border = Color.FromArgb(255, 151, 119);
                text = Color.FromArgb(38, 18, 14);
            }
            else
            {
                fill = _hovered ? LumaPalette.PanelRaised : LumaPalette.Panel;
                border = LumaPalette.Border;
                text = ForeColor;
            }

            using (GraphicsPath facePath = CreateRoundedPath(faceRect, 8))
            using (SolidBrush face = new SolidBrush(fill))
            using (Pen outline = new Pen(border))
            {
                graphics.FillPath(face, facePath);
                graphics.DrawPath(outline, facePath);
            }

            if (!_pressed)
            {
                int highlightY = faceRect.Top + 2;
                using (Pen highlight = new Pen(Color.FromArgb(_isPrimary ? 110 : 35, 255, 255, 255)))
                    graphics.DrawLine(highlight, faceRect.Left + 8, highlightY, faceRect.Right - 8, highlightY);
            }

            Rectangle textRect = new Rectangle(faceRect.Left + 3, faceRect.Top + (_pressed ? 2 : 0), faceRect.Width - 6, faceRect.Height);
            TextRenderer.DrawText(graphics, Text, Font, textRect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (Focused && ShowFocusCues)
            {
                Rectangle focusRect = Rectangle.Inflate(faceRect, -4, -4);
                ControlPaint.DrawFocusRectangle(graphics, focusRect, text, Color.Transparent);
            }
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class VolumeSlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; Value = _value; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = Math.Max(value, _minimum + 1); Value = _value; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int next = Math.Max(_minimum, Math.Min(_maximum, value));
                if (_value == next)
                    return;
                _value = next;
                Invalidate();
                EventHandler changed = ValueChanged;
                if (changed != null)
                    changed(this, EventArgs.Empty);
            }
        }

        public VolumeSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);
            int left = 7;
            int right = Math.Max(left + 1, Width - 8);
            int centerY = Height / 2;
            Rectangle groove = new Rectangle(left, centerY - 3, Math.Max(1, right - left), 6);
            using (GraphicsPath path = RoundedPath(groove, 3))
            using (LinearGradientBrush brush = new LinearGradientBrush(groove, Color.FromArgb(9, 11, 14), Color.FromArgb(54, 59, 67), LinearGradientMode.Vertical))
            using (Pen edge = new Pen(Color.FromArgb(12, 14, 17)))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(edge, path);
            }

            double ratio = (_value - _minimum) / (double)Math.Max(1, _maximum - _minimum);
            int knobX = left + (int)Math.Round((right - left) * ratio);
            if (Enabled && knobX > left)
            {
                Rectangle fill = new Rectangle(left, centerY - 2, knobX - left, 4);
                using (GraphicsPath path = RoundedPath(fill, 2))
                using (LinearGradientBrush accent = new LinearGradientBrush(fill, Color.FromArgb(135, 220, 255), Color.FromArgb(45, 143, 204), LinearGradientMode.Vertical))
                    e.Graphics.FillPath(accent, path);
            }

            Rectangle shadow = new Rectangle(knobX - 7, centerY - 5, 14, 14);
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                e.Graphics.FillEllipse(brush, shadow);
            Rectangle knob = new Rectangle(knobX - 7, centerY - 7, 14, 14);
            Color knobTop = Enabled ? Color.FromArgb(246, 248, 250) : Color.FromArgb(110, 114, 120);
            Color knobBottom = Enabled ? Color.FromArgb(125, 132, 141) : Color.FromArgb(68, 72, 78);
            using (LinearGradientBrush brush = new LinearGradientBrush(knob, knobTop, knobBottom, LinearGradientMode.Vertical))
            using (Pen edge = new Pen(Color.FromArgb(30, 33, 38)))
            {
                e.Graphics.FillEllipse(brush, knob);
                e.Graphics.DrawEllipse(edge, knob);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left)
                return;
            _dragging = true;
            Capture = true;
            Focus();
            SetFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
                SetFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging)
                return;
            _dragging = false;
            Capture = false;
            SetFromMouse(e.X);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down)
            {
                Value -= 5;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up)
            {
                Value += 5;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private void SetFromMouse(int x)
        {
            int left = 7;
            int width = Math.Max(1, ClientSize.Width - 15);
            double ratio = Math.Max(0, Math.Min(1, (x - left) / (double)width));
            Value = _minimum + (int)Math.Round((_maximum - _minimum) * ratio);
        }

        private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (rectangle.Width <= diameter || rectangle.Height <= diameter)
            {
                path.AddRectangle(rectangle);
                return path;
            }
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SeekBar : Control
    {
        private double _value;
        private bool _dragging;

        public event EventHandler ValueChanged;
        public event EventHandler SeekStarted;
        public event EventHandler SeekEnded;
        public event EventHandler PreviewChanged;

        public double Value
        {
            get { return _value; }
            set
            {
                double next = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(_value - next) < 0.0001)
                    return;
                int before = FilledWidth(_value);
                _value = next;
                // Repaint only when the thumb actually moves by a pixel.
                if (FilledWidth(next) != before)
                    Invalidate();
            }
        }

        private int FilledWidth(double value)
        {
            int usableWidth = Math.Max(1, ClientSize.Width - 7 * 2);
            return (int)Math.Round(usableWidth * value);
        }

        public SeekBar()
        {
            DoubleBuffered = true;
            Height = 30;
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(19, 22, 27);
            SetStyle(ControlStyles.Selectable, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int margin = 7;
            int usableWidth = Math.Max(1, ClientSize.Width - margin * 2);
            int centerY = ClientSize.Height / 2;
            Rectangle track = new Rectangle(margin, centerY - 2, usableWidth, 4);
            using (GraphicsPath path = RoundedRect(track, 2))
            using (SolidBrush back = new SolidBrush(Enabled ? Color.FromArgb(66, 73, 84) : Color.FromArgb(43, 47, 54)))
                e.Graphics.FillPath(back, path);

            int filledWidth = (int)Math.Round(usableWidth * _value);
            if (filledWidth > 0)
            {
                Rectangle filled = new Rectangle(margin, centerY - 2, filledWidth, 4);
                using (GraphicsPath path = RoundedRect(filled, 2))
                using (SolidBrush accent = new SolidBrush(Color.FromArgb(91, 190, 255)))
                    e.Graphics.FillPath(accent, path);
            }

            int thumbX = margin + filledWidth;
            using (SolidBrush thumb = new SolidBrush(Enabled ? Color.White : Color.FromArgb(110, 116, 124)))
                e.Graphics.FillEllipse(thumb, thumbX - 6, centerY - 6, 12, 12);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left)
                return;
            _dragging = true;
            Capture = true;
            EventHandler started = SeekStarted;
            if (started != null)
                started(this, EventArgs.Empty);
            SetFromMouse(e.X, false);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
                SetFromMouse(e.X, false);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging)
                return;
            SetFromMouse(e.X, true);
            _dragging = false;
            Capture = false;
            EventHandler ended = SeekEnded;
            if (ended != null)
                ended(this, EventArgs.Empty);
        }

        private void SetFromMouse(int x, bool notify)
        {
            int margin = 7;
            int usableWidth = Math.Max(1, ClientSize.Width - margin * 2);
            Value = (x - margin) / (double)usableWidth;
            EventHandler handler = notify ? ValueChanged : PreviewChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private static GraphicsPath RoundedRect(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (diameter <= 0)
            {
                path.AddRectangle(rectangle);
                return path;
            }
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(19, 22, 27); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(19, 22, 27); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(19, 22, 27); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(19, 22, 27); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(42, 48, 57); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(69, 78, 91); } }
        public override Color MenuBorder { get { return Color.FromArgb(48, 54, 64); } }
        public override Color SeparatorDark { get { return Color.FromArgb(48, 54, 64); } }
        public override Color SeparatorLight { get { return Color.FromArgb(48, 54, 64); } }
    }
}
