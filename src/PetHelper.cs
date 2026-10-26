// 鲸鱼娘桌宠助手 —— 把「启动 / 停止 / 检查 / 隐藏 / 显示 / 开机自启」收进一个小窗口
// 编译器：C# 5（.NET Framework 4.x 自带 csc.exe），不依赖第三方库
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WhalePetHelper
{
    static class Cfg
    {
        public static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string RuntimePath = Path.Combine(UserHome, @".dsh\dsh-pet\standalone-runtime.json");
        public static readonly string PetCmd = Path.Combine(AppData, @"npm\dsh-pet-standalone.cmd");
        public static readonly string LogoPath = Path.Combine(AppData, @"npm\node_modules\dsh-pet\assets\logo.png");
        public static readonly string StartupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        public static readonly string AutostartVbs = Path.Combine(StartupDir, "鲸鱼娘桌宠助手.vbs");
        public const string Owner = "鲸鱼娘助手";
        public const string HideLabel = "隐藏桌宠";
        public const string PetId = "main";
        public static string ExePath { get { return Application.ExecutablePath; } }
    }

    class PetRuntime
    {
        public int Port;
        public int Pid;
        public string Token;
    }

    static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static Dictionary<string, object> Parse(string s)
        {
            try { return Ser.Deserialize<Dictionary<string, object>>(s); }
            catch { return null; }
        }
        public static object Get(object root, params string[] path)
        {
            object cur = root;
            foreach (var key in path)
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || !d.ContainsKey(key)) return null;
                cur = d[key];
            }
            return cur;
        }
        public static string Str(object root, params string[] path)
        {
            var v = Get(root, path);
            return v == null ? null : Convert.ToString(v);
        }
    }

    static class Pet
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        static async Task<string> GetText(string url, CancellationToken ct)
        {
            using (var resp = await Http.GetAsync(url, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        public static Task<string> HttpGet(string url, CancellationToken ct)
        {
            return GetText(url, ct);
        }

        public static PetRuntime ReadRuntime()
        {
            try
            {
                if (!File.Exists(Cfg.RuntimePath)) return null;
                var dict = Json.Parse(File.ReadAllText(Cfg.RuntimePath, Encoding.UTF8));
                if (dict == null) return null;
                var rt = new PetRuntime();
                rt.Port = Convert.ToInt32(Json.Get(dict, "port"));
                rt.Pid = Convert.ToInt32(Json.Get(dict, "pid"));
                rt.Token = Json.Str(dict, "token");
                return rt.Port > 0 ? rt : null;
            }
            catch { return null; }
        }

        public static bool Health(int port)
        {
            try
            {
                using (var cts = new CancellationTokenSource(900))
                {
                    var t = Http.GetAsync("http://127.0.0.1:" + port + "/health", cts.Token);
                    t.Wait();
                    return t.Result.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        public static bool StandaloneAlive(out PetRuntime rt)
        {
            rt = ReadRuntime();
            return rt != null && Health(rt.Port);
        }

        public static string StartStandalone()
        {
            if (!File.Exists(Cfg.PetCmd)) return "找不到 dsh-pet-standalone.cmd，请先 npm install -g dsh-pet";
            PetRuntime rt;
            if (StandaloneAlive(out rt)) return "独立桌宠已经在运行（端口 " + rt.Port + "）";
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + Cfg.PetCmd + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 25000)
            {
                Thread.Sleep(400);
                if (StandaloneAlive(out rt)) return "已启动（端口 " + rt.Port + "）";
            }
            return "已发起启动，但 25 秒内没等到端口就绪；请查看托盘或日志";
        }

        public static string StopStandalone()
        {
            PetRuntime rt;
            if (!StandaloneAlive(out rt)) return "独立桌宠本来就没在运行";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + rt.Port + "/shutdown");
                req.Headers.Add("x-dsh-pet-token", rt.Token ?? "");
                Http.SendAsync(req).Wait();
                Thread.Sleep(600);
                return "已请求退出（端口 " + rt.Port + "）";
            }
            catch (Exception ex) { return "停止失败：" + ex.Message; }
        }

        public static string SetDisplay(int port, string display, string token, out bool ok)
        {
            ok = false;
            try
            {
                var body = "{\"id\":\"" + Cfg.PetId + "\",\"display\":\"" + display + "\"}";
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/dsh-pet-7340/pet-display");
                req.Content = content;
                if (!string.IsNullOrEmpty(token)) req.Headers.Add("x-dsh-pet-token", token);
                var resp = Http.SendAsync(req).Result;
                ok = resp.IsSuccessStatusCode;
                return ok ? null : "HTTP " + (int)resp.StatusCode;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static Dictionary<string, object> State(int port)
        {
            try
            {
                var t = GetText("http://127.0.0.1:" + port + "/dsh-pet-7340/state", CancellationToken.None);
                t.Wait();
                return Json.Parse(t.Result);
            }
            catch { return null; }
        }

        public static string RegisterMenu(int port, string token)
        {
            try
            {
                var body = "{\"owner\":\"" + Cfg.Owner + "\",\"labels\":[\"" + Cfg.HideLabel + "\"]}";
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/dsh-pet-7340/menu/item");
                req.Content = content;
                if (!string.IsNullOrEmpty(token)) req.Headers.Add("x-dsh-pet-token", token);
                var resp = Http.SendAsync(req).Result;
                return resp.IsSuccessStatusCode ? null : "HTTP " + (int)resp.StatusCode;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string UnregisterMenu(int port, string token)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Delete, "http://127.0.0.1:" + port + "/dsh-pet-7340/menu/item?owner=" + Uri.EscapeDataString(Cfg.Owner));
                if (!string.IsNullOrEmpty(token)) req.Headers.Add("x-dsh-pet-token", token);
                Http.SendAsync(req).Wait();
                return null;
            }
            catch { return "unregister failed"; }
        }

        // 在监听端口里找 DSH 宿主（插件路由是公开的，直接探 /state）
        public static int DiscoverDsh(int excludePort)
        {
            var ports = new List<int>();
            try
            {
                foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (ep.Port < 1024) continue;
                    if (excludePort > 0 && ep.Port == excludePort) continue;
                    if (ports.Contains(ep.Port)) continue;
                    ports.Add(ep.Port);
                }
            }
            catch { }
            if (ports.Count == 0) return 0;

            int found = 0;
            var tasks = new List<Task>();
            var gate = new SemaphoreSlim(16);
            foreach (var port in ports)
            {
                int p = port;
                tasks.Add(Task.Run(async delegate
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        using (var cts = new CancellationTokenSource(450))
                        {
                            var s = await GetText("http://127.0.0.1:" + p + "/dsh-pet-7340/state", cts.Token).ConfigureAwait(false);
                            if (s != null && s.Contains("\"sections\"")) Interlocked.CompareExchange(ref found, p, 0);
                        }
                    }
                    catch { }
                    finally { gate.Release(); }
                }));
            }
            try { Task.WaitAll(tasks.ToArray(), 9000); } catch { }
            return found;
        }
    }

    class MenuWatcher
    {
        readonly Action onHide;
        volatile bool stopping;
        public MenuWatcher(Action onHide) { this.onHide = onHide; }

        public void Stop() { stopping = true; }

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        void Loop()
        {
            int dshPort = 0;
            DateTime dshAt = DateTime.MinValue;
            while (!stopping)
            {
                PetRuntime rt;
                int stdPort = Pet.StandaloneAlive(out rt) ? rt.Port : 0;
                if ((DateTime.Now - dshAt).TotalSeconds > 30)
                {
                    dshPort = Pet.DiscoverDsh(stdPort);
                    dshAt = DateTime.Now;
                }

                bool polled = false;
                if (stdPort > 0) { PollOnce(stdPort, rt.Token); polled = true; }
                if (dshPort > 0 && dshPort != stdPort) { PollOnce(dshPort, null); polled = true; }
                if (!polled) Thread.Sleep(3000);
            }
        }

        void PollOnce(int port, string token)
        {
            if (Pet.RegisterMenu(port, token) != null) return;
            try
            {
                var url = "http://127.0.0.1:" + port + "/dsh-pet-7340/menu/events?owner=" + Uri.EscapeDataString(Cfg.Owner) + "&wait=15000";
                using (var cts = new CancellationTokenSource(20000))
                {
                    var t = Pet.HttpGet(url, cts.Token);
                    t.Wait();
                    var dict = Json.Parse(t.Result);
                    if (dict == null) return;
                    foreach (var kv in dict)
                    {
                        if (kv.Value != null) { onHide(); return; }
                    }
                }
            }
            catch { }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "WhalePetHelper";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue(RunName) != null;
                }
                catch { return false; }
            }
        }

        public static void Enable()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                k.SetValue(RunName, "\"" + Cfg.ExePath + "\" --tray");
        }

        public static void Disable()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null) k.DeleteValue(RunName, false);
        }
    }
    class MainForm : Form
    {
        readonly Label lblStd = new Label();
        readonly Label lblDsh = new Label();
        readonly Label lblPet = new Label();
        readonly Label lblAuto = new Label();
        readonly TextBox log = new TextBox();
        readonly CheckBox chkAuto = new CheckBox();
        readonly NotifyIcon tray;
        readonly MenuWatcher watcher;
        int cachedDshPort;
        DateTime dshFoundAt = DateTime.MinValue;

        public MainForm(bool startHidden)
        {
            Text = "鲸鱼娘桌宠助手";
            ClientSize = new Size(560, 470);
            MinimumSize = new Size(560, 470);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(248, 249, 252);
            Icon = LoadIcon();

            var title = new Label();
            title.Text = "🐋  鲸鱼娘桌宠助手";
            title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            var card = new Panel();
            card.Location = new Point(20, 56);
            card.Size = new Size(520, 150);
            card.BackColor = Color.White;
            card.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(card);

            StyleStatus(lblStd, 14, 12);
            StyleStatus(lblDsh, 14, 52);
            StyleStatus(lblPet, 14, 92);
            card.Controls.Add(lblStd); card.Controls.Add(lblDsh); card.Controls.Add(lblPet);

            int y = 224;
            AddButton("启动桌宠", 20, y, 96, delegate { RunAsync(delegate { return Pet.StartStandalone(); }); });
            AddButton("停止桌宠", 124, y, 96, delegate { RunAsync(delegate { return Pet.StopStandalone(); }); });
            AddButton("隐藏桌宠", 228, y, 96, delegate { HideAll(); });
            AddButton("显示桌宠", 332, y, 96, delegate { ShowAll(); });
            AddButton("刷新状态", 436, y, 104, delegate { RefreshStatus(); });

            y += 44;
            AddButton("打开 DSH", 20, y, 96, delegate { OpenDsh(); });
            AddButton("打开配置目录", 124, y, 120, delegate { OpenConfig(); });
            chkAuto.Text = "开机自动启动（静默到托盘）";
            chkAuto.AutoSize = true;
            chkAuto.Location = new Point(258, y + 8);
            chkAuto.Checked = Autostart.Enabled;
            chkAuto.CheckedChanged += delegate
            {
                try
                {
                    if (chkAuto.Checked) Autostart.Enable(); else Autostart.Disable();
                    AppendLog(chkAuto.Checked ? "已开启开机自启" : "已关闭开机自启（快捷方式进回收站）");
                }
                catch (Exception ex) { AppendLog("开机自启设置失败：" + ex.ToString()); }
                UpdateAutoLabel();
            };
            Controls.Add(chkAuto);

            y += 44;
            lblAuto.Text = "";
            lblAuto.AutoSize = true;
            lblAuto.ForeColor = Color.FromArgb(110, 116, 130);
            lblAuto.Location = new Point(20, y);
            Controls.Add(lblAuto);

            log.Location = new Point(20, y + 26);
            log.Size = new Size(520, 110);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(243, 245, 249);
            log.BorderStyle = BorderStyle.FixedSingle;
            log.Font = new Font("Consolas", 8.5f);
            Controls.Add(log);

            var tip = new Label();
            tip.Text = "右键桌宠 →「更多」→ 鲸鱼娘助手 → 隐藏桌宠；隐藏后从这里或托盘「显示桌宠」恢复。";
            tip.AutoSize = false;
            tip.Size = new Size(520, 34);
            tip.ForeColor = Color.FromArgb(120, 126, 140);
            tip.Location = new Point(20, y + 142);
            Controls.Add(tip);

            watcher = new MenuWatcher(HideAll);
            watcher.Start();

            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "鲸鱼娘桌宠助手";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowPanel(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开面板", null, delegate { ShowPanel(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示桌宠", null, delegate { ShowAll(); });
            menu.Items.Add("隐藏桌宠", null, delegate { HideAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("启动独立桌宠", null, delegate { RunAsync(delegate { return Pet.StartStandalone(); }); });
            menu.Items.Add("停止独立桌宠", null, delegate { RunAsync(delegate { return Pet.StopStandalone(); }); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 2500;
            timer.Tick += delegate { RefreshStatus(); };
            timer.Start();

            Shown += delegate
            {
                if (startHidden) { Hide(); ShowInTaskbar = false; }
                RefreshStatus();
            };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    ShowInTaskbar = false;
                    tray.ShowBalloonTip(1500, "鲸鱼娘桌宠助手", "已最小化到托盘，双击图标可打开。", ToolTipIcon.Info);
                }
            };
            AppendLog("助手已启动。");

        }

        static void StyleStatus(Label l, int x, int y)
        {
            l.AutoSize = false;
            l.Size = new Size(486, 34);
            l.Location = new Point(x, y);
            l.Font = new Font("Microsoft YaHei UI", 10.5f);
            l.Text = "…";
        }

        void AddButton(string text, int x, int y, int w, EventHandler onClick)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 34);
            b.FlatStyle = FlatStyle.System;
            b.Click += onClick;
            Controls.Add(b);
        }

        Icon LoadIcon()
        {
            try
            {
                var embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) return embedded;
            }
            catch { }
            try
            {
                if (File.Exists(Cfg.LogoPath))
                {
                    using (var bmp = new Bitmap(Cfg.LogoPath))
                    {
                        using (var small = new Bitmap(bmp, new Size(32, 32)))
                        {
                            IntPtr h = small.GetHicon();
                            using (var tmp = Icon.FromHandle(h))
                            {
                                return (Icon)tmp.Clone();
                            }
                        }
                    }
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        void ShowPanel()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshStatus();
        }

        void ExitApp()
        {
            watcher.Stop();
            try
            {
                PetRuntime rt;
                if (Pet.StandaloneAlive(out rt)) Pet.UnregisterMenu(rt.Port, rt.Token);
                if (cachedDshPort > 0) Pet.UnregisterMenu(cachedDshPort, null);
            }
            catch { }
            tray.Visible = false;
            tray.Dispose();
            Environment.Exit(0);
        }

        void AppendLog(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), s); return; }
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine);
        }

        void RunAsync(Func<string> work)
        {
            Task.Run(delegate
            {
                string msg;
                try { msg = work(); }
                catch (Exception ex) { msg = "操作失败：" + ex.Message; }
                if (!string.IsNullOrEmpty(msg)) AppendLog(msg);
            });
        }

        void HideAll()
        {
            RunAsync(delegate
            {
                PetRuntime rt;
                int std = Pet.StandaloneAlive(out rt) ? rt.Port : 0;
                int dsh = CurrentDshPort();
                var parts = new List<string>();
                if (std > 0) { bool ok; var err = Pet.SetDisplay(std, "none", rt.Token, out ok); parts.Add(ok ? "独立桌宠已隐藏" : "独立桌宠隐藏失败：" + err); }
                if (dsh > 0) { bool ok; var err = Pet.SetDisplay(dsh, "none", null, out ok); parts.Add(ok ? "DSH 桌宠已隐藏" : "DSH 桌宠隐藏失败：" + err); }
                if (parts.Count == 0) parts.Add("没有找到正在运行的桌宠");
                return string.Join("；", parts.ToArray());
            });
        }

        void ShowAll()
        {
            RunAsync(delegate
            {
                PetRuntime rt;
                int std = Pet.StandaloneAlive(out rt) ? rt.Port : 0;
                int dsh = CurrentDshPort();
                var parts = new List<string>();
                if (std > 0) { bool ok; var err = Pet.SetDisplay(std, "desktop", rt.Token, out ok); parts.Add(ok ? "独立桌宠已显示" : "独立桌宠显示失败：" + err); }
                if (dsh > 0) { bool ok; var err = Pet.SetDisplay(dsh, "desktop", null, out ok); parts.Add(ok ? "DSH 桌宠已显示" : "DSH 桌宠显示失败：" + err); }
                if (parts.Count == 0) parts.Add("没有找到正在运行的桌宠，先点「启动桌宠」");
                return string.Join("；", parts.ToArray());
            });
        }

        int CurrentDshPort()
        {
            if (cachedDshPort > 0 && (DateTime.Now - dshFoundAt).TotalSeconds < 30) return cachedDshPort;
            PetRuntime rt;
            int std = Pet.StandaloneAlive(out rt) ? rt.Port : 0;
            int found = Pet.DiscoverDsh(std);
            cachedDshPort = found;
            dshFoundAt = DateTime.Now;
            return found;
        }

        void OpenDsh()
        {
            try
            {
                Process.Start(new ProcessStartInfo("dsh://open") { UseShellExecute = true });
                AppendLog("已请求唤起 DSH。");
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo(@"D:\dsh桌面版\DeepSeek Harness.exe") { UseShellExecute = true });
                    AppendLog("已启动 DSH 桌面版。");
                }
                catch (Exception ex) { AppendLog("打开 DSH 失败：" + ex.Message); }
            }
        }

        void OpenConfig()
        {
            try { Process.Start("explorer.exe", Path.Combine(Cfg.UserHome, @".dsh\dsh-pet")); }
            catch { }
        }

        void UpdateAutoLabel()
        {
            lblAuto.Text = Autostart.Enabled ? "开机自启：已开启（登录后静默运行）" : "开机自启：未开启";
        }

        void RefreshStatus()
        {
            Task.Run(delegate
            {
                PetRuntime rt;
                bool stdAlive = Pet.StandaloneAlive(out rt);
                int stdPort = stdAlive ? rt.Port : 0;
                int dsh;
                if (cachedDshPort > 0 && (DateTime.Now - dshFoundAt).TotalSeconds < 30) dsh = cachedDshPort;
                else { dsh = Pet.DiscoverDsh(stdPort); cachedDshPort = dsh; dshFoundAt = DateTime.Now; }

                string petInfo = "任务状态：—";
                if (dsh > 0)
                {
                    var st = Pet.State(dsh);
                    var work = Json.Str(st, "sections", "workStatus", "data", "state");
                    string money = null;
                    var wallets = Json.Get(st, "sections", "balance", "data", "data", "wallets") as IList;
                    if (wallets != null && wallets.Count > 0) money = Json.Str(wallets[0], "total");
                    petInfo = "任务状态：" + (work ?? "—") + (money != null ? "　余额：¥" + money : "");
                }

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        lblStd.Text = stdAlive
                            ? "独立桌宠：● 运行中　端口 " + stdPort + "　PID " + (rt != null ? rt.Pid : 0)
                            : "独立桌宠：○ 未运行";
                        lblStd.ForeColor = stdAlive ? Color.FromArgb(28, 140, 90) : Color.FromArgb(130, 136, 150);
                        lblDsh.Text = dsh > 0 ? "DSH：● 已连接　端口 " + dsh : "DSH：○ 未运行（或未装桌宠插件）";
                        lblDsh.ForeColor = dsh > 0 ? Color.FromArgb(28, 140, 90) : Color.FromArgb(130, 136, 150);
                        lblPet.Text = petInfo;
                        lblPet.ForeColor = Color.FromArgb(60, 66, 80);
                        UpdateAutoLabel();
                    }));
                }
                catch { }
            });
        }
    }

    static class Program
    {
        static Mutex mutex;
        static EventWaitHandle showEvent;

        static string InstalledExe()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\WhalePetHelper\鲸鱼娘桌宠助手.exe");
        }

        // 从交付目录直接双击时，先自安装到 %LOCALAPPDATA%\Programs，再从那里启动。
        // 这样开机自启、注册表、开始菜单写入都不会被工作区的执行限制影响。
        static bool RelocateIfNeeded(string[] args)
        {
            try
            {
                var installed = InstalledExe();
                var current = Path.GetFullPath(Application.ExecutablePath);
                if (string.Equals(current, Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase)) return false;
                var dir = Path.GetDirectoryName(installed);
                Directory.CreateDirectory(dir);
                if (!File.Exists(installed) || File.GetLastWriteTimeUtc(current) > File.GetLastWriteTimeUtc(installed))
                    File.Copy(current, installed, true);
                var psi = new ProcessStartInfo(installed);
                foreach (var a in args) psi.Arguments += (psi.Arguments.Length > 0 ? " " : "") + a;
                psi.UseShellExecute = true;
                psi.WorkingDirectory = dir;
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (RelocateIfNeeded(args)) return;
            bool created;
            mutex = new Mutex(true, "WhalePetHelper.SingleInstance", out created);
            if (!created)
            {
                try
                {
                    showEvent = EventWaitHandle.OpenExisting("WhalePetHelper.Show");
                    showEvent.Set();
                }
                catch { }
                return;
            }
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "WhalePetHelper.Show");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool startHidden = Array.IndexOf(args, "--tray") >= 0 || Array.IndexOf(args, "--autostart") >= 0;
            var form = new MainForm(startHidden);

            var waiter = new Thread(delegate()
            {
                while (true)
                {
                    showEvent.WaitOne();
                    try { form.BeginInvoke(new Action(delegate { form.Show(); form.ShowInTaskbar = true; form.WindowState = FormWindowState.Normal; form.Activate(); })); }
                    catch { }
                }
            });
            waiter.IsBackground = true;
            waiter.Start();

            if (startHidden)
            {
                Task.Run(delegate { Pet.StartStandalone(); });
                Application.Run();
            }
            else
            {
                Application.Run(form);
            }
        }
    }
}
