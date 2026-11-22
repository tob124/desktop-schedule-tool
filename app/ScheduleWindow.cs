using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScdDesktop
{
    internal sealed partial class ScheduleWindow : Form
    {
        private const string Origin = "https://schedule.invalid";
        private readonly string stateDirectory;
        private readonly StateStore store;
        private readonly AppState state;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Label startup = new Label();
        private readonly WebView2 browser = new WebView2();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly Timer poll = new Timer { Interval = 30000 };
        private readonly Timer debounce = new Timer { Interval = 600 };
        private readonly Timer save = new Timer { Interval = 350 };
        private readonly Timer zOrder = new Timer { Interval = 64 };
        private readonly Timer exit = new Timer { Interval = 700 };
        private readonly NativeWindow.ForegroundCallback foregroundCallback;
        private readonly uint taskbarCreated = NativeWindow.RegisterWindowMessage("TaskbarCreated");
        private readonly Dictionary<string, string> assets = new Dictionary<string, string>();

        private IntPtr foregroundHook;
        private IntPtr externalForeground;
        private IntPtr originalOwner;
        private IntPtr desktopOwner;
        private int desktopChecks;
        private bool lowerAfterInteraction;
        private bool choosingWorkbook;
        private FileSystemWatcher watcher;
        private string sourcePath;
        private WorkbookSnapshot displayed;
        private WorkbookSnapshot pending;
        private int revision;
        private bool expanded;
        private bool ready;
        private bool reading;
        private bool readAgain;
        private bool forceAgain;
        private bool changingBounds;
        private bool exitRequested;
        private bool exitConfirmed;
        private string lastNotice;

        public ScheduleWindow(string directory, string workbook)
        {
            stateDirectory = directory;
            store = new StateStore(directory);
            state = store.Load();
            sourcePath = Normalize(workbook) ?? Normalize(state.Source);
            Text = "日程表";
            Icon = SystemIcons.Application;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 252, 251);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            MinimumSize = new Size(410, 240);

            startup.Dock = DockStyle.Fill;
            startup.TextAlign = ContentAlignment.MiddleCenter;
            startup.Text = "正在打开日程表…";
            browser.Dock = DockStyle.Fill;
            browser.Visible = false;
            Controls.Add(startup);
            Controls.Add(browser);

            var toggleItem = trayMenu.Items.Add("展开挂件", null, delegate { ToggleMode(); });
            var browseItem = trayMenu.Items.Add("仅浏览", null, delegate { ToggleBrowse(); });
            trayMenu.Items.Add("设置…", null, delegate { OpenPreferences(); });
            trayMenu.Items.Add("刷新", null, delegate { RequestRead(true); });
            trayMenu.Items.Add("更换工作簿", null, delegate { ChooseWorkbook(); });
            trayMenu.Items.Add("退出", null, delegate { Close(); });
            trayMenu.Opening += delegate {
                toggleItem.Text = expanded ? "收起到桌面" : "展开挂件";
                browseItem.Text = state.Appearance.BrowseOnly ? "退出仅浏览" : "进入仅浏览";
            };
            tray.Icon = Icon;
            tray.Text = "日程表";
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowExpanded(); };
            tray.Visible = true;

            poll.Tick += delegate { RequestRead(false); };
            debounce.Tick += delegate { debounce.Stop(); RequestRead(false); };
            save.Tick += delegate { save.Stop(); SaveState(); };
            zOrder.Tick += delegate {
                bool shellActive = desktopChecks > 0 && NativeWindow.IsDesktop(NativeWindow.GetForegroundWindow());
                if (!CanPlaceOnDesktop()) { zOrder.Stop(); return; }
                desktopChecks = shellActive ? desktopChecks - 1 : 0;
                if (desktopChecks == 0) zOrder.Stop();
                bool wasHidden = NativeWindow.SetDesktopOrder(Handle, externalForeground, false);
                if (wasHidden && shellActive)
                    Send(new { type = "desktop-reveal" });
            };
            exit.Tick += delegate { FinishExit(); };
            Move += delegate { SaveWindowSoon(); };
            ResizeEnd += delegate { SaveWindowSoon(); };
            Activated += delegate { UpdateBackdropFocus(true); LowerMemoryWhenIdle(false); };
            Deactivate += delegate { UpdateBackdropFocus(false); LowerMemoryWhenIdle(true); };
            foregroundCallback = delegate(IntPtr hook, uint change, IntPtr window,
                int objectId, int childId, uint thread, uint time)
            {
                if (window == IntPtr.Zero || !IsHandleCreated) return;
                bool ownedByDesktop = HasDesktopOwner();
                if (NativeWindow.IsInWindow(window, Handle))
                {
                    lowerAfterInteraction = ownedByDesktop;
                    return;
                }
                externalForeground = window;
                if (!CanPlaceOnDesktop()) return;
                bool shellForeground = NativeWindow.IsDesktop(window);
                if (!ownedByDesktop)
                    QueueDesktopOrder(shellForeground);
                else if (lowerAfterInteraction && !shellForeground)
                {
                    lowerAfterInteraction = false;
                    NativeWindow.SetDesktopOrder(Handle, externalForeground, false);
                }
            };
        }

        protected override bool ShowWithoutActivation { get { return !expanded; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle = (parameters.ExStyle & ~NativeWindow.AppWindow) | NativeWindow.ToolWindow;
                if (titleHidden) parameters.Style &= ~NativeWindow.Caption;
                return parameters;
            }
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            originalOwner = NativeWindow.GetOwner(Handle);
            ApplyBounds();
            ApplyAppearance();
            externalForeground = NativeWindow.GetForegroundWindow();
            NativeWindow.SetDesktopWindowTraits(Handle, true);
            foregroundHook = NativeWindow.SetWinEventHook(NativeWindow.ForegroundEvent, NativeWindow.ForegroundEvent,
                IntPtr.Zero, foregroundCallback, 0, 0, 0);
            AttachToDesktop();
            try
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
                foreach (string name in new[] { "index.html", "app.js", "app.css" })
                {
                    string asset = Path.Combine(folder, name);
                    if (!File.Exists(asset)) throw new FileNotFoundException("找不到查看器资源 " + name);
                    assets.Add("/" + name, asset);
                }
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(stateDirectory, "WebView"));
                await browser.EnsureCoreWebView2Async(environment);
                if (exitRequested || IsDisposed) return;
                var web = browser.CoreWebView2;
                web.Settings.AreDevToolsEnabled = false;
                web.Settings.AreDefaultContextMenusEnabled = false;
                web.Settings.AreBrowserAcceleratorKeysEnabled = false;
                web.Settings.IsZoomControlEnabled = false;
                web.Settings.IsStatusBarEnabled = false;
                web.Settings.IsSwipeNavigationEnabled = false;
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                web.WebResourceRequested += ServeResource;
                web.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
                    { args.Cancel = args.Uri != Origin + "/index.html"; };
                web.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args)
                    { args.Handled = true; };
                web.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs args)
                    { args.Cancel = true; };
                web.WebMessageReceived += Receive;
                web.Navigate(Origin + "/index.html");
            }
            catch (Exception error)
            {
                if (!IsDisposed) startup.Text = "无法打开查看器。\r\n" + error.Message;
            }
        }

        private void ServeResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            Stream content = null;
            string media = "application/octet-stream";
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) &&
                uri.GetLeftPart(UriPartial.Authority) == Origin && e.Request.Method == "GET")
            {
                string path = uri.AbsolutePath;
                string asset;
                if (assets.TryGetValue(path, out asset))
                {
                    try { content = File.OpenRead(asset); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (path.EndsWith(".html")) media = "text/html; charset=utf-8";
                    else if (path.EndsWith(".css")) media = "text/css; charset=utf-8";
                    else media = "application/javascript; charset=utf-8";
                }
                else if (pending != null && path == "/workbook/" + pending.Revision)
                {
                    content = new MemoryStream(pending.Content, false);
                    media = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                }
            }
            string headers = "Content-Type: " + media + "\r\nCache-Control: no-store\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' blob: data:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; base-uri 'none'";
            e.Response = browser.CoreWebView2.Environment.CreateWebResourceResponse(
                content ?? Stream.Null, content == null ? 403 : 200,
                content == null ? "Forbidden" : "OK", headers);
        }

        private void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.Source != Origin + "/index.html" || e.WebMessageAsJson.Length > 1024 * 1024) return;
            BrowserMessage message;
            try { message = json.Deserialize<BrowserMessage>(e.WebMessageAsJson); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            if (message == null) return;

            switch (message.type)
            {
                case "ready":
                    if (ready) break;
                    ready = true;
                    ApplyAppearance();
                    browser.Visible = true;
                    startup.Visible = false;
                    Send(new { type = "mode", expanded = expanded });
                    ResetWatcher();
                    poll.Start();
                    RequestRead(true);
                    break;
                case "loaded":
                case "failed":
                    AcceptRead(message);
                    break;
                case "view":
                    if (displayed != null && message.revision == displayed.Revision && message.view != null)
                    {
                        message.view.Zoom = ReadZoom();
                        state.SetView(displayed.Path, message.view);
                        ScheduleSave();
                    }
                    break;
                case "refresh": RequestRead(true); break;
                case "toggle": ToggleMode(); break;
                case "browse": ToggleBrowse(); break;
                case "settings": OpenPreferences(); break;
                case "settings-closed": ClosePreferences(); break;
                case "settings-apply": SavePreferences(message.appearance, message.startOnLogin); break;
                case "move": if (titleHidden && !settingsOpen) NativeWindow.BeginMove(Handle); break;
                case "choose": ChooseWorkbook(); break;
                case "open": OpenWorkbook(); break;
                case "zoom-in": SetZoom(ReadZoom() + .1, true); break;
                case "zoom-out": SetZoom(ReadZoom() - .1, true); break;
                case "zoom-reset": SetZoom(1, true); break;
                case "exit": Close(); break;
                case "flushed": if (exitRequested) FinishExit(); break;
            }
        }

        private void Send(object message)
        {
            if (ready && !IsDisposed)
                browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(message));
        }

        private async void RequestRead(bool force)
        {
            if (!ready || exitRequested) return;
            if (sourcePath == null)
            {
                Notice("请选择要显示的 Excel 工作簿。", false);
                return;
            }
            if (reading || pending != null)
            {
                readAgain = true;
                forceAgain |= force;
                return;
            }

            reading = true;
            string path = sourcePath;
            int version = revision;
            WorkbookSnapshot previous = force ? null : displayed;
            try
            {
                var snapshot = await Task.Run(() => WorkbookSnapshot.Read(path, previous));
                if (exitRequested || IsDisposed || version != revision) return;
                if (snapshot == null)
                {
                    Notice("", false);
                    return;
                }
                snapshot.Revision = ++revision;
                pending = snapshot;
                BookView view = state.ViewFor(path);
                bool same = displayed != null && string.Equals(displayed.Path, path, StringComparison.OrdinalIgnoreCase);
                if (displayed == null) SetZoom(view == null ? 1 : BookView.ValidZoom(view.Zoom), false);
                Send(new { type = "load", revision = snapshot.Revision, name = Path.GetFileName(path),
                    view = view, same = same });
            }
            catch (FileNotFoundException) { if (version == revision) Notice("找不到工作簿。可更换文件。", true); }
            catch (DirectoryNotFoundException) { if (version == revision) Notice("工作簿所在文件夹暂不可用。", true); }
            catch (UnauthorizedAccessException) { if (version == revision) Notice("没有权限读取工作簿。", true); }
            catch (IOException) { if (version == revision) Notice("暂时无法读取工作簿。稍后会自动重试。", true); }
            finally
            {
                reading = false;
                ReadQueued();
            }
        }

        private void AcceptRead(BrowserMessage message)
        {
            if (pending == null || pending.Revision != message.revision) return;
            var snapshot = pending;
            pending = null;
            snapshot.Content = null;
            if (message.type == "loaded")
            {
                bool switched = displayed == null || !string.Equals(snapshot.Path, displayed.Path, StringComparison.OrdinalIgnoreCase);
                displayed = snapshot;
                state.Source = snapshot.Path;
                if (switched)
                {
                    var view = state.ViewFor(snapshot.Path);
                    SetZoom(view == null ? 1 : BookView.ValidZoom(view.Zoom), false);
                }
                Notice("", false);
                ScheduleSave();
                LowerMemoryWhenIdle(!ContainsFocus);
            }
            else
            {
                Notice("暂时无法打开工作簿。请检查文件或稍后刷新。", true);
                Debug.WriteLine(message.reason);
            }
            ReadQueued();
        }

        private void ReadQueued()
        {
            if (!readAgain || reading || pending != null || exitRequested || IsDisposed) return;
            bool force = forceAgain;
            readAgain = false;
            forceAgain = false;
            RequestRead(force);
        }

        private void Notice(string text, bool error)
        {
            if (lastNotice == text) return;
            lastNotice = text;
            Send(new { type = "notice", text = text, error = error });
        }

        private void ResetWatcher()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (sourcePath == null) return;
            string parent = Path.GetDirectoryName(sourcePath);
            if (!Directory.Exists(parent)) return;
            try
            {
                watcher = new FileSystemWatcher(parent, Path.GetFileName(sourcePath)) {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    SynchronizingObject = this
                };
                watcher.Changed += delegate { QueueFileRead(); };
                watcher.Created += delegate { QueueFileRead(); };
                watcher.Deleted += delegate { QueueFileRead(); };
                watcher.Renamed += delegate { QueueFileRead(); };
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void QueueFileRead()
        {
            if (exitRequested) return;
            debounce.Stop();
            debounce.Start();
        }

        private void ChooseWorkbook()
        {
            if (choosingWorkbook || exitRequested) return;
            choosingWorkbook = true;
            DetachFromDesktop();
            try
            {
                using (var picker = new OpenFileDialog {
                    Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false,
                    Title = "选择日程工作簿", InitialDirectory = Path.GetDirectoryName(sourcePath)
                })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    string selected = Normalize(picker.FileName);
                    if (selected == null) return;
                    sourcePath = selected;
                    pending = null;
                    Send(new { type = "cancel", revision = ++revision });
                    ResetWatcher();
                    RequestRead(true);
                }
            }
            finally
            {
                choosingWorkbook = false;
                AttachToDesktop();
            }
        }

        private void OpenWorkbook()
        {
            if (displayed == null) return;
            try { Process.Start(new ProcessStartInfo(displayed.Path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Notice("无法用默认应用打开原表。", true); }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return null;
            try { return Path.GetFullPath(path); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private double ReadZoom() { return ready ? browser.ZoomFactor : 1; }

        private void LowerMemoryWhenIdle(bool inactive)
        {
            if (ready && !exitRequested)
                browser.CoreWebView2.MemoryUsageTargetLevel = inactive
                    ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        }

        private void SetZoom(double factor, bool persist)
        {
            if (!ready) return;
            browser.ZoomFactor = Math.Round(Math.Max(.75, Math.Min(2, factor)), 2);
            Send(new { type = "zoom", factor = browser.ZoomFactor, save = persist });
        }

        public void ShowExpanded()
        {
            if (exitRequested) return;
            if (!expanded)
            {
                CapturePlacement();
                expanded = true;
                DetachFromDesktop();
                NativeWindow.SetDesktopWindowTraits(Handle, false);
                ApplyBounds();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            BringToFront();
            Activate();
            Send(new { type = "mode", expanded = true });
        }

        private void ToggleMode()
        {
            if (!expanded) { ShowExpanded(); return; }
            CapturePlacement();
            expanded = false;
            NativeWindow.SetDesktopWindowTraits(Handle, true);
            ApplyBounds();
            Send(new { type = "mode", expanded = false });
            AttachToDesktop();
            ScheduleSave();
        }

        private void ApplyBounds()
        {
            changingBounds = true;
            try { Bounds = NativeWindow.BoundsFor(expanded ? state.Expanded : state.Compact, expanded); }
            finally { changingBounds = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (titleHidden && m.Msg == NativeWindow.NcCalcSize)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (titleHidden && m.Msg == NativeWindow.NcHitTest)
            {
                int hit = NativeWindow.ResizeHit(ClientRectangle,
                    PointToClient(NativeWindow.ScreenPoint(m.LParam)), Padding.Left);
                if (hit != 0) { m.Result = new IntPtr(hit); return; }
            }
            if (!expanded && m.Msg == NativeWindow.SystemCommand && (m.WParam.ToInt64() & 0xfff0) == NativeWindow.Minimize)
                return;
            base.WndProc(ref m);
            if ((uint)m.Msg == taskbarCreated) AttachToDesktop();
        }

        private void AttachToDesktop()
        {
            if (!CanPlaceOnDesktop()) return;
            zOrder.Stop();
            IntPtr owner = NativeWindow.GetDesktopOwner();
            if (owner != IntPtr.Zero && NativeWindow.SetOwner(Handle, owner))
            {
                // Keep this HWND in Explorer's z-order group without changing its DPI or WebView.
                desktopOwner = owner;
                NativeWindow.SetDesktopOrder(Handle, externalForeground, true);
            }
            else
            {
                DetachFromDesktop();
                QueueDesktopOrder(NativeWindow.IsDesktop(NativeWindow.GetForegroundWindow()));
            }
        }

        private bool HasDesktopOwner()
        {
            return desktopOwner != IntPtr.Zero && NativeWindow.GetOwner(Handle) == desktopOwner;
        }

        private void DetachFromDesktop()
        {
            zOrder.Stop();
            if (IsHandleCreated && HasDesktopOwner()) NativeWindow.SetOwner(Handle, originalOwner);
            desktopOwner = IntPtr.Zero;
            lowerAfterInteraction = false;
        }

        private void QueueDesktopOrder(bool shellChanged = false)
        {
            if (!CanPlaceOnDesktop()) return;
            zOrder.Stop();
            // Explorer can move Progman again after it becomes foreground.
            // Recheck only during that short transition.
            desktopChecks = shellChanged ? 6 : 0;
            if (shellChanged) NativeWindow.SetDesktopOrder(Handle, externalForeground, false);
            zOrder.Interval = shellChanged ? 16 : 64;
            zOrder.Start();
        }

        private bool CanPlaceOnDesktop()
        {
            return !expanded && !settingsOpen && !choosingWorkbook && !exitRequested && IsHandleCreated && !IsDisposed;
        }

        private void CapturePlacement()
        {
            if (!Visible || WindowState != FormWindowState.Normal) return;
            if (expanded) state.Expanded = NativeWindow.CaptureBounds(this);
            else state.Compact = NativeWindow.CaptureBounds(this);
        }

        private void SaveWindowSoon()
        {
            if (changingBounds || exitRequested || !IsHandleCreated) return;
            CapturePlacement();
            ScheduleSave();
        }

        private void ScheduleSave() { save.Stop(); save.Start(); }

        private void SaveState()
        {
            if (!store.Save(state)) Notice("无法保存本机浏览位置。", true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exitConfirmed && e.CloseReason != CloseReason.WindowsShutDown && ready && displayed != null)
            {
                e.Cancel = true;
                if (!exitRequested)
                {
                    exitRequested = true;
                    Send(new { type = "flush" });
                    exit.Start();
                }
                return;
            }
            exitRequested = true;
            CapturePlacement();
            SaveState();
            DetachFromDesktop();
            base.OnFormClosing(e);
        }

        private void FinishExit()
        {
            if (exitConfirmed) return;
            exit.Stop();
            exitConfirmed = true;
            BeginInvoke(new Action(Close));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (foregroundHook != IntPtr.Zero)
                {
                    NativeWindow.UnhookWinEvent(foregroundHook);
                    foregroundHook = IntPtr.Zero;
                }
                if (watcher != null) watcher.Dispose();
                poll.Dispose();
                debounce.Dispose();
                save.Dispose();
                zOrder.Dispose();
                exit.Dispose();
                tray.Dispose();
                trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class BrowserMessage
        {
            public string type { get; set; }
            public int revision { get; set; }
            public BookView view { get; set; }
            public string reason { get; set; }
            public AppearancePreferences appearance { get; set; }
            public bool? startOnLogin { get; set; }
        }
    }
}
