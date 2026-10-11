using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Ledjoro
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        const int EM_SETCUEBANNER = 0x1501;

        /// <summary>Greyed hint text inside an empty single-line TextBox.</summary>
        public static void Cue(TextBox tb, string text)
        {
            if (tb.IsHandleCreated && !tb.Multiline) SendMessage(tb.Handle, EM_SETCUEBANNER, IntPtr.Zero, text ?? "");
        }

        /// <summary>Dark (or light) window frame on Windows 10 20H1+ and Windows 11; harmless where unsupported.</summary>
        public static void TitleBar(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref v, 4);
        }

        /// <summary>Makes scroll bars follow the theme.</summary>
        public static void ScrollTheme(Control c, bool dark)
        {
            if (!c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch (Exception) { }
            foreach (Control k in c.Controls) ScrollTheme(k, dark);
        }
    }
}
