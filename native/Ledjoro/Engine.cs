using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Ledjoro
{
    /// <summary>
    /// Runs the accounting engine (the same code as the web edition: ledger, SQLite database, importers) as a hidden child process.
    /// There is no browser and no web view anywhere: this app draws every screen itself with native Windows controls.
    /// </summary>
    public sealed class Engine : IDisposable
    {
        public int Port { get; private set; }
        public string Key { get; private set; }
        public string SetupToken { get; private set; }
        Process proc;

        static string Token(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        /// <summary>Everything Ledjoro stores (the books, settings, logs, the unpacked engine) lives in "Ledjoro-Data" next to Ledjoro.exe, so copying the exe and that folder moves the whole thing.</summary>
        public static string DataDir()
        {
            string env = Environment.GetEnvironmentVariable("LEDJORO_DATA");
            return !string.IsNullOrEmpty(env) ? env : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ledjoro-Data");
        }
        /// <summary>The folder of the company that is open (each company has its own books); the Ledjoro-Data folder itself until one is chosen.</summary>
        public static string CompanyDir;
        public static string CompanyFolder() { return !string.IsNullOrEmpty(CompanyDir) ? CompanyDir : DataDir(); }

        /// <summary>Null when the data folder can be used, otherwise an explanation for the person.</summary>
        public static string CheckWritable()
        {
            try
            {
                Directory.CreateDirectory(DataDir());
                string t = Path.Combine(DataDir(), ".write-test-" + Guid.NewGuid().ToString("N")); File.WriteAllText(t, "x"); File.Delete(t); return null;
            }
            catch (Exception e)
            {
                return "Ledjoro keeps all of its data in a folder next to the program:\n\n" + DataDir() + "\n\nbut Windows will not let it write there (" + e.Message + ").\n\nMove Ledjoro.exe into a folder you can write to, such as Documents, and start it again.";
            }
        }

        static string Sha256Hex(Stream s)
        {
            using (var h = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in h.ComputeHash(s)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The engine is embedded in Ledjoro.exe. It is unpacked once per version, and its SHA-256 is verified on every start.</summary>
        static string ExtractEmbedded()
        {
            var asm = Assembly.GetExecutingAssembly();
            string hash;
            using (var hs = asm.GetManifestResourceStream("engine.sha256"))
            {
                if (hs == null) return null;
                hash = new StreamReader(hs).ReadToEnd().Trim().ToLowerInvariant();
            }
            string parent = Path.Combine(DataDir(), "engine"), dir = Path.Combine(parent, hash.Substring(0, 16));
            string file = Path.Combine(dir, "Ledjoro-Engine.exe");
            if (File.Exists(file))
            {
                try { using (var fs = File.OpenRead(file)) if (Sha256Hex(fs) == hash) return file; } catch (IOException) { }
            }
            Directory.CreateDirectory(dir);
            string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var rs = asm.GetManifestResourceStream("engine.exe"))
            using (var ws = File.Create(tmp)) rs.CopyTo(ws);
            using (var fs = File.OpenRead(tmp))
                if (Sha256Hex(fs) != hash) { File.Delete(tmp); throw new InvalidOperationException("The engine inside Ledjoro.exe failed its safety check (its checksum does not match), so Ledjoro refuses to run it. The program file is damaged or was changed, for example by an incomplete download or by security software. Copy Ledjoro.exe again from the original."); }
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            File.Move(tmp, file);
            CleanOldEngines(parent, hash.Substring(0, 16));
            return file;
        }

        /// <summary>Remove unpacked engines of other versions from the engine folder, so only the current one is kept.</summary>
        static void CleanOldEngines(string parent, string keep)
        {
            try { foreach (var d in Directory.GetDirectories(parent)) if (Path.GetFileName(d) != keep) Directory.Delete(d, true); } catch (Exception) { }
        }

        public void Start()
        {
            Key = Token(32);
            SetupToken = Token(24);
            Port = FreePort();

            string file, args = "";
            string devJs = Environment.GetEnvironmentVariable("LEDJORO_ENGINE_JS"); // development: run the engine from source
            if (!string.IsNullOrEmpty(devJs)) { file = "node"; args = "\"" + devJs + "\" "; }
            else
            {
                file = ExtractEmbedded();
                if (file == null)
                {
                    file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ledjoro-Engine.exe");
                    if (!File.Exists(file)) throw new InvalidOperationException("Ledjoro cannot find its engine, the part that keeps your books. It should be inside Ledjoro.exe. The program file may be incomplete or damaged (for example a download that did not finish, or security software that removed part of it). Copy Ledjoro.exe again from where you got it.");
                }
            }
            var psi = new ProcessStartInfo(file, args + "--port " + Port)
            {
                UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(file) ?? "",
            };
            psi.EnvironmentVariables["LEDJORO_DATA"] = CompanyFolder();
            psi.EnvironmentVariables["LEDJORO_SHELL_KEY"] = Key;
            psi.EnvironmentVariables["LEDJORO_SETUP_TOKEN"] = SetupToken;
            proc = Process.Start(psi);
        }

        /// <summary>Waits until the engine answers; fails fast (with the engine's log) if it exits.</summary>
        public async Task WaitReady(Api api, int timeoutMs = 30000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (proc.HasExited) throw new InvalidOperationException(Say.EngineStopped(LogTail()));
                try { await api.Get("api/ping"); return; } catch (ApiException) { await Task.Delay(120); }
            }
            throw new InvalidOperationException(Say.EngineStopped(LogTail(), true));
        }

        static string LogTail()
        {
            try
            {
                var lines = File.ReadAllLines(Path.Combine(CompanyFolder(), "ledjoro.log"));
                return string.Join("\n", lines, Math.Max(0, lines.Length - 6), Math.Min(6, lines.Length));
            }
            catch (Exception) { return ""; }
        }

        public void Dispose()
        {
            try
            {
                if (proc == null || proc.HasExited) return;
                try { proc.StandardInput.Close(); } catch (Exception) { } // the engine exits when its input ends
                if (!proc.WaitForExit(4000)) proc.Kill();
            }
            catch (Exception) { }
        }
    }
}
