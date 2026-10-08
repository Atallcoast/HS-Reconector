using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading.Tasks;

[assembly: System.Reflection.AssemblyVersion("4.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("4.1.0.0")]

namespace HearthstoneReconnector
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if(args.Length > 0) return FirewallWorker.Dispatch(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using(var mutex = new System.Threading.Mutex(true, "Local\\HearthstoneReconnectorStandalone", out created))
            {
                if(!created) { MessageBox.Show("Приложение уже запущено. Найдите его значок в трее."); return 0; }
                if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                { MessageBox.Show("Запустите приложение от имени администратора."); return 1; }
                try { FirewallBackend.CleanStale(); Application.Run(new Overlay()); }
                catch(Exception ex) { ConnectionManager.Log(ex.ToString()); MessageBox.Show("Не удалось подготовить брандмауэр: " + ex.Message); return 1; }
            }
            return 0;
        }
    }

    internal sealed class Overlay : Form
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        readonly RuneButton reconnect = new RuneButton();
        readonly Label status = new Label();
        readonly NotifyIcon tray;
        readonly Timer timer = new Timer();
        readonly string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearthstoneReconnector", "position.txt");
        DateTime cooldown = DateTime.MinValue;
        DateTime messageUntil = DateTime.MinValue;
        DateTime previewUntil = DateTime.MinValue;
        bool dragging;
        bool positioned;
        bool moveMode;
        ToolStripMenuItem moveItem;
        Point dragStart;
        Point formStart;
        bool busy;
        bool closeAfterRestore;

        System.Threading.EventWaitHandle cancel;
        

        public Overlay()
        {
            Text = "Hearthstone Reconnector 4.1";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(144, 68);
            reconnect.Size = ClientSize;
            reconnect.ExternalInput = true;
            reconnect.FrameChanged += delegate { PresentFrame(); };
            reconnect.Click += Reconnect;
            Cursor = Cursors.Hand;
            var menu = new ContextMenuStrip();
            moveItem = new ToolStripMenuItem("Показать / переместить кнопку");
            moveItem.CheckOnClick = true;
            moveItem.Click += delegate { SetMoveMode(moveItem.Checked); };
            menu.Items.Add(moveItem);
            menu.Items.Add("Сбросить положение", null, delegate { positioned = false; PlaceDefault(); SavePosition(); previewUntil = DateTime.UtcNow.AddSeconds(30); });
            menu.Items.Add("Снять блокировку", null, delegate {
                if(busy && cancel != null) { cancel.Set(); return; }
                try { FirewallBackend.CleanStale(); status.Text = "Блокировка снята"; messageUntil = DateTime.UtcNow.AddSeconds(5); }
                catch(Exception ex) { ConnectionManager.Log(ex.ToString()); MessageBox.Show("Не удалось снять блокировку: " + ex.Message); }
            });
            menu.Items.Add("Открыть журнал", null, delegate { try { if(File.Exists(ConnectionManager.LogPath)) Process.Start("notepad.exe", "\"" + ConnectionManager.LogPath + "\""); } catch {} });
            menu.Items.Add("Выход", null, delegate { Close(); });
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray = new NotifyIcon { Icon = Icon, Text = "Hearthstone Reconnector", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { previewUntil = DateTime.UtcNow.AddSeconds(30); Show(); };
            LoadPosition();
            previewUntil = DateTime.UtcNow.AddSeconds(15);
            timer.Interval = 300;
            timer.Tick += delegate { RefreshState(); };
            timer.Start();
            FormClosed += delegate { timer.Dispose(); tray.Dispose(); status.Dispose(); reconnect.Dispose(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) {
                if(busy) { e.Cancel = true; closeAfterRestore = true; if(cancel != null) cancel.Set(); status.Text = "Снимаю блокировку…"; }
            };
            RefreshState();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        internal void BeginDrag(Point cursor) { if(!moveMode) return; dragging = true; dragStart = cursor; formStart = Location; }
        internal void MoveDrag(Point cursor) { if(dragging) Location = new Point(formStart.X + cursor.X - dragStart.X, formStart.Y + cursor.Y - dragStart.Y); }
        internal void SetMoveMode(bool enabled) {
            moveMode = enabled; reconnect.MoveMode = enabled; Cursor = reconnect.Cursor; moveItem.Checked = enabled;
            if(enabled) { Show(); BringToFront(); }
            else { dragging = false; Capture = false; SavePosition(); previewUntil = DateTime.UtcNow.AddSeconds(3); }
            RefreshState();
        }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x80000; return p; } }
        void PresentFrame() {
            if(IsDisposed || !IsHandleCreated) return;
            using(var frame=reconnect.RenderFrame()) LayeredSurface.Present(Handle,frame,Location);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); PresentFrame(); }
        protected override void OnMouseEnter(EventArgs e) { reconnect.InputEnter(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { reconnect.InputLeave(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) {
            if(e.Button==MouseButtons.Left) { Capture=true; if(moveMode)BeginDrag(Cursor.Position); }
            reconnect.InputDown(e); base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e) { MoveDrag(Cursor.Position); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) {
            if(e.Button==MouseButtons.Left && dragging) { dragging=false; positioned=true; SavePosition(); }
            reconnect.InputUp(e);
            if(e.Button==MouseButtons.Left)Capture=false;
            base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) {
            if(!Capture) { reconnect.InputLeave(); if(dragging) { dragging=false; positioned=true; SavePosition(); } }
            base.OnMouseCaptureChanged(e);
        }
        void PlaceDefault()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            foreach(var p in Process.GetProcessesByName("Hearthstone"))
            {
                using(p) { Rect r; if(p.MainWindowHandle != IntPtr.Zero && GetWindowRect(p.MainWindowHandle, out r)) { area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); break; } }
            }
            Location = new Point(area.Right - Width - 30, area.Bottom - Height - 70);
            ClampPosition(); positioned = true;
        }
        void ClampPosition()
        {
            var area = Screen.FromRectangle(Bounds).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        void LoadPosition()
        {
            try { var parts = File.ReadAllText(config).Split(','); Location = new Point(int.Parse(parts[0]), int.Parse(parts[1])); positioned = true; ClampPosition(); }
            catch { Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 30, 100); }
        }
        void SavePosition() { try { ClampPosition(); Directory.CreateDirectory(Path.GetDirectoryName(config)); File.WriteAllText(config, Left + "," + Top); } catch {} }
        void RefreshState()
        {
            bool running = false, foreground = false;
            uint foregroundPid; GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
            foreach(var p in Process.GetProcessesByName("Hearthstone"))
                using(p) { running = true; if((uint)p.Id == foregroundPid) foreground = true; }
            if(running && !positioned) PlaceDefault();
            bool shouldShow = moveMode || foreground || dragging || DateTime.UtcNow < previewUntil;
            if(shouldShow && !Visible) Show(); else if(!shouldShow && Visible) Hide();
            reconnect.ActionEnabled = running && !busy && DateTime.UtcNow >= cooldown;
            if(!busy && DateTime.UtcNow >= messageUntil) status.Text = !running ? "Ожидание Hearthstone" : DateTime.UtcNow < cooldown ? "Ожидание переподключения…" : "Готово";
        }
        async void Reconnect(object sender, EventArgs e)
        {
            if(moveMode || busy || DateTime.UtcNow < cooldown) return;
            busy = true;
            reconnect.Busy = true;
            reconnect.ActionEnabled = false;
            string eventName = "Local\\HSRC2Cancel_" + Guid.NewGuid().ToString("N");
            cancel = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, eventName);
            try
            {
                int pid = 0;
                foreach(var p in Process.GetProcessesByName("Hearthstone")) using(p) { pid = p.Id; break; }
                if(pid == 0) throw new InvalidOperationException("Hearthstone не запущен.");
                status.Text = "Проверка переподключения…";
                string result = await Task.Run(delegate { return FirewallWorker.Launch(pid, eventName); });
                status.Text = result;
                if(result.StartsWith("MATCH_RESTORED|",StringComparison.Ordinal)) reconnect.Flash(false);
                else reconnect.FlashWarning();
                ConnectionManager.Log(result);
            }
            catch(Exception ex) {
                reconnect.Flash(true);
                status.Text = "Ошибка — см. журнал в трее"; ConnectionManager.Log(ex.ToString());
                tray.ShowBalloonTip(5000, "Reconnector", ex.Message, ToolTipIcon.Warning);
                try { FirewallBackend.CleanStale(); }
                catch(Exception cleanup) { ConnectionManager.Log(cleanup.ToString()); MessageBox.Show("Не удалось снять блокировку. Используйте RestoreConnection.cmd из папки приложения от имени администратора."); }
            }
            finally {
                busy = false; reconnect.Busy = false; cancel.Dispose(); cancel = null;
                cooldown = DateTime.UtcNow.AddSeconds(12); messageUntil = DateTime.UtcNow.AddSeconds(5);
                if(closeAfterRestore) Close();
            }
        }
    }

}


