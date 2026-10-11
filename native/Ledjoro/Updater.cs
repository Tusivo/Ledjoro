using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Ledjoro
{
    /// <summary>
    /// Looks for a newer Ledjoro among the project's GitHub releases (only when the person clicks Check for Updates), downloads it,
    /// checks it, keeps the current Ledjoro.exe as a backup in the same folder and puts the new one in its place.
    /// </summary>
    static class Updater
    {
        public const string Repo = "Tusivo/Ledjoro";
        const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

        public sealed class Release
        {
            public string Tag = "", Version = "", Title = "", Notes = "", Page = "", AssetName = "", AssetUrl = "", SigUrl = "", Sha256 = "";
            public long Size;
        }

        // ---- version order (semantic versioning: 0.9.0-beta.1 < 0.9.0-beta.2 < 0.9.0 < 0.10.0) --------------------------------

        public static string Clean(string v) { v = (v ?? "").Trim(); if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1); int plus = v.IndexOf('+'); return plus >= 0 ? v.Substring(0, plus) : v; }

        public static bool Valid(string v) { return System.Text.RegularExpressions.Regex.IsMatch(Clean(v), @"^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$"); }

        public static int Compare(string a, string b)
        {
            a = Clean(a); b = Clean(b);
            string pa = "", pb = ""; int i = a.IndexOf('-'), j = b.IndexOf('-');
            if (i >= 0) { pa = a.Substring(i + 1); a = a.Substring(0, i); }
            if (j >= 0) { pb = b.Substring(j + 1); b = b.Substring(0, j); }
            var na = a.Split('.').Select(x => { long n; long.TryParse(x, out n); return n; }).ToArray();
            var nb = b.Split('.').Select(x => { long n; long.TryParse(x, out n); return n; }).ToArray();
            for (int k = 0; k < Math.Max(na.Length, nb.Length); k++)
            {
                long x = k < na.Length ? na[k] : 0, y = k < nb.Length ? nb[k] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            if (pa == pb) return 0;
            if (pa.Length == 0) return 1;      // a release beats its own betas
            if (pb.Length == 0) return -1;
            var ia = pa.Split('.'); var ib = pb.Split('.');
            for (int k = 0; k < Math.Max(ia.Length, ib.Length); k++)
            {
                if (k >= ia.Length) return -1;
                if (k >= ib.Length) return 1;
                long x, y; bool nx = long.TryParse(ia[k], out x), ny = long.TryParse(ib[k], out y);
                int c = nx && ny ? x.CompareTo(y) : nx ? -1 : ny ? 1 : string.CompareOrdinal(ia[k], ib[k]);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            return 0;
        }

        // ---- the release list ---------------------------------------------------------------------------------------------

        /// <summary>The newest published release that has a Ledjoro .exe attached (drafts are never listed by GitHub for the public).</summary>
        public static Release Pick(Jv releases)
        {
            Release best = null;
            if (releases == null || !releases.IsArray) return null;
            foreach (var r in releases.Items)
            {
                if (r["draft"].Bool) continue;
                string tag = r["tag_name"].Str;
                if (!Valid(tag)) continue;
                Jv asset = null;
                foreach (var a in r["assets"].Items)
                {
                    string n = a["name"].Str ?? "";
                    string u = a["browser_download_url"].Str ?? "";
                    if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !n.StartsWith("Ledjoro", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!u.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                    if (asset == null || string.Equals(n, "Ledjoro.exe", StringComparison.OrdinalIgnoreCase)) asset = a;
                }
                if (asset == null) continue;
                // a release is only offered when it carries a signature file next to the program (Ledjoro.exe.sig)
                Jv sig = null;
                foreach (var a in r["assets"].Items) { string u = a["browser_download_url"].Str ?? ""; if (string.Equals(a["name"].Str, asset["name"].Str + ".sig", StringComparison.OrdinalIgnoreCase) && u.StartsWith(DownloadPrefix, StringComparison.Ordinal) && a["size"].Long > 0 && a["size"].Long < 16384) sig = a; }
                if (sig == null) continue;
                var rel = new Release
                {
                    Tag = tag, Version = Clean(tag), Title = r["name"].Str ?? tag, Notes = r["body"].Str ?? "", Page = r["html_url"].Str ?? "",
                    AssetName = asset["name"].Str, AssetUrl = asset["browser_download_url"].Str, SigUrl = sig["browser_download_url"].Str, Size = asset["size"].Long
                };
                string digest = asset["digest"].Str ?? ""; if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) rel.Sha256 = digest.Substring(7).ToLowerInvariant();
                if (best == null || Compare(rel.Version, best.Version) > 0) best = rel;
            }
            return best;
        }

        static HttpClient Client(int seconds)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
            var c = new HttpClient(); c.Timeout = TimeSpan.FromSeconds(seconds);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Ledjoro/" + AboutScreen.Version.Replace(' ', '-'));
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>Null when this is already the newest published version.</summary>
        public static async Task<Release> Check(string current)
        {
            if (App.SelfTesting) return null;   // the self-test never goes to the internet
            if (PublicKey() == null) throw new UpdateError("This copy of Ledjoro does not carry the release signing key, so it cannot check that an update is genuine. Download the newest Ledjoro.exe from the releases page instead.");
            using (var c = Client(20))
            {
                var resp = await c.GetAsync("https://api.github.com/repos/" + Repo + "/releases?per_page=30");
                if (resp.StatusCode == HttpStatusCode.NotFound) throw new UpdateError("No releases have been published for Ledjoro yet.");
                if (!resp.IsSuccessStatusCode) throw new UpdateError("GitHub answered with an error (" + (int)resp.StatusCode + "). Try again in a few minutes.");
                var best = Pick(Json.Parse(await resp.Content.ReadAsStringAsync()));
                if (best == null) throw new UpdateError("No signed release of Ledjoro was found. Download the newest Ledjoro.exe from the releases page instead.");
                return Compare(best.Version, current) > 0 ? best : null;
            }
        }

        // ---- download and install -----------------------------------------------------------------------------------------

        public sealed class UpdateError : Exception { public UpdateError(string m) : base(m) { } }

        public static string HashOf(string path)
        {
            using (var f = File.OpenRead(path)) using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(f).Select(b => b.ToString("x2")));
        }

        // ---- the release signature -------------------------------------------------------------------------------------

        /// <summary>The release signing public key built into this program (64 bytes: X then Y of an ECDSA P-256 key), or null when this build has none.</summary>
        public static byte[] PublicKey()
        {
            try
            {
                using (var s = typeof(Updater).Assembly.GetManifestResourceStream("release-key.txt"))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    {
                        string b64 = string.Concat(r.ReadToEnd().Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith("#")));
                        var raw = Convert.FromBase64String(b64); return raw.Length == 64 ? raw : null;
                    }
                }
            }
            catch (Exception) { return null; }
        }

        public static byte[] SignedMessage(string version, string sha256) { return Encoding.UTF8.GetBytes("ledjoro-release-v1\n" + Clean(version) + "\n" + sha256 + "\n"); }

        /// <summary>
        /// Passes silently when <paramref name="sigText"/> is a signature, made with the release key, for exactly this version and this file (by SHA-256).
        /// Throws UpdateError saying why when it is not. The version is part of what was signed, so an old signed program cannot be passed off as a new one.
        /// </summary>
        public static void VerifySignature(string sigText, string sha256, string version, byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length != 64) throw new UpdateError("This copy of Ledjoro does not carry the release signing key, so it cannot check that an update is genuine. Download the newest Ledjoro.exe from the releases page instead.");
            Jv j; try { j = Json.Parse(sigText); } catch (Exception) { throw new UpdateError("The update's signature file could not be read, so the update was thrown away."); }
            if (j["format"].Long != 1) throw new UpdateError("The update's signature is in a format this version of Ledjoro does not know. Download the newest Ledjoro.exe from the releases page instead.");
            if (!string.Equals(j["version"].Str, Clean(version), StringComparison.Ordinal)) throw new UpdateError("The update's signature is for a different version, so the update was thrown away.");
            if (!string.Equals((j["sha256"].Str ?? "").ToLowerInvariant(), sha256, StringComparison.Ordinal)) throw new UpdateError("The update's signature is for a different file than the one downloaded, so the update was thrown away.");
            byte[] sig; try { sig = Convert.FromBase64String(j["signature"].Str ?? ""); } catch (FormatException) { sig = new byte[0]; }
            bool ok = false;
            if (sig.Length == 64)
            {
                try
                {
                    var q = new System.Security.Cryptography.ECParameters { Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256, Q = new System.Security.Cryptography.ECPoint { X = publicKey.Take(32).ToArray(), Y = publicKey.Skip(32).ToArray() } };
                    using (var ec = System.Security.Cryptography.ECDsa.Create(q)) ok = ec.VerifyData(SignedMessage(version, sha256), sig, System.Security.Cryptography.HashAlgorithmName.SHA256);
                }
                catch (Exception) { ok = false; }
            }
            if (!ok) throw new UpdateError("The update is not signed with Ledjoro's release key, so it was thrown away. If you reached this by clicking Check for Updates on a normal release, please report it under Support.");
        }

        /// <summary>Checks a downloaded file against what GitHub said about it. Throws UpdateError when anything is off.</summary>
        public static void Verify(string path, Release r)
        {
            var info = new FileInfo(path);
            if (r.Size > 0 && info.Length != r.Size) throw new UpdateError("The download is incomplete (" + info.Length + " of " + r.Size + " bytes).");
            if (info.Length < 1024 * 1024) throw new UpdateError("The downloaded file is too small to be Ledjoro.");
            var head = new byte[2]; using (var f = File.OpenRead(path)) f.Read(head, 0, 2);
            if (head[0] != (byte)'M' || head[1] != (byte)'Z') throw new UpdateError("The downloaded file is not a Windows program.");
            if (r.Sha256.Length > 0 && HashOf(path) != r.Sha256) throw new UpdateError("The download does not match the fingerprint GitHub published for it, so it was thrown away.");
        }

        /// <summary>Downloads the release next to the running program (same drive, so the swap is a rename). Returns the file.</summary>
        public static async Task<string> Download(Release r, string exePath, IProgress<int> progress)
        {
            if (!r.AssetUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal)) throw new UpdateError("The update is not hosted where Ledjoro expects it.");
            string tmp = Path.Combine(Path.GetDirectoryName(exePath), "Ledjoro.update.download");
            try
            {
                using (var c = Client(600))
                using (var resp = await c.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) throw new UpdateError("GitHub would not send the file (" + (int)resp.StatusCode + ").");
                    long total = resp.Content.Headers.ContentLength ?? r.Size; long got = 0; int last = -1;
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920]; int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n); got += n;
                            int pct = total > 0 ? (int)(got * 100 / total) : 0; if (pct != last) { last = pct; if (progress != null) progress.Report(pct); }
                        }
                    }
                }
                Verify(tmp, r);
                string sigText;
                using (var c2 = Client(60)) { var sr = await c2.GetAsync(r.SigUrl); if (!sr.IsSuccessStatusCode) throw new UpdateError("GitHub would not send the update's signature (" + (int)sr.StatusCode + "), so the update was not installed."); sigText = await sr.Content.ReadAsStringAsync(); }
                VerifySignature(sigText, HashOf(tmp), r.Version, PublicKey());
                return tmp;
            }
            catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } throw; }
        }

        /// <summary>
        /// Keeps the current program as "Ledjoro-&lt;version&gt;-backup.exe" in the same folder and puts the new file in its place.
        /// A running program can be renamed (not overwritten), which is what makes this possible. If anything fails the old one is put back.
        /// Returns the backup's path.
        /// </summary>
        public static string Install(string newFile, string exePath, string currentVersion)
        {
            string dir = Path.GetDirectoryName(exePath), backup = Path.Combine(dir, "Ledjoro-" + Clean(currentVersion) + "-backup.exe");
            for (int n = 2; File.Exists(backup); n++) backup = Path.Combine(dir, "Ledjoro-" + Clean(currentVersion) + "-backup (" + n + ").exe");
            try { File.Move(exePath, backup); }
            catch (Exception ex) { throw new UpdateError("Ledjoro could not make a backup of itself in " + dir + " (" + ex.Message + "). Nothing was changed."); }
            try { File.Move(newFile, exePath); }
            catch (Exception ex)
            {
                try { if (!File.Exists(exePath)) File.Move(backup, exePath); } catch (Exception) { }
                throw new UpdateError("Ledjoro could not put the new version in place (" + ex.Message + "). The old version was left as it was.");
            }
            return backup;
        }

        public static string Friendly(Exception ex)
        {
            if (ex is UpdateError) return ex.Message;
            if (ex is TaskCanceledException) return "GitHub did not answer in time. Check your internet connection and try again.";
            if (ex is HttpRequestException || ex is WebException) return "Ledjoro could not reach GitHub. Check your internet connection and try again.";
            return Say.Plain(ex);
        }
    }
}
