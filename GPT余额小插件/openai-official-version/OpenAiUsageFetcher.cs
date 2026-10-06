using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenAiUsageOverlay;

internal record OpenAiUsageResult(decimal CostUsd, long InputTokens, long OutputTokens,
    DateTimeOffset PeriodStart, string? TokenUsageError);

internal static class OpenAiUsageFetcher
{
    public static async Task<OpenAiUsageResult> QueryAsync()
    {
        string? key = AdminKeyStore.Load();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("请右键配置 OpenAI Admin API Key");

        using var http = new HttpClient { BaseAddress = new Uri("https://api.openai.com"),
            Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await QueryWithClientAsync(http, DateTimeOffset.UtcNow);
    }

    private static async Task<OpenAiUsageResult> QueryWithClientAsync(HttpClient http, DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        decimal cost = 0;
        await ReadPagesAsync(http, "/v1/organization/costs", start, now, result =>
        {
            if (!result.TryGetProperty("amount", out var amount) ||
                !amount.TryGetProperty("value", out var value)) return;
            if (value.ValueKind == JsonValueKind.Number) cost += value.GetDecimal();
            else if (value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                cost += parsed;
        });

        long input = 0, output = 0;
        string? tokenError = null;
        try
        {
            await ReadPagesAsync(http, "/v1/organization/usage/completions", start, now, result =>
            {
                if (result.TryGetProperty("input_tokens", out var inputValue) &&
                    inputValue.ValueKind == JsonValueKind.Number) input += inputValue.GetInt64();
                if (result.TryGetProperty("output_tokens", out var outputValue) &&
                    outputValue.ValueKind == JsonValueKind.Number) output += outputValue.GetInt64();
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            tokenError = ex.Message;
        }
        return new OpenAiUsageResult(cost, input, output, start, tokenError);
    }

    public static async Task SelfTestAsync()
    {
        using var http = new HttpClient(new FixtureHandler())
        {
            BaseAddress = new Uri("https://api.openai.com")
        };
        var result = await QueryWithClientAsync(http, new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        if (result.CostUsd != 3.25m || result.InputTokens != 100 || result.OutputTokens != 20 ||
            result.PeriodStart != new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) ||
            result.TokenUsageError is not null)
            throw new InvalidOperationException("模拟用量与费用汇总失败");
        AdminKeyStore.SelfTest();
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            bool second = request.RequestUri?.Query.Contains("page=p2") == true;
            string json = path switch
            {
                "/v1/organization/costs" when second =>
                    """{"data":[{"results":[{"amount":{"value":2.00,"currency":"usd"}}]}],"has_more":false}""",
                "/v1/organization/costs" =>
                    """{"data":[{"results":[{"amount":{"value":1.25,"currency":"usd"}}]}],"has_more":true,"next_page":"p2"}""",
                "/v1/organization/usage/completions" =>
                    """{"data":[{"results":[{"input_tokens":100,"output_tokens":20}]}],"has_more":false}""",
                _ => throw new InvalidOperationException("模拟请求路径错误：" + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task ReadPagesAsync(HttpClient http, string path, DateTimeOffset start,
        DateTimeOffset end, Action<JsonElement> onResult)
    {
        string? page = null;
        for (int pageCount = 0; pageCount < 12; pageCount++)
        {
            string query = $"?start_time={start.ToUnixTimeSeconds()}&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d&limit=31";
            if (!string.IsNullOrEmpty(page)) query += "&page=" + Uri.EscapeDataString(page);
            using var response = await http.GetAsync(path + query);
            if (!response.IsSuccessStatusCode)
            {
                string message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Admin API Key 无效或已失效",
                    HttpStatusCode.Forbidden => "Admin API Key 没有组织用量权限",
                    HttpStatusCode.TooManyRequests => "OpenAI 请求过于频繁，请稍后点击重试",
                    _ => $"OpenAI 组织查询失败（HTTP {(int)response.StatusCode}）"
                };
                throw new HttpRequestException(message);
            }
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("OpenAI 返回了无效的用量数据");
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var result in results.EnumerateArray()) onResult(result);
            }
            bool hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            if (!hasMore) return;
            page = root.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            if (string.IsNullOrWhiteSpace(page)) throw new InvalidOperationException("OpenAI 用量分页信息缺失");
        }
        throw new InvalidOperationException("OpenAI 用量分页超过安全上限");
    }
}

internal static class AdminKeyStore
{
    private const uint UiForbidden = 0x1;
    private static string KeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAiUsageOverlay", "admin-key.bin");

    public static bool HasStoredKey =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_ADMIN_KEY")) ||
        File.Exists(KeyPath);

    public static string? Load()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("OPENAI_ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
        if (!File.Exists(KeyPath)) return null;
        byte[] saved = File.ReadAllBytes(KeyPath);
        if (saved.Length < 3 || saved[1] != (byte)'1')
            throw new InvalidOperationException("本机 Admin API Key 格式无效，请清除后重新配置");
        uint scope = saved[0] switch
        {
            (byte)'U' => UiForbidden,
            (byte)'M' => UiForbidden | LocalMachine,
            _ => throw new InvalidOperationException("本机 Admin API Key 格式无效，请清除后重新配置")
        };
        return Encoding.UTF8.GetString(Transform(saved[2..], decrypt: true, scope));
    }

    public static void Save(string key)
    {
        key = key.Trim();
        if (key.Length < 8 || key.Length > 1000 || key.Contains('\n') || key.Contains('\r'))
            throw new ArgumentException("请输入有效的 Admin API Key");
        string path = KeyPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Protect(Encoding.UTF8.GetBytes(key)));
        File.Move(temporary, path, overwrite: true);
    }

    public static void Clear()
    {
        if (File.Exists(KeyPath)) File.Delete(KeyPath);
    }

    public static void SelfTest()
    {
        byte[] sample = Encoding.UTF8.GetBytes("test-only-value");
        byte[] protectedValue = Protect(sample);
        uint scope = protectedValue[0] == (byte)'U' ? UiForbidden : UiForbidden | LocalMachine;
        byte[] roundTrip = Transform(protectedValue[2..], decrypt: true, scope);
        if (!sample.SequenceEqual(roundTrip))
            throw new InvalidOperationException("Windows 本机加密验证失败");
    }

    private const uint LocalMachine = 0x4;

    private static byte[] Protect(byte[] data)
    {
        try { return [(byte)'U', (byte)'1', .. Transform(data, decrypt: false, UiForbidden)]; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // An isolated Windows desktop may not have access to the user's DPAPI profile.
            return [(byte)'M', (byte)'1', .. Transform(data, decrypt: false, UiForbidden | LocalMachine)];
        }
    }

    private static byte[] Transform(byte[] data, bool decrypt, uint flags)
    {
        IntPtr inputMemory = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, inputMemory, data.Length);
        var input = new DataBlob { Length = data.Length, Data = inputMemory };
        DataBlob output;
        try
        {
            bool okay = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    flags, out output)
                : CryptProtectData(ref input, "OpenAI Admin API Key", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, flags, out output);
            if (!okay) throw new Win32Exception(Marshal.GetLastWin32Error(),
                decrypt ? "无法解密本机 Admin API Key" : "无法加密 Admin API Key");
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputMemory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class AdminSettingsDialog : Form
{
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly NumericUpDown _budget = new()
    {
        Minimum = 0, Maximum = 1000000, DecimalPlaces = 2, Increment = 5,
        ThousandsSeparator = true, Dock = DockStyle.Fill
    };

    public string NewKey => _key.Text;
    public decimal? MonthlyBudgetUsd => _budget.Value > 0 ? _budget.Value : null;

    public AdminSettingsDialog(decimal? currentBudget)
    {
        Text = "OpenAI 组织用量设置";
        ClientSize = new Size(600, 275);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        _budget.Value = Math.Clamp(currentBudget ?? 0, 0, _budget.Maximum);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "组织费用和用量需要 OpenAI Admin API Key。密钥在本机加密保存，留空则保留现有密钥。",
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = AdminKeyStore.HasStoredKey ? "Admin API Key（已配置；留空不更改）" : "Admin API Key（尚未配置）"
        }, 0, 1);
        layout.Controls.Add(_key, 0, 2);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "自设月预算（美元；填 0 表示不设置。预算剩余不等于账户实际余额）"
        }, 0, 3);
        layout.Controls.Add(_budget, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, Width = 88 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _key.Clear();
        base.Dispose(disposing);
    }
}
