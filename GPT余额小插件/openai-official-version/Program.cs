using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Media;

namespace OpenAiUsageOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            File.WriteAllText(args[1], DiagnoseAsync().GetAwaiter().GetResult());
            return;
        }
        if (args.Length == 2 && args[0] == "--query-once")
        {
            try
            {
                var result = OpenAiUsageFetcher.QueryAsync().GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(result));
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { error = ex.Message }));
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--self-test")
        {
            try
            {
                OpenAiUsageFetcher.SelfTestAsync().GetAwaiter().GetResult();
                File.WriteAllText(args[1], "ok");
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.GetType().Name + ": " + ex.Message +
                    (ex is System.ComponentModel.Win32Exception native ? $" (Windows {native.NativeErrorCode})" : ""));
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--sound-check")
        {
            try
            {
                using var sound = new SoundPlayer(Path.Combine(AppContext.BaseDirectory, "click.wav"));
                sound.Load();
                sound.PlaySync();
                File.WriteAllText(args[1], "ok");
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], ex.GetType().Name + ": " + ex.Message);
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--preview")
        {
            using var form = new UsageForm();
            form.SavePreview(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--inspect-session")
        {
            using var form = new UsageForm();
            form.SaveSessionSnapshot(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--desktop-probe")
        {
            File.WriteAllText(args[1], DesktopIdentity.CurrentName());
            return;
        }
        using var singleInstance = new Mutex(true, DesktopIdentity.MutexName(), out bool firstInstance);
        if (!firstInstance) return;
        Application.Run(new UsageForm(args.Contains("--show-now", StringComparer.OrdinalIgnoreCase)));
    }

    private static async Task<string> DiagnoseAsync()
    {
        try { return JsonSerializer.Serialize(await OpenAiUsageFetcher.QueryAsync()); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
    }
}

internal static class DesktopIdentity
{
    private const int UoiName = 2;

    internal static string CurrentName()
    {
        IntPtr desktop = GetThreadDesktop(GetCurrentThreadId());
        var name = new StringBuilder(256);
        if (desktop == IntPtr.Zero ||
            !GetUserObjectInformation(desktop, UoiName, name, name.Capacity * sizeof(char), out _))
            return "unknown-" + Environment.ProcessId;
        return name.ToString();
    }

    internal static string MutexName()
    {
        string desktop = CurrentName().Replace('\\', '_').Replace('/', '_');
        return $@"Local\OpenAiUsageOverlay-{Process.GetCurrentProcess().SessionId}-{desktop}";
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
}

internal sealed class OverlaySettings
{
    public int BubbleSeconds { get; set; } = 8;
    public int MascotWidth { get; set; } = 146;
    public decimal? MonthlyBudgetUsd { get; set; }

    private static string SettingsPath
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable("OPENAI_OVERLAY_SETTINGS_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath))
                return overridePath;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAiUsageOverlay", "settings.json");
        }
    }

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var saved = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(SettingsPath));
                if (saved is not null)
                {
                    if (saved.BubbleSeconds is < 5 or > 10) saved.BubbleSeconds = 8;
                    if (saved.MascotWidth is < 80 or > 260) saved.MascotWidth = 146;
                    if (saved.MonthlyBudgetUsd is < 0 or > 1000000) saved.MonthlyBudgetUsd = null;
                    return saved;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new OverlaySettings();
    }

    public void Save()
    {
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}

internal sealed class UsageForm : Form
{
    private const int BubbleX = 12;
    private const int BubbleY = 12;
    private const int BubbleW = 304;
    private const int BubbleH = 208;
    private const int BubbleGap = 10;
    private const int BounceTravel = 14;
    private static readonly Color KeyColor = Color.FromArgb(255, 0, 255);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _bubbleTimer = new();
    private readonly System.Windows.Forms.Timer _bounceTimer = new() { Interval = 16 };
    private readonly Stopwatch _bounceWatch = new();
    private readonly Image _mascot;
    private readonly SoundPlayer _clickSound;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tooltip = new();
    private readonly string _codexRoot = ResolveCodexRoot();
    private readonly OverlaySettings _settings;
    private string _spend = "点我查询";
    private string _balance = "右键配置 Admin API Key 后查询";
    private string _accountBalance = "组织文本 Token —";
    private string _model = "模型 —";
    private string _session = "会话 token —";
    private string _details = "输入 —  ·  输出 —";
    private string _error = "";
    private bool _apiBusy;
    private bool _bubbleVisible;
    private bool _soundEnabled = true;
    private int _mascotWidth;
    private int _bounceOffset;
    private bool _dragging;
    private Point _dragOrigin;
    private Point _mascotDragOrigin;
    private Point _mascotScreenLocation;
    private Rectangle _mascotBounds;
    private Rectangle _bubbleBounds;
    private DateTime _manualPlacementUntil = DateTime.MinValue;
    private readonly DateTime _showOnLaunchUntil;
    private Rectangle _lastCodexRect;

    public UsageForm(bool showOnLaunch = false)
    {
        _showOnLaunchUntil = showOnLaunch ? DateTime.UtcNow.AddSeconds(30) : DateTime.MinValue;
        _settings = OverlaySettings.Load();
        _mascotWidth = _settings.MascotWidth;
        _mascot = Image.FromFile(Path.Combine(AppContext.BaseDirectory, "mascot.png"));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen;
        _mascotScreenLocation = new Point(screen.Right - _mascotWidth - 26, screen.Bottom - MascotHeight - 71);
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        DoubleBuffered = true;
        UpdateVisualLayout();
        _clickSound = new SoundPlayer(Path.Combine(AppContext.BaseDirectory, "click.wav"));
        _clickSound.Load();
        _bounceTimer.Tick += (_, _) =>
        {
            double progress = _bounceWatch.Elapsed.TotalMilliseconds / 320.0;
            if (progress >= 1)
            {
                _bounceTimer.Stop();
                _bounceOffset = 0;
            }
            else _bounceOffset = (int)Math.Round(BounceTravel * Math.Sin(Math.PI * progress));
            Invalidate();
        };
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            _bubbleVisible = false;
            UpdateVisualLayout();
        };
        _menu.Items.Add("打开 OpenAI 用量页面", null, (_, _) => Process.Start(new ProcessStartInfo("https://platform.openai.com/usage") { UseShellExecute = true }));
        _menu.Items.Add("查询一次", null, (_, _) => _ = QueryOnceAsync());
        _menu.Items.Add("配置 Admin Key 与月预算…", null, (_, _) => ShowAdminSettingsDialog());
        _menu.Items.Add("清除本机 Admin Key", null, (_, _) =>
        {
            try
            {
                AdminKeyStore.Clear();
                _balance = "本机 Admin Key 已清除";
            }
            catch (Exception ex) { _balance = "清除失败：" + ex.Message; }
            Invalidate();
        });
        var durationMenu = new ToolStripMenuItem($"气泡显示时长：{_settings.BubbleSeconds} 秒");
        for (int seconds = 5; seconds <= 10; seconds++)
        {
            int selected = seconds;
            var item = new ToolStripMenuItem($"{selected} 秒") { Checked = selected == _settings.BubbleSeconds };
            item.Click += (_, _) =>
            {
                _settings.BubbleSeconds = selected;
                SaveSettings();
                durationMenu.Text = $"气泡显示时长：{selected} 秒";
                foreach (ToolStripMenuItem option in durationMenu.DropDownItems.OfType<ToolStripMenuItem>())
                    option.Checked = option == item;
                if (_bubbleTimer.Enabled)
                {
                    _bubbleTimer.Stop();
                    _bubbleTimer.Interval = selected * 1000;
                    _bubbleTimer.Start();
                }
            };
            durationMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(durationMenu);
        _menu.Items.Add("调整人物大小…", null, (_, _) => ShowMascotSizeDialog());
        var soundItem = new ToolStripMenuItem("点击音效") { Checked = true, CheckOnClick = true };
        soundItem.CheckedChanged += (_, _) => _soundEnabled = soundItem.Checked;
        _menu.Items.Add(soundItem);
        _menu.Items.Add("退出", null, (_, _) => Close());
        ContextMenuStrip = _menu;
        _tooltip.SetToolTip(this, "点击人物弹出气泡并查询一次；拖动调整位置；右键设置时长和大小");
        _timer.Tick += OnTick;
        _timer.Start();
        Shown += (_, _) => OnTick(this, EventArgs.Empty);
    }

    public void SavePreview(string path)
    {
        _timer.Stop();
        _bubbleVisible = true;
        UpdateVisualLayout();
        _spend = "$12.3400";
        _balance = "自设月预算剩余 $87.66";
        _accountBalance = "组织文本 2.31M tok";
        _model = "模型 gpt-5";
        _session = "最近会话 2.31M tok";
        _details = "输入 2.30M  ·  输出 9.6K";
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        bitmap.MakeTransparent(KeyColor);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void SaveSessionSnapshot(string path)
    {
        _timer.Stop();
        ReadLatestSession();
        File.WriteAllText(path, JsonSerializer.Serialize(new { codexRoot = _codexRoot, rootExists = Directory.Exists(Path.Combine(_codexRoot, "sessions")), model = _model, session = _session, details = _details }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _bubbleTimer.Dispose(); _bounceTimer.Dispose(); _bounceWatch.Stop(); _clickSound.Dispose(); _mascot.Dispose(); _menu.Dispose(); _tooltip.Dispose(); }
        base.Dispose(disposing);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        UpdatePlacement();
    }

    private async Task QueryOnceAsync()
    {
        if (_apiBusy) return;
        _apiBusy = true;
        _bubbleTimer.Stop();
        _bubbleVisible = true;
        UpdateVisualLayout();
        if (_soundEnabled) { try { _clickSound.Play(); } catch { } }
        _spend = "查询中…";
        _balance = "正在读取本月费用";
        _accountBalance = "组织文本 Token 查询中…";
        _error = "";
        ReadLatestSession();
        Invalidate();
        try
        {
            await RefreshApiAsync();
        }
        finally
        {
            _apiBusy = false;
            if (!IsDisposed)
            {
                _bubbleTimer.Interval = _settings.BubbleSeconds * 1000;
                _bubbleTimer.Start();
                Invalidate();
            }
        }
    }

    private int MascotHeight => (int)Math.Round(_mascotWidth * _mascot.Height / (double)_mascot.Width);
    private Rectangle BubbleBounds => _bubbleBounds;
    private Rectangle MascotBounds => _mascotBounds;
    private Rectangle MascotHitBounds
    {
        get
        {
            var shifted = _mascotBounds;
            shifted.Offset(0, _bounceOffset);
            return Rectangle.Union(_mascotBounds, shifted);
        }
    }

    private void UpdateVisualLayout()
    {
        int mascotHeight = MascotHeight;
        var center = new Point(_mascotScreenLocation.X + _mascotWidth / 2,
            _mascotScreenLocation.Y + mascotHeight / 2);
        Rectangle area = Screen.FromPoint(center).WorkingArea;
        const int margin = 4;
        int mascotX = Math.Clamp(_mascotScreenLocation.X, area.Left + margin,
            Math.Max(area.Left + margin, area.Right - _mascotWidth - margin));
        int mascotY = Math.Clamp(_mascotScreenLocation.Y, area.Top + margin,
            Math.Max(area.Top + margin, area.Bottom - mascotHeight - BounceTravel - margin));
        _mascotScreenLocation = new Point(mascotX, mascotY);
        var mascotScreen = new Rectangle(_mascotScreenLocation, new Size(_mascotWidth, mascotHeight));
        var content = new Rectangle(mascotX, mascotY, _mascotWidth, mascotHeight + BounceTravel);
        Rectangle bubbleScreen = Rectangle.Empty;
        if (_bubbleVisible)
        {
            int leftSpace = mascotScreen.Left - area.Left - margin - BubbleGap;
            int rightSpace = area.Right - margin - mascotScreen.Right - BubbleGap;
            bool left = leftSpace >= BubbleW || (rightSpace < BubbleW && leftSpace >= rightSpace);
            int bubbleX = left ? mascotScreen.Left - BubbleGap - BubbleW : mascotScreen.Right + BubbleGap;
            int bubbleY = mascotScreen.Top + (mascotHeight - BubbleH) / 2;
            bubbleX = Math.Clamp(bubbleX, area.Left + margin,
                Math.Max(area.Left + margin, area.Right - BubbleW - margin - 4));
            bubbleY = Math.Clamp(bubbleY, area.Top + margin,
                Math.Max(area.Top + margin, area.Bottom - BubbleH - margin - 6));
            bubbleScreen = new Rectangle(bubbleX, bubbleY, BubbleW, BubbleH);
            content = Rectangle.Union(content, new Rectangle(bubbleX, bubbleY, BubbleW + 4, BubbleH + 6));
        }
        content.Inflate(3, 3);
        _mascotBounds = new Rectangle(mascotScreen.X - content.X, mascotScreen.Y - content.Y,
            mascotScreen.Width, mascotScreen.Height);
        _bubbleBounds = _bubbleVisible
            ? new Rectangle(bubbleScreen.X - content.X, bubbleScreen.Y - content.Y, BubbleW, BubbleH)
            : Rectangle.Empty;
        if (Bounds != content) Bounds = content;
        Invalidate();
    }

    private void StartBounce()
    {
        _bounceOffset = 0;
        _bounceWatch.Restart();
        _bounceTimer.Start();
    }

    private void ShowMascotSizeDialog()
    {
        using var dialog = new Form
        {
            Text = "调整人物大小", ClientSize = new Size(340, 165), StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, TopMost = true
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = new Label { Dock = DockStyle.Fill, Text = $"人物宽度：{_mascotWidth} 像素" };
        var slider = new TrackBar
        {
            Minimum = 80, Maximum = 260, Value = _mascotWidth, TickFrequency = 20,
            SmallChange = 1, LargeChange = 10, Dock = DockStyle.Fill
        };
        slider.ValueChanged += (_, _) => label.Text = $"人物宽度：{slider.Value} 像素";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 82 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82 };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(slider, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK) SetMascotWidth(slider.Value);
    }

    private void SetMascotWidth(int width)
    {
        if (width == _mascotWidth) return;
        int right = _mascotScreenLocation.X + _mascotWidth;
        int bottom = _mascotScreenLocation.Y + MascotHeight;
        _mascotWidth = Math.Clamp(width, 80, 260);
        _mascotScreenLocation = new Point(right - _mascotWidth, bottom - MascotHeight);
        UpdateVisualLayout();
        _settings.MascotWidth = _mascotWidth;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "设置已生效，但无法保存到本机：" + ex.Message, "OpenAI 用量小助手",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdatePlacement()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        bool ownWindow = IsOverlayWindow(hwnd);
        if (!ownWindow && IsCodexWindow(hwnd) && Native.GetWindowRect(hwnd, out var rect))
            _lastCodexRect = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        bool show = DateTime.UtcNow < _showOnLaunchUntil || hwnd == IntPtr.Zero || ownWindow || IsCodexWindow(hwnd);
        if (Visible != show) Visible = show;
        if (show && !_dragging && DateTime.UtcNow > _manualPlacementUntil && _lastCodexRect.Width > 0)
        {
            var target = new Point(_lastCodexRect.Right - _mascotWidth - 18,
                _lastCodexRect.Bottom - MascotHeight - 71);
            if (_mascotScreenLocation != target)
            {
                _mascotScreenLocation = target;
                UpdateVisualLayout();
            }
        }
    }

    private bool IsOverlayWindow(IntPtr hwnd)
    {
        if (hwnd == Handle) return true;
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    private static bool IsCodexWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            string name = Process.GetProcessById((int)pid).ProcessName;
            if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Codex", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    private async Task RefreshApiAsync()
    {
        try
        {
            var result = await OpenAiUsageFetcher.QueryAsync();
            _spend = $"${result.CostUsd:0.0000}";
            if (_settings.MonthlyBudgetUsd is { } budget)
            {
                decimal remaining = budget - result.CostUsd;
                _balance = remaining >= 0 ? $"自设月预算剩余 ${remaining:0.00}"
                    : $"自设月预算超出 ${-remaining:0.00}";
            }
            else _balance = "自设月预算：未设置";
            _accountBalance = result.TokenUsageError is null
                ? "组织文本 " + Compact(result.InputTokens + result.OutputTokens) + " tok"
                : "组织文本 Token 暂不可用";
            _error = "";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _spend = ex.Message.Contains("Admin API Key") ? "未配置" : "查询失败";
            _accountBalance = "组织文本 Token —";
        }
        finally { if (!IsDisposed) Invalidate(); }
    }

    private void ShowAdminSettingsDialog()
    {
        using var dialog = new AdminSettingsDialog(_settings.MonthlyBudgetUsd);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(dialog.NewKey)) AdminKeyStore.Save(dialog.NewKey);
            _settings.MonthlyBudgetUsd = dialog.MonthlyBudgetUsd;
            SaveSettings();
            _balance = "设置已保存，点击人物查询";
            _error = "";
            Invalidate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法保存设置：" + ex.Message, "OpenAI 用量小助手",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReadLatestSession()
    {
        try
        {
            string root = Path.Combine(_codexRoot, "sessions");
            var file = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) return;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? model = null;
            long? total = null, input = null, output = null;
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"token_count\"") && !line.Contains("\"thread_settings_applied\"") &&
                    !line.Contains("\"turn_context\"")) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var entry = json.RootElement;
                    if (!entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                    string? type = entry.GetProperty("type").GetString();
                    if (type == "event_msg" && payload.TryGetProperty("type", out var subtype))
                    {
                        if (subtype.GetString() == "thread_settings_applied" &&
                            payload.TryGetProperty("thread_settings", out var settings) &&
                            settings.TryGetProperty("model", out var modelField))
                            model = modelField.GetString() ?? model;
                        if (subtype.GetString() == "token_count" &&
                            payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                            info.TryGetProperty("total_token_usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        {
                            total = usage.GetProperty("total_tokens").GetInt64();
                            input = usage.GetProperty("input_tokens").GetInt64();
                            output = usage.GetProperty("output_tokens").GetInt64();
                        }
                    }
                    else if (type == "turn_context" && payload.TryGetProperty("model", out var contextModel))
                        model = contextModel.GetString() ?? model;
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
                catch (KeyNotFoundException) { }
            }
            if (!string.IsNullOrWhiteSpace(model)) _model = "模型 " + DisplayModel(model);
            if (total is not null && input is not null && output is not null)
            {
                _session = "最近会话 " + Compact(total.Value) + " tok";
                _details = "输入 " + Compact(input.Value) + "  ·  输出 " + Compact(output.Value);
            }
            Invalidate();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string DisplayModel(string id)
    {
        if (id.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase))
        {
            string[] words = id[9..].Split('-', StringSplitOptions.RemoveEmptyEntries);
            return "DeepSeek " + string.Join(" ", words.Select(word =>
                word.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word));
        }
        return id;
    }

    private static string ResolveCodexRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        string? profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(profile)) profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("无法定位用户主目录");
        return Path.Combine(profile, ".codex");
    }

    private static string Compact(long value) => value >= 1_000_000 ? $"{value / 1_000_000.0:0.00}M" :
        value >= 1000 ? $"{value / 1000.0:0.#}K" : value.ToString();

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_bubbleVisible)
        {
            var state = g.Save();
            g.TranslateTransform(BubbleBounds.X - BubbleX, BubbleBounds.Y - BubbleY);
            using var shadow = RoundedRect(new Rectangle(BubbleX + 3, BubbleY + 5, BubbleW, BubbleH), 24);
            using var shadowBrush = new SolidBrush(Color.FromArgb(45, 37, 27, 73));
            g.FillPath(shadowBrush, shadow);
            using var bubble = RoundedRect(new Rectangle(BubbleX, BubbleY, BubbleW, BubbleH), 24);
            using var fill = new SolidBrush(Color.FromArgb(250, 250, 247, 255));
            using var stroke = new Pen(Color.FromArgb(205, 187, 224), 1.5f);
            g.FillPath(fill, bubble);
            g.DrawPath(stroke, bubble);
            using var titleFont = new Font("Microsoft YaHei UI", 9, FontStyle.Bold);
            using var modelFont = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
            using var bigFont = new Font("Microsoft YaHei UI", 23, FontStyle.Bold);
            using var smallFont = new Font("Microsoft YaHei UI", 9);
            using var accent = new SolidBrush(Color.FromArgb(111, 76, 155));
            using var main = new SolidBrush(Color.FromArgb(65, 42, 93));
            using var secondary = new SolidBrush(Color.FromArgb(116, 104, 132));
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString("OpenAI · 本月组织费用", titleFont, accent, 30, 24);
            g.DrawString(_model, modelFont, main, new RectangleF(30, 44, 272, 22), format);
            g.DrawString(_spend, bigFont, main, 26, 65);
            g.DrawString(_error == "" ? _balance : _error, smallFont, secondary, new RectangleF(30, 112, 272, 22), format);
            g.DrawString(_accountBalance, modelFont, main, new RectangleF(30, 135, 272, 24), format);
            g.DrawString(_session, smallFont, main, 30, 163);
            g.DrawString(_details, smallFont, secondary, 30, 186);
            g.Restore(state);
        }
        var mascot = MascotBounds;
        mascot.Offset(0, _bounceOffset);
        g.DrawImage(_mascot, mascot);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left &&
            (MascotHitBounds.Contains(e.Location) || (_bubbleVisible && BubbleBounds.Contains(e.Location))))
        {
            _dragging = true;
            _dragOrigin = Cursor.Position;
            _mascotDragOrigin = _mascotScreenLocation;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var p = Cursor.Position;
            _mascotScreenLocation = new Point(_mascotDragOrigin.X + p.X - _dragOrigin.X,
                _mascotDragOrigin.Y + p.Y - _dragOrigin.Y);
            UpdateVisualLayout();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        if (Math.Abs(Cursor.Position.X - _dragOrigin.X) < 5 && Math.Abs(Cursor.Position.Y - _dragOrigin.Y) < 5)
        {
            if (MascotHitBounds.Contains(e.Location))
            {
                StartBounce();
                _ = QueryOnceAsync();
            }
            else if (_bubbleVisible && BubbleBounds.Contains(e.Location))
                _ = QueryOnceAsync();
        }
        else _manualPlacementUntil = DateTime.UtcNow.AddMinutes(30);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    }
}
