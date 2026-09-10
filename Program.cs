using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GPTUsageTray {
static class Json {
    public static Dictionary<string, object> Parse(string s) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(s); }
    public static string Stringify(object o) { return new JavaScriptSerializer().Serialize(o); }
    public static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }
    public static string Str(Dictionary<string, object> d, string k) { return Convert.ToString(Get(d, k), CultureInfo.InvariantCulture); }
}

sealed class RpcException : Exception { public RpcException(string message) : base(message) {} }

// Uses the installed, official CLI for account state, OAuth and token renewal.
// Credentials never pass through this client or its diagnostics.
sealed class AppServer : IDisposable {
    Process process;
    readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    readonly object writeLock = new object();
    int nextId;
    volatile bool disposed;
    public event Action<string, Dictionary<string, object>> Notification;
    public bool Alive { get { try { return process != null && !process.HasExited && !disposed; } catch { return false; } } }
    public int ProcessId { get { return Alive ? process.Id : 0; } }

    public static string FindCodex() {
        string configured = Environment.GetEnvironmentVariable("GPT_USAGE_CODEX_EXE");
        if (!String.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var roots = new List<string> {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
        };
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(p => !String.IsNullOrWhiteSpace(p)));
        foreach (string root in roots.Distinct()) {
            try {
                string direct = Path.Combine(root.Trim('"'), "codex.exe");
                if (File.Exists(direct)) return direct;
                string vendor = Path.Combine(root.Trim('"'), "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
                if (File.Exists(vendor)) return vendor;
            } catch { }
        }
        throw new IOException("Codex CLI was not found. Install it, then choose Refresh now.");
    }
    public async Task Start() {
        var info = new ProcessStartInfo(FindCodex(), "app-server --stdio") {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        process = new Process { StartInfo = info };
        process.Start();
        // Always drain both pipes, but do not retain server stderr (it can contain private data).
        #pragma warning disable 4014 // Reader tasks run for the owned helper's lifetime.
        Task.Run(async delegate { try { while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) != null) {} } catch {} });
        Task.Run((Func<Task>)ReadLoop);
        #pragma warning restore 4014
        await Call("initialize", new { clientInfo = new { name = "gpt_usage_tray", title = "GPT Usage Tray", version = "1.0.0" } });
        Send(new { method = "initialized", @params = new {} });
    }
    async Task ReadLoop() {
        try {
            string line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                Dictionary<string, object> msg;
                try { msg = Json.Parse(line); } catch { continue; }
                object id = Json.Get(msg, "id");
                if (id != null && Json.Get(msg, "method") == null) {
                    TaskCompletionSource<Dictionary<string, object>> tcs;
                    if (pending.TryRemove(Convert.ToInt32(id), out tcs)) {
                        var error = Json.Obj(msg, "error");
                        if (error != null) tcs.TrySetException(new RpcException(Json.Str(error, "message")));
                        else tcs.TrySetResult(Json.Obj(msg, "result") ?? new Dictionary<string, object>());
                    }
                } else if (id != null) {
                    Send(new { id = id, error = new { code = -32601, message = "This client only supports account and usage operations." } });
                } else {
                    var handler = Notification;
                    if (handler != null) handler(Json.Str(msg, "method"), Json.Obj(msg, "params"));
                }
            }
        } catch { }
        finally { FailPending(); }
    }
    void Send(object data) {
        lock (writeLock) {
            if (!Alive) throw new IOException("The usage service disconnected.");
            process.StandardInput.WriteLine(Json.Stringify(data));
            process.StandardInput.Flush();
        }
    }
    public async Task<Dictionary<string, object>> Call(string method, object args) {
        int id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try {
            Send(new { id = id, method = method, @params = args });
            using (var timeout = new CancellationTokenSource()) {
                var delay = Task.Delay(TimeSpan.FromSeconds(35), timeout.Token);
                if (await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false) != tcs.Task) throw new TimeoutException("The usage service took too long to respond.");
                timeout.Cancel();
                return await tcs.Task.ConfigureAwait(false);
            }
        } finally { TaskCompletionSource<Dictionary<string, object>> removed; pending.TryRemove(id, out removed); }
    }
    void FailPending() {
        foreach (var pair in pending) { TaskCompletionSource<Dictionary<string, object>> t; if (pending.TryRemove(pair.Key, out t)) t.TrySetException(new IOException("The usage service disconnected.")); }
    }
    public void Dispose() {
        disposed = true;
        try { if (process != null && !process.HasExited) { process.StandardInput.Close(); if (!process.WaitForExit(1200)) process.Kill(); } } catch { }
        FailPending();
        if (process != null) process.Dispose();
    }
}

sealed class UsageWindow {
    public string Key, Name;
    public int Remaining, Minutes;
    public DateTimeOffset? Reset;
    public static UsageWindow Read(Dictionary<string, object> d, string key) {
        if (d == null || Json.Get(d, "usedPercent") == null) return null;
        double used = Convert.ToDouble(Json.Get(d, "usedPercent"), CultureInfo.InvariantCulture);
        if (Double.IsNaN(used) || Double.IsInfinity(used)) return null;
        int mins = Convert.ToInt32(Json.Get(d, "windowDurationMins") ?? 0);
        string name = mins == 10080 ? "Weekly" : mins == 300 ? "5-hour" : mins > 0 && mins % 1440 == 0 ? (mins / 1440) + "-day" : mins > 0 && mins % 60 == 0 ? (mins / 60) + "-hour" : mins > 0 ? mins + "-minute" : key;
        var w = new UsageWindow { Key = key, Name = name, Minutes = mins, Remaining = (int)Math.Floor(Math.Max(0, Math.Min(100, 100 - used))) };
        if (Json.Get(d, "resetsAt") != null) {
            try { w.Reset = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Json.Get(d, "resetsAt"))); } catch { }
        }
        return w;
    }
    public string Description {
        get { return Name + ": " + Remaining + "% left" + (Reset.HasValue ? "  |  resets " + Reset.Value.LocalDateTime.ToString("ddd, MMM d 'at' HH:mm") : ""); }
    }
}

static class Badge {
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    public static Bitmap Draw(string text, bool locked, bool stale) {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            int n; bool numeric = Int32.TryParse(text, out n);
            var accent = stale ? Color.FromArgb(250, 185, 55) : numeric && n <= 10 ? Color.FromArgb(255, 108, 108) : numeric && n <= 25 ? Color.FromArgb(250, 185, 55) : Color.FromArgb(117, 233, 190);
            using (var bg = new SolidBrush(Color.FromArgb(28, 33, 40))) g.FillRectangle(bg, 0, 0, 32, 32);
            using (var pen = new Pen(locked ? Color.FromArgb(173, 183, 196) : accent, 2)) g.DrawRectangle(pen, 1, 1, 30, 30);
            if (locked) {
                using (var pen = new Pen(Color.White, 3)) g.DrawArc(pen, 10, 6, 12, 16, 180, 180);
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, 7, 14, 18, 13);
                using (var b = new SolidBrush(Color.FromArgb(28, 33, 40))) g.FillRectangle(b, 15, 18, 2, 5);
            } else {
                using (var font = new Font("Segoe UI", text.Length >= 3 ? 15 : 22, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(stale ? accent : Color.White))
                using (var format = new StringFormat(StringFormat.GenericTypographic)) {
                    format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    SizeF size = g.MeasureString(text, font, 100, format);
                    g.DrawString(text, font, brush, new PointF((32 - size.Width) / 2, (32 - size.Height) / 2), format);
                }
            }
        }
        return bmp;
    }
    public static Icon Make(string text, bool locked, bool stale) {
        using (var bmp = Draw(text, locked, stale)) {
            IntPtr handle = bmp.GetHicon();
            try { using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
        }
    }
}

sealed class Tray : ApplicationContext {
    // .NET Framework exposes this only internally. Use the exact same path as
    // NotifyIcon's right-click: it sets the tray owner foreground and shows the
    // menu as a tray menu, without the generic popup's extra taskbar window.
    static readonly System.Reflection.MethodInfo showTrayMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    readonly Control dispatcher = new Control();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
    readonly EventWaitHandle quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\GPTUsageTray.Quit.v1");
    readonly EventWaitHandle refreshSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\GPTUsageTray.Refresh.v1");
    AppServer server;
    Icon currentIcon;
    bool closing, busy, stale, signedOut, authenticated, loginPending;
    int failures;
    string accountType = "", identity = "", plan = "", message = "Connecting to Codex...", selected = "lowest", loginId;
    DateTime updated = DateTime.MinValue, nextRefresh = DateTime.MinValue, loginStarted;
    List<UsageWindow> windows = new List<UsageWindow>();
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPTUsageTray");
    public Tray() {
        dispatcher.CreateControl();
        Directory.CreateDirectory(dataDir);
        try { selected = File.ReadAllText(Path.Combine(dataDir, "display.txt")).Trim(); } catch { }
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) showTrayMenu.Invoke(tray, null); };
        menu.Opening += delegate { BuildMenu(); };
        timer.Interval = 1000;
        timer.Tick += async delegate {
            if (quitSignal.WaitOne(0)) { ExitThread(); return; }
            if (refreshSignal.WaitOne(0)) nextRefresh = DateTime.MinValue;
            if (loginPending && DateTime.UtcNow - loginStarted > TimeSpan.FromMinutes(10)) { await CancelLogin(); }
            if (!busy && DateTime.UtcNow >= nextRefresh) await Refresh();
        };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        Render();
        tray.Visible = true;
        timer.Start();
    }
    void Ui(Action action) { try { if (!closing && !dispatcher.IsDisposed) dispatcher.BeginInvoke(action); } catch { } }
    void PowerChanged(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Ui(delegate { nextRefresh = DateTime.MinValue; }); }
    void PreferenceChanged(object s, UserPreferenceChangedEventArgs e) { Ui(Render); }
    async Task EnsureServer() {
        if (server != null && server.Alive) return;
        DropServer();
        var fresh = new AppServer();
        server = fresh;
        fresh.Notification += (method, args) => Ui(delegate {
            if (server != fresh) return;
            if (method == "account/login/completed") {
                loginPending = false; loginId = null;
                bool success = Object.Equals(Json.Get(args, "success"), true);
                message = success ? "Signed in. Reading usage..." : "Sign-in did not complete. Try Authenticate again.";
                nextRefresh = DateTime.MinValue;
                Render();
            } else if (method == "account/updated") { nextRefresh = DateTime.MinValue; }
        });
        await fresh.Start();
    }
    void DropServer() { if (server != null) { server.Dispose(); server = null; } loginPending = false; loginId = null; }
    public async Task Refresh() {
        if (!await operation.WaitAsync(0)) return;
        busy = true;
        try {
            await EnsureServer();
            var response = await server.Call("account/read", new { refreshToken = false });
            var account = Json.Obj(response, "account");
            if (account == null) {
                signedOut = true; authenticated = false; stale = false; windows.Clear(); identity = ""; plan = "";
                message = loginPending ? "Finish signing in in your browser." : "Not authenticated";
            } else {
                string newIdentity = Json.Str(account, "email");
                if (identity != newIdentity) { windows.Clear(); updated = DateTime.MinValue; }
                identity = newIdentity; accountType = Json.Str(account, "type"); plan = Json.Str(account, "planType");
                signedOut = false;
                if (accountType == "apiKey") {
                    authenticated = true; windows.Clear(); stale = false;
                    message = "API key connected. Authenticate with ChatGPT to read subscription limits.";
                } else {
                    var usage = await server.Call("account/rateLimits/read", new {});
                    var limits = Json.Obj(usage, "rateLimits");
                    var byId = Json.Obj(usage, "rateLimitsByLimitId");
                    if (Json.Obj(byId, "codex") != null) limits = Json.Obj(byId, "codex");
                    windows = new [] { UsageWindow.Read(Json.Obj(limits, "primary"), "primary"), UsageWindow.Read(Json.Obj(limits, "secondary"), "secondary") }.Where(w => w != null).ToList();
                    authenticated = true; stale = false; updated = DateTime.UtcNow;
                    message = windows.Count > 0 ? "Usage is up to date" : "Authenticated; no percentage limit was returned.";
                }
            }
            failures = 0;
            nextRefresh = DateTime.UtcNow.AddSeconds(loginPending ? 5 : 60);
        } catch (Exception ex) {
            HandleFailure(ex);
        } finally { busy = false; operation.Release(); if (!closing) Render(); }
    }
    internal static bool IsAuthFailure(Exception ex) {
        // Only explicit credential failures change auth state; network errors never sign the user out.
        string s = ex.Message.ToLowerInvariant();
        return ex is RpcException && (s.Contains("401") || s.Contains("unauthorized") || s.Contains("refresh_token_reused") || s.Contains("refresh_token_expired") || s.Contains("invalid_grant") || s.Contains("not authenticated") || s.Contains("authentication required") || s.Contains("please log in") || s.Contains("please sign in"));
    }
    void HandleFailure(Exception ex) {
        failures++;
        if (IsAuthFailure(ex)) {
            authenticated = false; signedOut = true; windows.Clear(); stale = false;
            message = "Sign-in expired. Choose Authenticate with ChatGPT.";
        } else {
            stale = true;
            message = ex is IOException && ex.Message.Contains("not found") ? ex.Message : "Usage refresh failed. Retrying automatically.";
        }
        // Restart only our helper, never the user's Codex app. Keep the last successful reading.
        if (!loginPending) DropServer();
        nextRefresh = DateTime.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(failures - 1, 5))));
    }
    async Task Authenticate() {
        if (!await operation.WaitAsync(0)) return;
        busy = true;
        try {
            await EnsureServer();
            var login = await server.Call("account/login/start", new { type = "chatgpt" });
            Uri url;
            if (!Uri.TryCreate(Json.Str(login, "authUrl"), UriKind.Absolute, out url) || url.Scheme != "https" || !(url.Host == "auth.openai.com" || url.Host == "chatgpt.com" || url.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase))) throw new IOException("Unexpected sign-in URL.");
            loginId = Json.Str(login, "loginId"); loginPending = true; loginStarted = DateTime.UtcNow;
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            message = "Finish signing in in your browser.";
            nextRefresh = DateTime.UtcNow.AddSeconds(5);
        } catch (Exception ex) {
            HandleFailure(ex);
            message = "Could not start sign-in. Choose Authenticate to retry.";
            tray.ShowBalloonTip(5000, "GPT Usage Tray", message, ToolTipIcon.Warning);
        } finally { busy = false; operation.Release(); if (!closing) Render(); }
    }
    async Task CancelLogin() {
        if (!await operation.WaitAsync(0)) return;
        busy = true;
        try { if (server != null && server.Alive && loginId != null) await server.Call("account/login/cancel", new { loginId = loginId }); }
        catch { DropServer(); }
        finally { loginPending = false; loginId = null; busy = false; operation.Release(); nextRefresh = DateTime.MinValue; Render(); }
    }
    UsageWindow Chosen() { return windows.FirstOrDefault(w => w.Key == selected) ?? windows.OrderBy(w => w.Remaining).FirstOrDefault(); }
    string AuthLabel() { return signedOut ? "Not authenticated" : authenticated ? "Authenticated" + (stale ? " (last verified)" : "") : "Authentication: checking..."; }
    void Render() {
        if (closing) return;
        var chosen = Chosen();
        string text = chosen != null ? chosen.Remaining.ToString() : stale ? "!" : signedOut ? "" : "...";
        var icon = Badge.Make(text, signedOut, stale);
        tray.Icon = icon;
        if (currentIcon != null) currentIcon.Dispose();
        currentIcon = icon;
        string tip = "GPT Usage: " + (chosen != null ? chosen.Remaining + "% left (" + chosen.Name + ")" : AuthLabel()) + (stale ? " - stale" : "");
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        try {
            File.WriteAllText(Path.Combine(dataDir, "status.json"), Json.Stringify(new {
                authenticated = authenticated, signedOut = signedOut, stale = stale, remaining = chosen == null ? (int?)null : chosen.Remaining,
                window = chosen == null ? null : chosen.Name, message = message, lastUpdatedUtc = updated == DateTime.MinValue ? null : updated.ToString("o"),
                nextRefreshUtc = nextRefresh.ToString("o"), helperPid = server == null ? 0 : server.ProcessId, appPid = Process.GetCurrentProcess().Id,
                loginPending = loginPending
            }));
        } catch { }
    }
    ToolStripMenuItem Label(string text, bool bold) {
        var item = new ToolStripMenuItem(text) { Enabled = false };
        if (bold) item.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(item); return item;
    }
    void BuildMenu() {
        foreach (ToolStripItem item in menu.Items.Cast<ToolStripItem>().ToArray()) { menu.Items.Remove(item); item.Dispose(); }
        menu.ShowImageMargin = false;
        Label("GPT Usage Tray", true);
        Label(AuthLabel(), false);
        if (!String.IsNullOrEmpty(identity) && !signedOut) Label(identity + (String.IsNullOrEmpty(plan) ? "" : "  |  " + plan), false);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var window in windows) Label(window.Description, false);
        Label(message, false);
        if (updated != DateTime.MinValue && windows.Count > 0) Label("Last updated " + updated.ToLocalTime().ToString("HH:mm:ss") + (stale ? "  (last known reading)" : ""), false);
        if (windows.Count > 1) {
            var display = new ToolStripMenuItem("Percentage in tray");
            var lowest = new ToolStripMenuItem("Lowest remaining") { Checked = selected == "lowest" };
            lowest.Click += delegate { SelectWindow("lowest"); }; display.DropDownItems.Add(lowest);
            foreach (var w in windows) {
                string key = w.Key;
                var item = new ToolStripMenuItem(w.Name) { Checked = selected == key };
                item.Click += delegate { SelectWindow(key); }; display.DropDownItems.Add(item);
            }
            menu.Items.Add(display);
        }
        menu.Items.Add(new ToolStripSeparator());
        var auth = new ToolStripMenuItem(loginPending ? "Waiting for browser sign-in..." : "Authenticate with ChatGPT...") { Enabled = !busy && !loginPending };
        auth.Click += async delegate { await Authenticate(); }; menu.Items.Add(auth);
        if (loginPending) { var cancel = new ToolStripMenuItem("Cancel sign-in"); cancel.Click += async delegate { await CancelLogin(); }; menu.Items.Add(cancel); }
        var refresh = new ToolStripMenuItem(busy ? "Refreshing..." : "Refresh now") { Enabled = !busy };
        refresh.Click += async delegate { await Refresh(); }; menu.Items.Add(refresh);
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled() };
        startup.Click += delegate { try { SetStartup(!StartupEnabled()); } catch { MessageBox.Show("Windows could not update the startup setting.", "GPT Usage Tray"); } }; menu.Items.Add(startup);
        var settings = new ToolStripMenuItem("Windows tray visibility settings");
        settings.Click += delegate { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }; menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Quit"); quit.Click += delegate { ExitThread(); }; menu.Items.Add(quit);
    }
    void SelectWindow(string key) { selected = key; try { File.WriteAllText(Path.Combine(dataDir, "display.txt"), key); } catch { } Render(); }
    static bool StartupEnabled() { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue("GPTUsageTray") != null; }
    static void SetStartup(bool enabled) {
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
            if (enabled) key.SetValue("GPTUsageTray", "\"" + Application.ExecutablePath + "\""); else key.DeleteValue("GPTUsageTray", false);
        }
    }
    protected override void ExitThreadCore() {
        closing = true; timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose();
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        DropServer(); menu.Dispose(); quitSignal.Dispose(); refreshSignal.Dispose(); if (currentIcon != null) currentIcon.Dispose(); dispatcher.Dispose(); base.ExitThreadCore();
    }
}

static class Program {
    [STAThread] static void Main(string[] args) {
        if (args.Contains("--quit")) { try { using (var signal = EventWaitHandle.OpenExisting(@"Local\GPTUsageTray.Quit.v1")) signal.Set(); } catch (WaitHandleCannotBeOpenedException) {} return; }
        if (args.Contains("--refresh")) { try { using (var signal = EventWaitHandle.OpenExisting(@"Local\GPTUsageTray.Refresh.v1")) signal.Set(); } catch (WaitHandleCannotBeOpenedException) {} return; }
        if (args.Contains("--self-test")) { SelfTest(); return; }
        if (args.Contains("--smoke-test")) { SmokeTest().GetAwaiter().GetResult(); return; }
        bool first;
        using (var mutex = new Mutex(true, @"Local\GPTUsageTray.Rafael.v1", out first)) {
            if (!first) return;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Tray());
        }
    }
    static void Assert(bool b, string name) { if (!b) throw new Exception("Test failed: " + name); }
    static void SelfTest() {
        var report = new List<string>();
        var weekly = UsageWindow.Read(Json.Parse("{\"usedPercent\":43,\"windowDurationMins\":10080,\"resetsAt\":1789483807}"), "primary");
        Assert(weekly.Remaining == 57 && weekly.Name == "Weekly" && weekly.Reset.HasValue, "weekly primary"); report.Add("PASS weekly primary maps to 57% remaining");
        Assert(UsageWindow.Read(null, "secondary") == null, "missing window");
        Assert(UsageWindow.Read(Json.Parse("{\"usedPercent\":100.5}"), "primary").Remaining == 0, "lower clamp");
        Assert(UsageWindow.Read(Json.Parse("{\"usedPercent\":-5}"), "primary").Remaining == 100, "upper clamp");
        Assert(UsageWindow.Read(Json.Parse("{\"usedPercent\":43.9}"), "primary").Remaining == 56, "conservative rounding");
        Assert(UsageWindow.Read(Json.Parse("{\"windowDurationMins\":300}"), "primary") == null, "unknown usage"); report.Add("PASS missing windows, percentage bounds, fractional rounding");
        Assert(!Tray.IsAuthFailure(new IOException("Network unavailable")), "network isn't logout");
        Assert(!Tray.IsAuthFailure(new RpcException("HTTP 503 service unavailable")), "503 isn't logout");
        Assert(Tray.IsAuthFailure(new RpcException("401 Unauthorized")), "401 is auth failure"); report.Add("PASS authentication errors distinguished from network failures");
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        using (var sheet = new Bitmap(480, 170)) using (var g = Graphics.FromImage(sheet)) using (var font = new Font("Segoe UI", 10)) {
            g.Clear(Color.FromArgb(241, 243, 245));
            string[] labels = { "57", "100", "9", "0", "lock", "stale", "!" };
            for (int i = 0; i < labels.Length; i++) {
                using (var bmp = Badge.Draw(labels[i] == "stale" ? "57" : labels[i], labels[i] == "lock", labels[i] == "stale" || labels[i] == "!")) {
                    g.DrawImage(bmp, i * 68 + 15, 20, 32, 32); g.DrawImage(bmp, i * 68 + 23, 74, 16, 16);
                }
                g.DrawString(labels[i], font, Brushes.Black, i * 68 + 16, 111);
            }
            sheet.Save(Path.Combine(dir, "icon-preview.png"));
        }
        report.Add("PASS rendered number, lock, stale and unavailable icons at 16px and 32px");
        File.WriteAllLines(Path.Combine(dir, "self-test.txt"), report);
    }
    static async Task SmokeTest() {
        using (var rpc = new AppServer()) {
            await rpc.Start();
            var account = await rpc.Call("account/read", new { refreshToken = false });
            var a = Json.Obj(account, "account");
            var usage = a == null ? null : await rpc.Call("account/rateLimits/read", new {});
            var limits = Json.Obj(usage, "rateLimits");
            var w = UsageWindow.Read(Json.Obj(limits, "primary"), "primary");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-test.json"), Json.Stringify(new { initialized = true, authenticated = a != null, window = w == null ? null : w.Name, remaining = w == null ? (int?)null : w.Remaining }));
        }
    }
}
}
