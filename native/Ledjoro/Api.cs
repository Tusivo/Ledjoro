using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Ledjoro
{
    public sealed class ApiException : Exception
    {
        public int Status; public Jv Body;
        public ApiException(int status, string message, Jv data) : base(message) { Status = status; Body = data; }
    }

    public sealed class FileResult { public byte[] Bytes; public string FileName; }

    /// <summary>
    /// Client for the Ledjoro engine. The engine only answers requests that carry the per-launch secret key, and it is bound to
    /// 127.0.0.1, so nothing else on the computer (including a web browser) can use it.
    /// </summary>
    public sealed class Api : IDisposable
    {
        readonly HttpClient http;
        public string Csrf = "";
        public Jv User = Jv.Null;
        public Jv Integrity = Jv.Null;   // result of the start-up check of the books file (ok / problems)
        public string Company = "";
        public bool SetupRequired;
        public HashSet<string> Perms = new HashSet<string>();
        public event Action SessionLost;

        public Api(int port, string key)
        {
            var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false, AllowAutoRedirect = false };
            http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.Add("X-Ledjoro-Key", key);
        }

        public bool Can(string perm) { return Perms.Contains(perm); }
        public bool SignedIn { get { return !User.IsNull; } }

        public Task<Jv> Get(string path) { return Send(HttpMethod.Get, path, null); }
        public Task<Jv> Post(string path, object body = null) { return Send(HttpMethod.Post, path, body ?? new Dictionary<string, object>()); }
        public Task<Jv> Patch(string path, object body) { return Send(new HttpMethod("PATCH"), path, body); }
        public Task<Jv> Put(string path, object body) { return Send(HttpMethod.Put, path, body); }
        public Task<Jv> Delete(string path) { return Send(HttpMethod.Delete, path, null); }

        public static string Q(string v) { return Uri.EscapeDataString(v ?? ""); }

        HttpRequestMessage Build(HttpMethod m, string path, object body)
        {
            var req = new HttpRequestMessage(m, path.TrimStart('/'));
            if (body != null) req.Content = new StringContent(Json.Stringify(body), Encoding.UTF8, "application/json");
            if (m != HttpMethod.Get && Csrf.Length > 0) req.Headers.Add("X-CSRF-Token", Csrf);
            return req;
        }

        async Task<Jv> Send(HttpMethod m, string path, object body)
        {
            HttpResponseMessage r;
            try { r = await http.SendAsync(Build(m, path, body)); }
            catch (HttpRequestException e) { throw new ApiException(0, Say.EngineUnreachable, Jv.Null); }
            using (r)
            {
                string text = await r.Content.ReadAsStringAsync();
                Jv data = Jv.Null;
                if (text.Length > 0) { try { data = Json.Parse(text); } catch (FormatException) { } }
                if (!r.IsSuccessStatusCode)
                {
                    if ((int)r.StatusCode == 401 && SignedIn) { User = Jv.Null; var h = SessionLost; if (h != null) h(); }
                    string msg = data["error"].Str;
                    if (msg.Length == 0) msg = Say.ForStatus((int)r.StatusCode);
                    // import problems come back as a list; include the first few so the user can act on them
                    var errs = data["errors"].Items.Take(8).Select(e => e.Str).ToList();
                    if (errs.Count > 0) msg += "\n• " + string.Join("\n• ", errs);
                    throw new ApiException((int)r.StatusCode, msg, data);
                }
                return data;
            }
        }

        public async Task<FileResult> GetFile(string path) { return await SendForFile(HttpMethod.Get, path, null); }
        public async Task<FileResult> PostForFile(string path, object body) { return await SendForFile(HttpMethod.Post, path, body); }

        async Task<FileResult> SendForFile(HttpMethod m, string path, object body)
        {
            HttpResponseMessage r;
            try { r = await http.SendAsync(Build(m, path, body)); }
            catch (HttpRequestException e) { throw new ApiException(0, Say.EngineUnreachable, Jv.Null); }
            using (r)
            {
                if (!r.IsSuccessStatusCode)
                {
                    string text = await r.Content.ReadAsStringAsync(); Jv d = Jv.Null;
                    try { d = Json.Parse(text); } catch (FormatException) { }
                    throw new ApiException((int)r.StatusCode, d["error"].Str.Length > 0 ? d["error"].Str : "Request failed (" + (int)r.StatusCode + ")", d);
                }
                string name = "download";
                var cd = r.Content.Headers.ContentDisposition;
                if (cd != null && !string.IsNullOrEmpty(cd.FileName)) name = cd.FileName.Trim('"');
                return new FileResult { Bytes = await r.Content.ReadAsByteArrayAsync(), FileName = name };
            }
        }

        /// <summary>Loads who is signed in (if anyone), their permissions and the anti-forgery token.</summary>
        public async Task<bool> RefreshSession()
        {
            var s = await Get("api/session");
            if (!s["authenticated"].Bool)
            {
                User = Jv.Null; Csrf = ""; Perms = new HashSet<string>(); SetupRequired = s["setupRequired"].Bool; return false;
            }
            User = s["user"]; Csrf = s["csrf"].Str; Company = s["company"].Str; SetupRequired = false; Integrity = s["integrity"];
            Perms = new HashSet<string>(s["permissions"].Items.Select(p => p.Str));
            return true;
        }

        public void Dispose() { http.Dispose(); }
    }
}
