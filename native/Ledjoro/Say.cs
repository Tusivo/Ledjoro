using System;
using System.IO;
using System.Net.Sockets;

namespace Ledjoro
{
    /// <summary>
    /// Turns any exception into a short explanation in plain words: what went wrong, why it probably happened, and what to do next.
    /// The engine already words its own messages this way; this covers everything that can fail on the Windows side.
    /// </summary>
    public static class Say
    {
        public static string Plain(Exception e)
        {
            if (e == null) return "Something went wrong, but no details were available.";
            var agg = e as AggregateException; if (agg != null && agg.InnerException != null) return Plain(agg.InnerException);
            var api = e as ApiException; if (api != null) return ForApi(api);
            var pk = e as PasskeyException; if (pk != null) return pk.Message;
            if (e is InvalidOperationException && !(e is ObjectDisposedException)) return e.Message;   // our own checks are already written for people
            if (e is TimeoutException) return "Ledjoro waited a long time for an answer and gave up. The part of Ledjoro that keeps your books may be busy or stuck. Close Ledjoro and open it again; nothing is lost by doing this. If it happens again, your computer may be low on memory or something is blocking Ledjoro.";
            if (e is UnauthorizedAccessException) return "Windows did not allow Ledjoro to use a file or folder (" + Short(e) + "). The folder may be read-only, may belong to another Windows user, or a security program may be blocking it. Try a folder you own, such as Documents.";
            if (e is FileNotFoundException || e is DirectoryNotFoundException) return "Ledjoro could not find a file or folder it needed (" + Short(e) + "). It may have been moved, renamed or deleted, or it is on a drive that is not plugged in.";
            if (e is PathTooLongException) return "The folder path is too long for Windows. Move Ledjoro to a folder closer to the top of the drive, such as C:\\Ledjoro, and try again.";
            if (e is IOException)
            {
                int code = e.HResult & 0xFFFF;
                if (code == 112 || code == 39) return "The disk is full, so Windows could not save the file. Free up some space (or choose a different drive) and try again.";
                if (code == 32 || code == 33) return "Another program is using that file, so Ledjoro cannot use it right now. Close the other program (or the file's window) and try again.";
                return "Windows could not read or write a file (" + Short(e) + "). Check that the file is not open in another program, that the drive is still connected, and that the folder is not read-only.";
            }
            if (e is OutOfMemoryException) return "Your computer ran out of memory. Close some other programs and try again. If you were opening a very large file, try a smaller one.";
            if (e is SocketException || e is System.Net.Http.HttpRequestException) return "Ledjoro could not talk to its own engine (the hidden helper that keeps your books). It may have stopped or been blocked by security software. Close Ledjoro and open it again.";
            return "Ledjoro hit a problem it did not expect: " + Short(e) + ". If you were saving something, check whether it saved before you repeat it. If this keeps happening, close Ledjoro and open it again; the details are written to shell-crash.log in the Ledjoro-Data folder for whoever supports you.";
        }

        public static string Date(string what) { return "The " + what + " is not a real date. Type it the way dates are written on this computer, for example " + Fmt.Example + ", or pick it from the calendar."; }
        public static string Money(string what) { return "The " + what + " is missing or is not a valid money amount. Type dollars and cents, such as 125.50 (digits and one decimal point only)."; }
        public const string EngineUnreachable = "Ledjoro could not talk to its own engine (the hidden helper that keeps your books), so nothing was saved. It may have stopped or been blocked by security software. Close Ledjoro and open it again.";

        /// <summary>The engine would not start or stopped: the reason the engine wrote to its log (in its own plain words), and what to try.</summary>
        public static string EngineStopped(string logTail, bool slow = false)
        {
            string reason = "";
            var lines = (logTail ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0 && reason.Length == 0; i--) { int k = lines[i].IndexOf("problem:"); if (k >= 0) reason = lines[i].Substring(k + 8).Trim(); }
            if (reason.Length == 0 && lines.Length > 0) { string last = lines[lines.Length - 1].Trim(); int sp = last.IndexOf(' '); reason = sp > 0 && last.Length > 25 ? last.Substring(sp + 1) : last; }
            return "Ledjoro's engine (the hidden helper that keeps your books) " + (slow ? "did not finish starting within 30 seconds." : "stopped while starting up.")
                + (reason.Length > 0 ? "\n\nWhy: " + reason : "")
                + "\n\nWhat to do: close Ledjoro and open it again. If it keeps happening, check that the Ledjoro-Data folder is not read-only, that no other program (or another copy of Ledjoro) has the books file open, and that security software is not blocking Ledjoro.";
        }

        public static string ForStatus(int status)
        {
            switch (status)
            {
                case 401: return "You are not signed in any more (you may have been signed out after a long pause, or the password changed). Sign in again.";
                case 403: return "Your user is not allowed to do this. Ask an administrator to change your role under Settings, Users, if you need it.";
                case 404: return "That item no longer exists. It may have been deleted or changed by someone else. Go back and look again.";
                case 413: return "That is bigger than Ledjoro accepts in one go. Split it into smaller parts.";
                case 429: return "There have been too many tries in a short time, so Ledjoro is making you wait a few minutes. Try again shortly.";
                default: return "Ledjoro hit a problem inside its own engine (status " + status + "). Nothing you typed was lost on screen. Try again; if it repeats, close Ledjoro and open it again.";
            }
        }

        static string Short(Exception e) { string m = (e.Message ?? "").Trim().TrimEnd('.'); return m.Length > 200 ? m.Substring(0, 200) + "…" : m; }

        /// <summary>Messages from the engine are already plain (they are written that way); if one is missing, explain the status instead.</summary>
        static string ForApi(ApiException e)
        {
            string m = (e.Message ?? "").Trim();
            return m.Length > 0 ? m : ForStatus(e.Status);
        }
    }
}
