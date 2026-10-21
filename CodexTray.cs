using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) { Checks.Run(); return 0; }
            if (args.Contains("--check"))
            {
                var usage = Codex.ReadAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage-check.txt"), usage.Summary());
                return 0;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var single = new Mutex(false, @"Local\Rhyse.CodexTray"))
            {
                if (!single.WaitOne(0)) return 0;
                try { using (var app = new TrayApp(args.Contains("--show"))) Application.Run(app); }
                finally { single.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.txt"), e.Message);
            return 1;
        }
    }
}

sealed class WindowLimit
{
    public double Left;
    public DateTimeOffset? Reset;
    public bool Expired { get { return Reset.HasValue && Reset.Value <= DateTimeOffset.Now; } }
    public string Percent { get { return Expired ? "--" : Math.Floor(Left).ToString(CultureInfo.InvariantCulture) + "%"; } }
    public string ResetText(string format)
    {
        return Reset.HasValue ? Reset.Value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture) : "UNKNOWN";
    }
}

sealed class Usage
{
    public WindowLimit FiveHours, Week;
    public DateTimeOffset Updated = DateTimeOffset.Now;

    public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
    public static object Get(Dictionary<string, object> obj, string key)
    {
        object value;
        return obj != null && obj.TryGetValue(key, out value) ? value : null;
    }
    public static Usage Parse(Dictionary<string, object> response)
    {
        var result = Obj(Get(response, "result"));
        if (result == null) throw new InvalidDataException("Codex returned no limit data.");
        var buckets = Obj(Get(result, "rateLimitsByLimitId"));
        // Select the account's Codex quota; never substitute another model's bucket.
        var quota = buckets != null ? Obj(Get(buckets, "codex")) : null;
        if (quota == null)
        {
            quota = Obj(Get(result, "rateLimits"));
            var id = Get(quota, "limitId") as string;
            if (id != null && id != "codex") quota = null;
        }
        if (quota == null) throw new InvalidDataException("Codex account limits unavailable.");
        var usage = new Usage();
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = Obj(Get(quota, key));
            var duration = Get(window, "windowDurationMins");
            var usedValue = Get(window, "usedPercent");
            if (duration == null || usedValue == null) continue;
            double used = Convert.ToDouble(usedValue, CultureInfo.InvariantCulture);
            if (Double.IsNaN(used) || Double.IsInfinity(used) || used < 0 || used > 100) continue;
            var limit = new WindowLimit { Left = 100 - used };
            var reset = Get(window, "resetsAt");
            if (reset != null)
            {
                try { limit.Reset = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(Convert.ToDouble(reset, CultureInfo.InvariantCulture)); }
                catch (ArgumentOutOfRangeException) { }
            }
            int minutes = Convert.ToInt32(duration, CultureInfo.InvariantCulture);
            if (minutes == 300) usage.FiveHours = limit;
            if (minutes == 10080) usage.Week = limit;
        }
        return usage;
    }
    public string Summary()
    {
        return Line("5h", FiveHours) + "\n" + Line("Week", Week);
    }
    static string Line(string title, WindowLimit limit)
    {
        return title + (limit == null ? " not reported" : " " + limit.Percent + " | " + limit.ResetText("MMMdd h:mm tt"));
    }
}

static class Codex
{
    static string Profile { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Length > 0 ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Environment.GetEnvironmentVariable("USERPROFILE"); } }
    static string FindExe()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var locations = new List<string> { Path.Combine(root, "codex.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (!String.IsNullOrWhiteSpace(dir)) locations.Add(Path.Combine(dir.Trim('"'), "codex.exe"));
        // VS Code and the Windows Codex app ship the same local protocol executable.
        var profile = Profile;
        var extensions = Path.Combine(profile, ".vscode", "extensions");
        if (Directory.Exists(extensions))
            locations.AddRange(Directory.GetDirectories(extensions, "openai.chatgpt-*")
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(dir => Path.Combine(dir, "bin", "windows-x86_64", "codex.exe")));
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex");
        locations.Add(Path.Combine(apps, "resources", "codex.exe"));
        var found = locations.FirstOrDefault(File.Exists);
        if (found == null) throw new FileNotFoundException("Install Codex CLI or its VS Code extension.");
        return found;
    }

    public static async Task<Usage> ReadAsync()
    {
        // One short-lived reader per refresh keeps idle memory low. No model turns.
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(FindExe(), "app-server --stdio") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_HOME")))
                process.StartInfo.EnvironmentVariables["CODEX_HOME"] = Path.Combine(Profile, ".codex");
            process.ErrorDataReceived += delegate { }; // Drain stderr without storing account/config details.
            process.Start();
            process.BeginErrorReadLine();
            try
            {
                var deadline = Task.Delay(20000);
                await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex_tray\",\"title\":\"Codex Tray\",\"version\":\"1.0.0\"}}}");
                await Reply(process, 1, deadline);
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
                await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                return Usage.Parse(await Reply(process, 2, deadline));
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
                if (!process.HasExited)
                {
                    // Close stdin for normal cleanup; kill only this reader if it hangs.
                    if (!process.WaitForExit(2000)) process.Kill();
                }
            }
        }
    }
    static async Task<Dictionary<string, object>> Reply(Process process, int id, Task deadline)
    {
        var json = new JavaScriptSerializer();
        while (true)
        {
            var read = process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, deadline) != read) throw new TimeoutException("Codex timed out. Try refresh.");
            string line = await read;
            if (line == null) throw new IOException("Codex reader stopped. Check your Codex login.");
            var response = json.Deserialize<Dictionary<string, object>>(line);
            var responseId = Usage.Get(response, "id");
            if (responseId == null || Convert.ToString(responseId, CultureInfo.InvariantCulture) != id.ToString()) continue;
            if (Usage.Get(response, "error") != null) throw new InvalidOperationException("Cannot read limits. Sign in to Codex with ChatGPT, then refresh.");
            return response;
        }
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Widget widget = new Widget();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60000 };
    readonly Icon icon;
    bool busy, stopped;
    Usage usage;
    string error;

    public TrayApp(bool show)
    {
        icon = MakeIcon();
        tray = new NotifyIcon { Icon = icon, Text = "Codex: checking limits...", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / hide", null, delegate { Toggle(); });
        menu.Items.Add("Refresh", null, async delegate { await Refresh(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { ExitThread(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Toggle(); };
        widget.RefreshRequested += async delegate { await Refresh(); };
        timer.Tick += async delegate { await Refresh(); };
        timer.Start();
        if (show) Toggle();
        // The message loop must be running before the asynchronous result is applied.
        widget.CreateControl();
        var handle = widget.Handle;
        widget.BeginInvoke(new Action(async () => await Refresh()));
    }
    async Task Refresh()
    {
        if (busy || stopped) return;
        busy = true;
        UpdateView();
        try { usage = await Task.Run(() => Codex.ReadAsync()); error = null; }
        catch (Exception e) { error = e.Message; }
        finally { busy = false; if (!stopped) UpdateView(); }
    }
    void UpdateView()
    {
        widget.SetData(usage, error, busy);
        string summary = usage == null ? (error == null ? "Codex: checking limits..." : "Codex: unavailable. Click for details.") : usage.Summary();
        if (usage != null && error != null) summary += "\nSTALE";
        tray.Text = summary.Length <= 63 ? summary : summary.Substring(0, 63);
    }
    void Toggle()
    {
        if (widget.Visible) widget.Hide();
        else
        {
            if (!widget.WasShown)
            {
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                widget.Location = new Point(area.Right - widget.Width - 12, area.Bottom - widget.Height - 12);
                widget.WasShown = true;
            }
            widget.Show(); widget.Activate();
            // Refresh only if the snapshot is old, including after sleep.
            if (usage == null || (DateTimeOffset.Now - usage.Updated).TotalSeconds >= 60)
                widget.BeginInvoke(new Action(async () => await Refresh()));
        }
    }
    static Icon MakeIcon()
    {
        using (var bitmap = new Bitmap(16, 16))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                using (var ink = new SolidBrush(Color.FromArgb(202, 185, 225)))
                {
                    // Static pixel brackets. The icon never carries usage information.
                    graphics.FillRectangle(ink, 2, 3, 5, 2); graphics.FillRectangle(ink, 2, 5, 2, 6);
                    graphics.FillRectangle(ink, 2, 11, 5, 2); graphics.FillRectangle(ink, 9, 3, 5, 2);
                    graphics.FillRectangle(ink, 12, 5, 2, 6); graphics.FillRectangle(ink, 9, 11, 5, 2);
                }
            }
            var hicon = bitmap.GetHicon();
            try { using (var borrowed = Icon.FromHandle(hicon)) return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(hicon); }
        }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    protected override void ExitThreadCore()
    {
        stopped = true;
        timer.Stop(); timer.Dispose();
        tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose();
        icon.Dispose(); widget.Dispose();
        base.ExitThreadCore();
    }
}

sealed class Widget : Form
{
    public event EventHandler RefreshRequested;
    public bool WasShown;
    Usage usage;
    string error;
    bool busy;
    readonly PixelButton refresh;
    readonly ToolTip tip = new ToolTip();
    static readonly Color Ink = Color.FromArgb(228, 220, 237), Muted = Color.FromArgb(163, 154, 175), Purple = Color.FromArgb(202, 185, 225);

    public Widget()
    {
        Text = "Codex Usage"; AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(264, 176);
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(29, 28, 32); DoubleBuffered = true; KeyPreview = true;
        var close = new PixelButton("X", "Hide widget") { Bounds = new Rectangle(232, 5, 26, 24), TabIndex = 1 };
        close.Click += delegate { Hide(); }; Controls.Add(close);
        refresh = new PixelButton("R", "Refresh usage") { Bounds = new Rectangle(232, 148, 26, 24), TabIndex = 0 };
        refresh.Click += delegate { if (RefreshRequested != null) RefreshRequested(this, EventArgs.Empty); };
        Controls.Add(refresh); tip.SetToolTip(refresh, "Refresh (R)"); tip.SetToolTip(close, "Hide (Esc)");
        KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            if (e.KeyCode == Keys.R) { refresh.PerformClick(); e.Handled = true; }
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        MouseDown += delegate(object sender, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left && e.Y < 38) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        };
    }
    public void SetData(Usage value, string failure, bool loading)
    {
        usage = value; error = failure; busy = loading; refresh.Enabled = !loading;
        AccessibleDescription = (value == null ? "Usage unavailable" : value.Summary()) + (failure == null ? "" : ". Stale: " + failure);
        tip.SetToolTip(this, failure ?? "Reset times use your Windows time zone. Drag the header to move.");
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var border = new Pen(Color.FromArgb(74, 68, 82))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        Pixel.Draw(g, "CODEX / LEFT", 12, 12, 2, Purple);
        DrawLimit(g, "5 HOURS", usage == null ? null : usage.FiveHours, 44);
        DrawLimit(g, "WEEK", usage == null ? null : usage.Week, 100);
        string status = busy ? "CHECKING..." : error != null ? (usage == null ? "UNAVAILABLE - HOVER FOR HELP" : "STALE - REFRESH FAILED") : usage == null ? "WAITING..." : "UPDATED " + usage.Updated.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture) + " / LOCAL TIME";
        Pixel.Draw(g, status, 12, 159, 1, error == null ? Muted : Purple);
    }
    void DrawLimit(Graphics g, string title, WindowLimit limit, int y)
    {
        Pixel.Draw(g, title, 12, y, 2, Ink);
        string amount = limit == null ? "--" : limit.Percent;
        Pixel.Draw(g, amount, 252 - amount.Length * 12, y, 2, limit == null || limit.Expired || error != null ? Muted : Purple);
        using (var track = new SolidBrush(Color.FromArgb(66, 61, 72))) g.FillRectangle(track, 12, y + 24, 240, 1);
        if (limit != null && !limit.Expired)
            using (var fill = new SolidBrush(error == null ? Purple : Muted)) g.FillRectangle(fill, 12, y + 24, (int)Math.Floor(240 * limit.Left / 100), 1);
        string reset = limit == null ? "NOT REPORTED BY CODEX" : limit.Expired ? "RESET PASSED - WAITING" : "RESET " + limit.ResetText("MMM dd h:mm tt").ToUpperInvariant();
        Pixel.Draw(g, reset, 12, y + 36, 1, Muted);
    }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);
}

sealed class PixelButton : Button
{
    public PixelButton(string text, string name) { Text = text; AccessibleName = name; Cursor = Cursors.Hand; FlatStyle = FlatStyle.Flat; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        Pixel.Draw(e.Graphics, Text, (Width - 10) / 2, (Height - 14) / 2, 2, Enabled ? Color.FromArgb(202, 185, 225) : Color.DimGray);
    }
}

static class Pixel
{
    // Embedded 5x7 bitmap letterforms: no font installation or downloads.
    const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789%:-/. ";
    static readonly string[] Glyphs = ("7E1111117E 7F49494936 3E41414122 7F4141221C 7F49494941 7F09090901 3E41495172 7F0808087F 00417F4100 2040413F01 7F08142241 7F40404040 7F020C027F 7F0408107F 3E4141413E 7F09090906 3E4151215E 7F09192946 2649494932 01017F0101 3F4040403F 1F2040201F 3F4030403F 6314081463 0708700807 6151494543 3E4549513E 00427F4000 4261514946 2141454B31 1814127F10 2745454539 3C4A494930 0171090503 3649494936 064949291E 2313086462 0036000000 0808080808 2010080402 0060600000 0000000000").Split(' ');
    public static void Draw(Graphics g, string text, int x, int y, int scale, Color color)
    {
        using (var ink = new SolidBrush(color))
            foreach (char c in text.ToUpperInvariant())
            {
                int index = Chars.IndexOf(c);
                if (index >= 0)
                    for (int column = 0; column < 5; column++)
                    {
                        int bits = Int32.Parse(Glyphs[index].Substring(column * 2, 2), NumberStyles.HexNumber);
                        for (int row = 0; row < 7; row++)
                            if ((bits & (1 << row)) != 0) g.FillRectangle(ink, x + column * scale, y + row * scale, scale, scale);
                    }
                x += 6 * scale;
            }
    }
}

static class Checks
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception("Self-test: " + message); }
    public static void Run()
    {
        var json = new JavaScriptSerializer();
        var usage = Usage.Parse(json.Deserialize<Dictionary<string, object>>(
            "{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":10080,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":2000000000}}}}}"));
        Assert(usage.FiveHours.Left == 90 && usage.Week.Left == 75, "select Codex bucket and classify by duration, not position");
        Assert(usage.Week.Reset.Value.ToUnixTime() == 2000000000, "Unix reset timestamp");
        Assert(usage.Summary().Length + 6 <= 63, "native tooltip fits, including stale marker");
        var widest = new Usage { FiveHours = new WindowLimit { Left = 100, Reset = DateTimeOffset.Now.Date.AddYears(1).AddHours(23).AddMinutes(59) }, Week = new WindowLimit { Left = 100, Reset = DateTimeOffset.Now.Date.AddYears(1).AddHours(23).AddMinutes(59) } };
        Assert(widest.Summary().Length + 6 <= 63, "AM/PM tooltip fits with 100% and stale marker");
        var midnight = new WindowLimit { Reset = DateTimeOffset.Now.Date.AddYears(1) };
        var noon = new WindowLimit { Reset = DateTimeOffset.Now.Date.AddYears(1).AddHours(12) };
        Assert(midnight.ResetText("h:mm tt") == "12:00 AM" && noon.ResetText("h:mm tt") == "12:00 PM", "midnight and noon use AM/PM");
        var missing = Usage.Parse(json.Deserialize<Dictionary<string, object>>("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":5,\"windowDurationMins\":10080},\"secondary\":null}}}"));
        Assert(missing.FiveHours == null && missing.Week.Left == 95 && !missing.Week.Reset.HasValue, "missing window and reset stay unknown");
        var expired = new WindowLimit { Left = 10, Reset = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert(expired.Percent == "--", "expired usage cannot appear current or assume a reset");
        var invalid = Usage.Parse(json.Deserialize<Dictionary<string, object>>("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":-1,\"windowDurationMins\":300}}}}"));
        Assert(invalid.FiveHours == null, "reject invalid percentage");
        using (var widget = new Widget())
        using (var image = new Bitmap(widget.Width, widget.Height))
        {
            widget.SetData(usage, null, false);
            widget.Location = new Point(-32000, -32000);
            widget.Show();
            widget.DrawToBitmap(image, widget.ClientRectangle);
            Assert(widget.ClientSize == new Size(264, 176), "compact widget size");
            Assert(image.GetPixel(12, 68).G == 185 && image.GetPixel(12, 69).G != 185, "bar is exactly one physical pixel high");
            Assert(image.GetPixel(240, 10).G == 185 && image.GetPixel(240, 153).G == 185, "hide and refresh controls render");
            widget.Hide();
            image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "widget-preview.png"));
        }
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.txt"), "PASS: window mapping, bucket selection, remaining percentage, AM/PM including noon/midnight, resets, missing/expired/invalid data, tooltip length, compact widget, one-pixel bar, widget rendering.");
    }
    static long ToUnixTime(this DateTimeOffset value) { return (long)(value - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds; }
}
