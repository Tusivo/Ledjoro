using System;
using System.Linq;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Lets the mouse wheel scroll whatever is under the pointer, like modern apps (WinForms only scrolls the focused control).</summary>
    sealed class WheelFilter : IMessageFilter
    {
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point p);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x20A) return false;
            long l = m.LParam.ToInt64();
            var pt = new System.Drawing.Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
            IntPtr h = WindowFromPoint(pt);
            if (h == IntPtr.Zero || h == m.HWnd || Control.FromHandle(h) == null) return false;
            SendMessage(h, m.Msg, m.WParam, m.LParam);
            return true;
        }
    }

    sealed class StartingCard : AuthCard
    {
        public StartingCard() : base("Starting Ledjoro…", 420) { Add(new Lbl("Opening your books. This only takes a moment.", LS.Muted, true)); }
    }

    static class Program
    {
        [STAThread, HandleProcessCorruptedStateExceptions, SecurityCritical]
        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception e)
            {
                try { Directory.CreateDirectory(Engine.DataDir()); File.AppendAllText(Path.Combine(Engine.DataDir(), "shell-crash.log"), DateTime.Now.ToString("s") + "\n" + e + "\n\n"); } catch (Exception) { }
                MessageBox.Show("Ledjoro hit a problem and has to close.\n\n" + Say.Plain(e) + "\n\nYour books are safe: every change is saved all-or-nothing. Open Ledjoro again to carry on.", "Ledjoro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--passkey-probe"))
            {   // developer check: does Windows accept our passkey request structures? (one short request that is then cancelled by timeout)
                string outFile = args[args.Length - 1]; var sb = new System.Text.StringBuilder();
                sb.AppendLine("api version " + WebAuthn.ApiVersion + ", available " + WebAuthn.Available); sb.AppendLine(WebAuthn.LayoutReport());
                var form = new System.Windows.Forms.Form { ShowInTaskbar = false, Opacity = 0, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000) }; form.Show(); IntPtr hw = form.Handle;
                foreach (var tryIt in new[] { new object[] { "no window, ledjoro.local", IntPtr.Zero, "ledjoro.local", "https://ledjoro.local" }, new object[] { "window, ledjoro.local", hw, "ledjoro.local", "https://ledjoro.local" }, new object[] { "window, example.com", hw, "example.com", "https://example.com" } })
                {
                    try { var t = WebAuthn.Get((IntPtr)tryIt[1], (string)tryIt[2], (string)tryIt[3], "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 1500); t.Wait(30000); sb.AppendLine((string)tryIt[0] + ": completed " + t.IsCompleted); }
                    catch (AggregateException ex) { var pe = ex.InnerException as PasskeyException; sb.AppendLine((string)tryIt[0] + ": " + (pe != null ? (pe.Cancelled ? "cancelled/timed out (expected)" : pe.Message) : ex.InnerException.GetType().Name + " " + ex.InnerException.Message)); }
                }
                System.IO.File.WriteAllText(outFile, sb.ToString()); return 0;
            }
            if (args.Contains("--camera-probe"))
            {   // developer check: can the camera be started, and do frames arrive? Frames are counted and thrown away, never saved or shown.
                string outFile = args[args.Length - 1]; var sb = new System.Text.StringBuilder();
#if HAS_WINRT_CAMERA
                try
                {
                    using (var cam = new Webcam())
                    {
                        cam.StartAsync().GetAwaiter().GetResult(); sb.AppendLine("camera started");
                        int frames = 0, w = 0, h = 0; var until = DateTime.UtcNow.AddSeconds(3);
                        while (DateTime.UtcNow < until) { using (var f = cam.TryGetFrame()) { if (f != null) { frames++; w = f.Width; h = f.Height; } } System.Threading.Thread.Sleep(40); }
                        sb.AppendLine("frames in 3 seconds: " + frames + ", size " + w + " x " + h);
                    }
                }
                catch (Exception ex) { sb.AppendLine("camera problem: " + Webcam.Explain(ex)); sb.AppendLine("(" + ex.GetType().Name + ", 0x" + ex.HResult.ToString("X8") + ")"); }
#else
                sb.AppendLine("camera support is not compiled into this build");
#endif
                System.IO.File.WriteAllText(outFile, sb.ToString()); return 0;
            }
            if (args.Contains("--make-icon")) { IconWriter.Write(args.Length > 1 ? args[args.Length - 1] : "Ledjoro.ico"); return 0; }
            int afterIdx = Array.IndexOf(args, "--after"), afterPid;
            if (afterIdx >= 0 && afterIdx + 1 < args.Length && int.TryParse(args[afterIdx + 1], out afterPid))
            {
                try { using (var old = System.Diagnostics.Process.GetProcessById(afterPid)) old.WaitForExit(30000); } catch (Exception) { /* already gone */ }
            }
            bool selftest = args.Contains("--selftest"); App.SelfTesting = selftest;
            bool first;
            using (var mutex = new Mutex(true, "Local\\Ledjoro.Native.v1", out first))
            {
                if (!first && !selftest)
                {   // already running: bring that window forward instead of starting a second copy
                    var h = Native.FindWindow(null, "Ledjoro");
                    if (h != IntPtr.Zero) { if (Native.IsIconic(h)) Native.ShowWindow(h, 9); Native.SetForegroundWindow(h); }
                    return 0;
                }
                if (!selftest && !Portable.Prepare()) return 1;
                Theme.Load();
                Application.AddMessageFilter(new WheelFilter());
                Application.ThreadException += (s, e) => App.Toast(Say.Plain(e.Exception), true);
                var form = new MainForm();
                int idx = Array.IndexOf(args, "--selftest");
                string outDir = selftest ? (args.Length > idx + 1 ? args[idx + 1] : "selftest-out") : null;
                form.Shown += async (s, e) => await StartUp(form, outDir);
                form.FormClosed += (s, e) => { if (App.Engine != null) App.Engine.Dispose(); };
                Application.Run(form);
            }
            return Environment.ExitCode;
        }

        static async Task StartUp(MainForm form, string selfTestDir)
        {
            try
            {
                Companies.Load();
                if (selfTestDir != null) { await form.OpenCompany(Companies.Current ?? Companies.List[0], false); await SelfTest.Run(form, selfTestDir); return; }
                if (Companies.List.Count > 1) form.ShowPicker();                    // several companies: choose which to open
                else await form.OpenCompany(Companies.List[0]);                     // one company: straight to its sign-in
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ledjoro could not start.\n\n" + Say.Plain(ex), "Ledjoro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.Close();
            }
        }
    }
}
