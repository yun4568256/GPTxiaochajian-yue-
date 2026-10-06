using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CaapiUsageOverlay;

internal record AccountUsageResult(double? AvailableUsd, bool AuthRequired, string? Error);

internal static class AccountUsageFetcher
{
    public static Task<AccountUsageResult> QueryAsync() => RunAsync("--query");
    public static Task<AccountUsageResult> AuthorizeAsync(string cookie) => RunAsync("--authorize", cookie);
    public static Task<AccountUsageResult> ClearAsync() => RunAsync("--clear");

    private static async Task<AccountUsageResult> RunAsync(string command, string? input = null)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "query_account.py");
        if (!File.Exists(script)) throw new InvalidOperationException("缺少 query_account.py");
        using var process = new Process { StartInfo = new ProcessStartInfo(UsageFetcher.ResolvePython())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        } };
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        try { process.Start(); }
        catch (Exception ex) { throw new InvalidOperationException("无法启动账号查询程序：" + ex.Message, ex); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdoutTask;
            string stderr = await stderrTask;
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            string? error = root.TryGetProperty("error", out var errorField) ? errorField.GetString() : null;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error ?? (string.IsNullOrWhiteSpace(stderr) ? "账号查询失败" : stderr.Trim()));
            bool authRequired = root.TryGetProperty("auth_required", out var authField) && authField.ValueKind == JsonValueKind.True;
            double? available = root.TryGetProperty("available_usd", out var balanceField) && balanceField.ValueKind == JsonValueKind.Number
                ? balanceField.GetDouble() : null;
            return new AccountUsageResult(available, authRequired, error);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("账号余额查询超时，请稍后重试");
        }
        catch (JsonException ex) { throw new InvalidOperationException("账号查询程序返回了无效数据", ex); }
    }
}

internal sealed class AccountLoginDialog : Form
{
    private readonly TextBox _cookie = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.Firebrick };
    private readonly Button _save = new() { Text = "保存并查询", Width = 112 };
    public AccountUsageResult? Result { get; private set; }

    public AccountLoginDialog()
    {
        Text = "授权 CAAPI 账号余额查询";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(710, 325);
        MinimumSize = new Size(620, 290);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var guide = new Label
        {
            Dock = DockStyle.Fill,
            Text = "在浏览器登录 caapi.top，按 F12 打开“网络”，刷新网页，选择 /api/user/auth/refresh 请求。\n在“请求标头”中复制 Cookie 的值，粘贴到下方。请勿把 Cookie 发到聊天里。",
        };
        var link = new LinkLabel { Text = "打开 CAAPI 用量页面", Dock = DockStyle.Fill };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://caapi.top/dashboard/overview") { UseShellExecute = true });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", Width = 88 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _save.Click += OnSaveClick;
        buttons.Controls.Add(_save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(guide, 0, 0);
        layout.Controls.Add(link, 0, 1);
        layout.Controls.Add(_cookie, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = _save;
        CancelButton = cancel;
    }

    private async void OnSaveClick(object? sender, EventArgs args)
    {
        _save.Enabled = false;
        _status.ForeColor = Color.FromArgb(65, 42, 93);
        _status.Text = "正在验证网站授权…";
        try
        {
            var result = await AccountUsageFetcher.AuthorizeAsync(_cookie.Text);
            if (result.AuthRequired || result.AvailableUsd is null)
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = result.Error ?? "网站登录信息无效，请重新复制 Cookie。";
                return;
            }
            Result = result;
            _cookie.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = ex.Message;
        }
        finally { _save.Enabled = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cookie.Clear();
        base.Dispose(disposing);
    }
}
