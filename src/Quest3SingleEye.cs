using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Quest 3 单目投屏")]
[assembly: AssemblyDescription("USB-only single-eye viewer for Meta Quest 3, powered by scrcpy")]
[assembly: AssemblyCompany("Quest3SingleEye")]
[assembly: AssemblyProduct("Quest 3 单目投屏")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Quest3SingleEye
{
    internal enum Eye
    {
        Left,
        Right
    }

    internal sealed class MirrorSettings
    {
        public Eye Eye = Eye.Left;
        public decimal LeftAngle = 10M;
        public decimal RightAngle = -10M;

        private static string SettingsPath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Quest3SingleEye");
                return Path.Combine(folder, "settings.ini");
            }
        }

        public static MirrorSettings Load()
        {
            MirrorSettings settings = new MirrorSettings();
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return settings;
                }

                foreach (string sourceLine in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    string line = sourceLine.Trim();
                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    decimal angle;

                    if (key.Equals("Eye", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.Eye = value.Equals("Right", StringComparison.OrdinalIgnoreCase)
                            ? Eye.Right
                            : Eye.Left;
                    }
                    else if (key.Equals("LeftAngle", StringComparison.OrdinalIgnoreCase)
                        && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out angle))
                    {
                        settings.LeftAngle = ClampAngle(angle);
                    }
                    else if (key.Equals("RightAngle", StringComparison.OrdinalIgnoreCase)
                        && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out angle))
                    {
                        settings.RightAngle = ClampAngle(angle);
                    }
                }
            }
            catch
            {
                // A damaged settings file should never prevent the app from starting.
            }

            return settings;
        }

        public void Save()
        {
            string path = SettingsPath;
            string folder = Path.GetDirectoryName(path);
            Directory.CreateDirectory(folder);

            string[] lines =
            {
                "Eye=" + Eye,
                "LeftAngle=" + LeftAngle.ToString(CultureInfo.InvariantCulture),
                "RightAngle=" + RightAngle.ToString(CultureInfo.InvariantCulture)
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        public decimal CurrentAngle
        {
            get { return Eye == Eye.Left ? LeftAngle : RightAngle; }
            set
            {
                if (Eye == Eye.Left)
                {
                    LeftAngle = ClampAngle(value);
                }
                else
                {
                    RightAngle = ClampAngle(value);
                }
            }
        }

        private static decimal ClampAngle(decimal value)
        {
            return Math.Max(-30M, Math.Min(30M, value));
        }
    }

    internal sealed class DeviceInfo
    {
        public string Model;
        public int Width;
        public int Height;
        public int CropWidth;
        public int CropHeight;
        public int CropX;
    }

    internal sealed class CommandResult
    {
        public int ExitCode;
        public bool TimedOut;
        public string StandardOutput;
        public string StandardError;

        public string Combined
        {
            get { return (StandardOutput + Environment.NewLine + StandardError).Trim(); }
        }
    }

    internal sealed class MainForm : Form
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_F11 = 0x7A;

        private readonly string _appDirectory;
        private readonly string _runtimeDirectory;
        private readonly string _adbPath;
        private readonly string _scrcpyPath;
        private readonly MirrorSettings _settings;
        private readonly object _processSync = new object();
        private readonly List<string> _recentLog = new List<string>();

        private Process _mirrorProcess;
        private bool _busy;
        private bool _intentionalStop;
        private bool _loadingControls;

        private RadioButton _leftEyeButton;
        private RadioButton _rightEyeButton;
        private NumericUpDown _angleInput;
        private Button _applyAngleButton;
        private Button _startButton;
        private Button _fullscreenButton;
        private Label _statusLabel;
        private Label _deviceLabel;
        private TextBox _logBox;

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        public MainForm()
        {
            _appDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            _runtimeDirectory = Path.Combine(_appDirectory, "scrcpy");
            _adbPath = Path.Combine(_runtimeDirectory, "adb.exe");
            _scrcpyPath = Path.Combine(_runtimeDirectory, "scrcpy.exe");
            _settings = MirrorSettings.Load();

            InitializeWindow();
            InitializeControls();
            ApplySettingsToControls();
        }

        private void InitializeWindow()
        {
            Text = "Quest 3 单目投屏";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 455);
            MinimumSize = new Size(576, 494);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            FormClosing += OnFormClosing;

            string iconPath = Path.Combine(_appDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                try { Icon = new Icon(iconPath); }
                catch { }
            }
        }

        private void InitializeControls()
        {
            Label title = new Label();
            title.Text = "Quest 3 单目投屏";
            title.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.FromArgb(25, 32, 45);
            title.AutoSize = true;
            title.Location = new Point(30, 24);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "USB 有线连接 · 单目裁切 · 旋转校正";
            subtitle.ForeColor = Color.FromArgb(100, 109, 124);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(32, 64);
            Controls.Add(subtitle);

            GroupBox eyeGroup = new GroupBox();
            eyeGroup.Text = "显示眼睛";
            eyeGroup.Location = new Point(30, 96);
            eyeGroup.Size = new Size(240, 84);
            Controls.Add(eyeGroup);

            _leftEyeButton = CreateEyeButton("左眼", new Point(16, 28));
            _rightEyeButton = CreateEyeButton("右眼", new Point(124, 28));
            _leftEyeButton.CheckedChanged += OnEyeChanged;
            _rightEyeButton.CheckedChanged += OnEyeChanged;
            eyeGroup.Controls.Add(_leftEyeButton);
            eyeGroup.Controls.Add(_rightEyeButton);

            GroupBox angleGroup = new GroupBox();
            angleGroup.Text = "画面旋转";
            angleGroup.Location = new Point(290, 96);
            angleGroup.Size = new Size(240, 84);
            Controls.Add(angleGroup);

            _angleInput = new NumericUpDown();
            _angleInput.Minimum = -30M;
            _angleInput.Maximum = 30M;
            _angleInput.DecimalPlaces = 1;
            _angleInput.Increment = 0.5M;
            _angleInput.TextAlign = HorizontalAlignment.Center;
            _angleInput.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            _angleInput.Location = new Point(15, 30);
            _angleInput.Size = new Size(104, 29);
            _angleInput.ValueChanged += OnAngleChanged;
            angleGroup.Controls.Add(_angleInput);

            Label degreeLabel = new Label();
            degreeLabel.Text = "° 顺时针";
            degreeLabel.AutoSize = true;
            degreeLabel.Location = new Point(122, 37);
            angleGroup.Controls.Add(degreeLabel);

            _applyAngleButton = new Button();
            _applyAngleButton.Text = "应用";
            _applyAngleButton.Location = new Point(169, 28);
            _applyAngleButton.Size = new Size(58, 32);
            _applyAngleButton.FlatStyle = FlatStyle.Flat;
            _applyAngleButton.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 221);
            _applyAngleButton.Click += OnApplyAngle;
            angleGroup.Controls.Add(_applyAngleButton);

            _startButton = new Button();
            _startButton.Text = "开始投屏";
            _startButton.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
            _startButton.ForeColor = Color.White;
            _startButton.BackColor = Color.FromArgb(30, 111, 235);
            _startButton.FlatStyle = FlatStyle.Flat;
            _startButton.FlatAppearance.BorderSize = 0;
            _startButton.Location = new Point(30, 200);
            _startButton.Size = new Size(330, 58);
            _startButton.Click += OnStartStop;
            Controls.Add(_startButton);

            _fullscreenButton = new Button();
            _fullscreenButton.Text = "全屏 / 窗口\nF11";
            _fullscreenButton.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
            _fullscreenButton.ForeColor = Color.FromArgb(30, 88, 170);
            _fullscreenButton.BackColor = Color.White;
            _fullscreenButton.FlatStyle = FlatStyle.Flat;
            _fullscreenButton.FlatAppearance.BorderColor = Color.FromArgb(155, 181, 221);
            _fullscreenButton.Location = new Point(375, 200);
            _fullscreenButton.Size = new Size(155, 58);
            _fullscreenButton.Enabled = false;
            _fullscreenButton.Click += OnFullscreen;
            Controls.Add(_fullscreenButton);

            _statusLabel = new Label();
            _statusLabel.Text = "准备就绪";
            _statusLabel.ForeColor = Color.FromArgb(59, 68, 82);
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Location = new Point(31, 280);
            _statusLabel.Size = new Size(499, 22);
            Controls.Add(_statusLabel);

            _deviceLabel = new Label();
            _deviceLabel.Text = "请连接 Quest 3，并在头显中允许 USB 调试";
            _deviceLabel.ForeColor = Color.FromArgb(112, 121, 134);
            _deviceLabel.AutoEllipsis = true;
            _deviceLabel.Location = new Point(31, 304);
            _deviceLabel.Size = new Size(499, 22);
            Controls.Add(_deviceLabel);

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Color.FromArgb(32, 37, 46);
            _logBox.ForeColor = Color.FromArgb(215, 222, 232);
            _logBox.BorderStyle = BorderStyle.FixedSingle;
            _logBox.Font = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _logBox.Location = new Point(30, 338);
            _logBox.Size = new Size(500, 88);
            Controls.Add(_logBox);
        }

        private RadioButton CreateEyeButton(string text, Point location)
        {
            RadioButton button = new RadioButton();
            button.Appearance = Appearance.Button;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Text = text;
            button.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(187, 196, 210);
            button.FlatAppearance.CheckedBackColor = Color.FromArgb(220, 234, 255);
            button.Location = location;
            button.Size = new Size(98, 36);
            return button;
        }

        private void ApplySettingsToControls()
        {
            _loadingControls = true;
            _leftEyeButton.Checked = _settings.Eye == Eye.Left;
            _rightEyeButton.Checked = _settings.Eye == Eye.Right;
            _angleInput.Value = _settings.CurrentAngle;
            _loadingControls = false;
        }

        private async void OnStartStop(object sender, EventArgs e)
        {
            if (IsMirroring())
            {
                StopProjection();
                SetStatus("投屏已停止", false);
                return;
            }

            await StartProjectionAsync(false);
        }

        private async void OnEyeChanged(object sender, EventArgs e)
        {
            if (_loadingControls)
            {
                return;
            }

            Eye selected = _rightEyeButton.Checked ? Eye.Right : Eye.Left;
            if (_settings.Eye == selected)
            {
                return;
            }

            _settings.Eye = selected;
            _loadingControls = true;
            _angleInput.Value = _settings.CurrentAngle;
            _loadingControls = false;
            SaveSettingsQuietly();

            if (IsMirroring())
            {
                await RestartProjectionAsync("正在切换到" + EyeText(selected) + "…");
            }
        }

        private void OnAngleChanged(object sender, EventArgs e)
        {
            if (_loadingControls)
            {
                return;
            }

            _settings.CurrentAngle = _angleInput.Value;
            SaveSettingsQuietly();
            if (IsMirroring())
            {
                SetStatus("旋转角度已修改，点击“应用”更新投屏", false);
            }
        }

        private async void OnApplyAngle(object sender, EventArgs e)
        {
            if (IsMirroring())
            {
                await RestartProjectionAsync("正在应用旋转角度…");
            }
            else
            {
                SetStatus("旋转角度已保存，下次投屏时生效", false);
            }
        }

        private void OnFullscreen(object sender, EventArgs e)
        {
            Process process = GetMirrorProcess();
            if (process == null)
            {
                SetStatus("请先开始投屏", true);
                return;
            }

            try
            {
                process.Refresh();
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero)
                {
                    SetStatus("未找到投屏窗口，请直接在投屏窗口按 F11", true);
                    return;
                }

                PostMessage(window, WM_KEYDOWN, new IntPtr(VK_F11), IntPtr.Zero);
                PostMessage(window, WM_KEYUP, new IntPtr(VK_F11), IntPtr.Zero);
                SetStatus("已切换窗口 / 全屏模式", false);
            }
            catch
            {
                SetStatus("切换失败，请直接在投屏窗口按 F11", true);
            }
        }

        private async Task RestartProjectionAsync(string status)
        {
            if (_busy)
            {
                return;
            }

            SetStatus(status, false);
            StopProjection();
            await Task.Delay(450);
            await StartProjectionAsync(true);
        }

        private async Task StartProjectionAsync(bool restarting)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            UpdateButtons();
            SetStatus(restarting ? "正在重新连接视频流…" : "正在检查 USB 连接…", false);

            try
            {
                DeviceInfo device = await Task.Run(new Func<DeviceInfo>(DetectDevice));
                _deviceLabel.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} · 原始画面 {1}×{2} · {3}",
                    device.Model,
                    device.Width,
                    device.Height,
                    EyeText(_settings.Eye));

                LaunchScrcpy(device);
                SetStatus("投屏已启动：" + EyeText(_settings.Eye), false);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR: " + ex.Message);
                SetStatus(ex.Message, true);
                MessageBox.Show(this, ex.Message, "无法开始投屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private DeviceInfo DetectDevice()
        {
            EnsureRuntimeAvailable();

            CommandResult state = RunCommand(_adbPath, "-d get-state", 7000);
            string stateText = state.Combined.ToLowerInvariant();
            if (state.TimedOut)
            {
                throw new InvalidOperationException("连接 Quest 3 超时，请重新插拔 USB 数据线");
            }

            if (state.ExitCode != 0 || !state.StandardOutput.Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                if (stateText.Contains("unauthorized"))
                {
                    throw new InvalidOperationException("Quest 3 尚未授权，请在头显中勾选“始终允许”并确认 USB 调试");
                }
                if (stateText.Contains("more than one"))
                {
                    throw new InvalidOperationException("检测到多个 USB Android 设备，请只保留 Quest 3");
                }
                throw new InvalidOperationException("未检测到 Quest 3，请确认使用支持数据传输的 USB 线并已开启 USB 调试");
            }

            CommandResult modelResult = RunCommand(_adbPath, "-d shell getprop ro.product.model", 5000);
            string model = modelResult.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                model = "Quest 3";
            }

            CommandResult sizeResult = RunCommand(_adbPath, "-d shell wm size", 7000);
            MatchCollection matches = Regex.Matches(sizeResult.Combined, @"(?<w>\d{3,5})x(?<h>\d{3,5})");
            int width = 4128;
            int height = 2208;
            if (matches.Count > 0)
            {
                Match match = matches[matches.Count - 1];
                width = int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture);
                height = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            }

            if (width < height)
            {
                int swap = width;
                width = height;
                height = swap;
            }
            if (width < 2000 || height < 1000)
            {
                width = 4128;
                height = 2208;
            }

            int half = width / 2;
            int cropWidth = AlignDown(half, 8);
            int cropHeight = AlignDown(height, 8);
            int cropX = _settings.Eye == Eye.Right ? AlignDown(half, 8) : 0;

            DeviceInfo info = new DeviceInfo();
            info.Model = model;
            info.Width = width;
            info.Height = height;
            info.CropWidth = cropWidth;
            info.CropHeight = cropHeight;
            info.CropX = cropX;
            return info;
        }

        private void EnsureRuntimeAvailable()
        {
            if (!File.Exists(_adbPath) || !File.Exists(_scrcpyPath))
            {
                throw new InvalidOperationException(
                    "缺少 scrcpy 运行组件，请确认程序目录中的 scrcpy 文件夹完整");
            }
        }

        private void LaunchScrcpy(DeviceInfo device)
        {
            decimal angle = _settings.CurrentAngle;
            string crop = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}:{2}:0",
                device.CropWidth,
                device.CropHeight,
                device.CropX);
            string title = "Quest 3 单目投屏 - " + EyeText(_settings.Eye);

            List<string> args = new List<string>();
            args.Add("--select-usb");
            args.Add("--no-audio");
            args.Add("--no-control");
            args.Add("--video-codec=h264");
            args.Add("--video-bit-rate=20M");
            args.Add("--max-fps=60");
            args.Add("--crop=" + crop);
            args.Add("--angle=" + angle.ToString("0.0", CultureInfo.InvariantCulture));
            args.Add("--window-title=" + QuoteArgument(title));

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = _scrcpyPath;
            startInfo.Arguments = string.Join(" ", args.ToArray());
            startInfo.WorkingDirectory = _runtimeDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += OnScrcpyOutput;
            process.ErrorDataReceived += OnScrcpyOutput;
            process.Exited += OnScrcpyExited;

            _intentionalStop = false;
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 scrcpy");
            }

            lock (_processSync)
            {
                _mirrorProcess = process;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendLog("START: scrcpy " + startInfo.Arguments);
            UpdateButtons();
        }

        private void OnScrcpyOutput(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AppendLog(e.Data);
            }
        }

        private void OnScrcpyExited(object sender, EventArgs e)
        {
            Process exited = sender as Process;
            bool intentional = _intentionalStop;
            int exitCode = -1;
            try { exitCode = exited.ExitCode; }
            catch { }

            lock (_processSync)
            {
                if (ReferenceEquals(_mirrorProcess, exited))
                {
                    _mirrorProcess = null;
                }
            }

            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(new Action(delegate
            {
                UpdateButtons();
                if (!intentional && !_busy)
                {
                    string message = exitCode == 0
                        ? "投屏窗口已关闭"
                        : "投屏意外结束，请检查下方日志";
                    SetStatus(message, exitCode != 0);
                }
            }));
        }

        private void StopProjection()
        {
            Process process = GetMirrorProcess();
            if (process == null)
            {
                UpdateButtons();
                return;
            }

            _intentionalStop = true;
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1200))
                    {
                        process.Kill();
                        process.WaitForExit(1200);
                    }
                }
            }
            catch { }

            lock (_processSync)
            {
                if (ReferenceEquals(_mirrorProcess, process))
                {
                    _mirrorProcess = null;
                }
            }
            UpdateButtons();
        }

        private Process GetMirrorProcess()
        {
            lock (_processSync)
            {
                if (_mirrorProcess == null)
                {
                    return null;
                }
                try
                {
                    if (_mirrorProcess.HasExited)
                    {
                        _mirrorProcess = null;
                        return null;
                    }
                }
                catch
                {
                    _mirrorProcess = null;
                    return null;
                }
                return _mirrorProcess;
            }
        }

        private bool IsMirroring()
        {
            return GetMirrorProcess() != null;
        }

        private void UpdateButtons()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateButtons));
                return;
            }

            bool running = IsMirroring();
            _startButton.Text = running ? "停止投屏" : (_busy ? "正在连接…" : "开始投屏");
            _startButton.BackColor = running
                ? Color.FromArgb(201, 54, 61)
                : Color.FromArgb(30, 111, 235);
            _startButton.Enabled = !_busy;
            _fullscreenButton.Enabled = running && !_busy;
            _leftEyeButton.Enabled = !_busy;
            _rightEyeButton.Enabled = !_busy;
            _applyAngleButton.Enabled = !_busy;
            _angleInput.Enabled = !_busy;
        }

        private void SetStatus(string message, bool error)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, bool>(SetStatus), message, error);
                return;
            }

            _statusLabel.Text = message;
            _statusLabel.ForeColor = error
                ? Color.FromArgb(194, 49, 57)
                : Color.FromArgb(59, 68, 82);
        }

        private void AppendLog(string line)
        {
            lock (_recentLog)
            {
                _recentLog.Add(line);
                if (_recentLog.Count > 200)
                {
                    _recentLog.RemoveRange(0, _recentLog.Count - 200);
                }
            }

            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLogToBox), line);
            }
            else
            {
                AppendLogToBox(line);
            }
        }

        private void AppendLogToBox(string line)
        {
            _logBox.AppendText(line + Environment.NewLine);
            if (_logBox.TextLength > 24000)
            {
                _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 18000);
                _logBox.SelectionStart = _logBox.TextLength;
            }
        }

        private static CommandResult RunCommand(string fileName, string arguments, int timeoutMs)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = Path.GetDirectoryName(fileName);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = Process.Start(startInfo))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { process.Kill(); }
                    catch { }
                }

                try { Task.WaitAll(new Task[] { stdout, stderr }, 1500); }
                catch { }

                CommandResult result = new CommandResult();
                result.TimedOut = !exited;
                result.ExitCode = exited ? process.ExitCode : -1;
                result.StandardOutput = stdout.IsCompleted ? stdout.Result : string.Empty;
                result.StandardError = stderr.IsCompleted ? stderr.Result : string.Empty;
                return result;
            }
        }

        private static int AlignDown(int value, int alignment)
        {
            return value - value % alignment;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string EyeText(Eye eye)
        {
            return eye == Eye.Left ? "左眼" : "右眼";
        }

        private void SaveSettingsQuietly()
        {
            try { _settings.Save(); }
            catch { }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettingsQuietly();
            StopProjection();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
