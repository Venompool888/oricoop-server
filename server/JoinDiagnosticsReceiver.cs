using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OriCoop.Authority;

namespace OriCoop.CServer
{
    // A TLS reverse proxy forwards this single path to the loopback listener. The UDP
    // game loop only registers a short-lived grant and never waits for a report or disk.
    internal sealed class JoinDiagnosticsReceiver : IDisposable
    {
        internal const string Route = "/oricoop/diagnostics/v1";
        internal const int MaxBytes = 2048;
        const int MaxGrants = 2048, MaxConcurrent = 8, MaxPerMinute = 60;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly HttpListener listener = new HttpListener();
        readonly Dictionary<string, Grant> grants = new Dictionary<string, Grant>(StringComparer.Ordinal);
        readonly object storage = new object();
        readonly string directory;
        readonly int retentionDays;
        readonly Func<DateTime> utcNow;
        DateTime rateWindow;
        int accepted, handling;
        Timer cleanup;

        sealed class Grant
        {
            public string Capability;
            public DateTime Expires;
        }

        internal JoinDiagnosticsReceiver(string privateParent, int days, Func<DateTime> clock)
        {
            if (String.IsNullOrWhiteSpace(privateParent) || !Path.IsPathRooted(privateParent) ||
                !Directory.Exists(privateParent)) throw new ArgumentException("diagnostics-dir must be an existing absolute directory");
            if (days < 1 || days > 365) throw new ArgumentException("diagnostics-days must be 1 through 365");
            directory = Path.Combine(privateParent, "join-reports");
            retentionDays = days;
            utcNow = clock;
        }

        internal void Start(int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentException("diagnostics-port must be 1 through 65535");
            PreparePrivateDirectory();
            Prune();
            listener.Prefixes.Add("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
            listener.Start();
            listener.BeginGetContext(Accept, null);
            cleanup = new Timer(delegate {
                try { Prune(); }
                catch (IOException) { Console.Error.WriteLine("Diagnostics retention cleanup failed"); }
                catch (UnauthorizedAccessException) { Console.Error.WriteLine("Diagnostics retention cleanup denied"); }
            }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        void PreparePrivateDirectory()
        {
            // The operator chooses a parent under their control; never serve these files over HTTP.
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("diagnostics storage cannot be a link");
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(directory, mode);
                File.SetUnixFileMode(directory, mode);
                return;
            }
#endif
            DirectorySecurity acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            DirectoryInfo info = new DirectoryInfo(directory);
            info.Create(acl);
            info.SetAccessControl(acl);
        }

        internal void Prune()
        {
            DateTime oldestDay = utcNow().Date.AddDays(1 - retentionDays);
            lock (storage)
            {
                foreach (string path in Directory.GetFiles(directory, "report-*.form"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    DateTime day;
                    if (DateTime.TryParseExact(name, "'report-'yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out day) && day < oldestDay) File.Delete(path);
                }
            }
        }

        internal void RegisterJoin(byte[] token)
        {
            string id = JoinDiagnosticsKey.RequestId(token);
            string capability = JoinDiagnosticsKey.Capability(token);
            DateTime now = utcNow();
            lock (grants)
            {
                if (grants.Count >= MaxGrants)
                {
                    List<string> expired = new List<string>();
                    foreach (KeyValuePair<string, Grant> pair in grants)
                        if (pair.Value.Expires <= now) expired.Add(pair.Key);
                    foreach (string old in expired) grants.Remove(old);
                    if (grants.Count >= MaxGrants && !grants.ContainsKey(id)) return;
                }
                grants[id] = new Grant { Capability = capability, Expires = now.AddMinutes(2) };
            }
        }

        Grant FindGrant(string id)
        {
            Grant result;
            if (id == null || id.Length != 64 || !grants.TryGetValue(id, out result)) return null;
            if (result.Expires > utcNow()) return result;
            grants.Remove(id);
            return null;
        }

        void Accept(IAsyncResult result)
        {
            HttpListenerContext context = null;
            try
            {
                context = listener.EndGetContext(result);
            }
            catch (ObjectDisposedException) { return; }
            catch (HttpListenerException) { if (!listener.IsListening) return; }
            try { listener.BeginGetContext(Accept, null); }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
            if (context != null) Handle(context);
        }

        async void Handle(HttpListenerContext context)
        {
            int count = Interlocked.Increment(ref handling);
            int status = 503;
            try
            {
                context.Response.KeepAlive = false;
                context.Response.Headers["Cache-Control"] = "no-store";
                status = count > MaxConcurrent ? 429 : await Process(context);
            }
            // async void: an exception that escapes here would end the whole C-server process (every room),
            // so a failed report (disconnect, disposal during a read/write, disk error) is only that request's failure.
            catch (Exception) { }
            finally
            {
                try
                {
                    if (status != 200) context.Response.StatusCode = status;
                    context.Response.Close();
                }
                catch (Exception) { } // response already closed or headers sent to a disconnected peer
                Interlocked.Decrement(ref handling);
            }
        }

        async Task<int> Process(HttpListenerContext context)
        {
            var request = context.Request;
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.RawUrl != Route) return 404;
            string id = request.Headers["X-OriCoop-Join"];
            Grant grant;
            lock (grants) { grant = FindGrant(id); }
            if (grant == null) return 404;
            if (request.HttpMethod == "GET")
            {
                byte[] marker = Encoding.ASCII.GetBytes(grant.Capability);
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = marker.Length;
                context.Response.OutputStream.Write(marker, 0, marker.Length);
                return 200;
            }
            if (request.HttpMethod != "POST") return 405;
            if (!String.Equals((request.ContentType ?? "").Split(';')[0].Trim(),
                "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) return 415;
            if (request.ContentLength64 < 1 || request.ContentLength64 > MaxBytes) return 413;
            byte[] body = new byte[(int)request.ContentLength64];
            using (var deadlineTimer = new CancellationTokenSource())
            {
                Task deadline = Task.Delay(5000, deadlineTimer.Token);
                try
                {
                    for (int used = 0; used < body.Length; )
                    {
                        Task<int> read = request.InputStream.ReadAsync(body, used, body.Length - used);
                        if (await Task.WhenAny(read, deadline) != read) return 408;
                        int length = await read;
                        if (length == 0) return 400;
                        used += length;
                    }
                }
                finally { deadlineTimer.Cancel(); }
            }
            string report = Validate(body);
            if (report == null) return 400;
            DateTime now = utcNow();
            lock (grants)
            {
                if (FindGrant(id) != grant) return 404;
                if (now >= rateWindow.AddMinutes(1)) { rateWindow = now; accepted = 0; }
                if (accepted >= MaxPerMinute) return 429;
                accepted++;
                grants.Remove(id);
            }
            lock (storage)
            {
                string path = Path.Combine(directory, "report-" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".form");
                File.AppendAllText(path, report + "\n", Utf8);
            }
            return 204;
        }

        internal static string Validate(byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > MaxBytes) return null;
            // Forms use ASCII transport and percent-escape non-ASCII bytes. All v1 fields have ASCII values.
            for (int i = 0; i < body.Length; i++) if (body[i] < 32 || body[i] > 126) return null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var canonical = new List<string>();
            foreach (string pair in Encoding.ASCII.GetString(body).Split('&'))
            {
                int split = pair.IndexOf('=');
                if (split < 1) return null;
                string key = pair.Substring(0, split);
                string value = Uri.UnescapeDataString(pair.Substring(split + 1).Replace("+", " "));
                if (values.ContainsKey(key) || !JoinDiagnosticsFields.Valid(key, value)) return null;
                values.Add(key, value);
                canonical.Add(key + "=" + Uri.EscapeDataString(value));
            }
            if (!values.ContainsKey("v") || !values.ContainsKey("probe")) return null;
            bool success = values["probe"] == "ok";
            if (values.ContainsKey("ip") != success || values.ContainsKey("provider") != success) return null;
            return String.Join("&", canonical.ToArray());
        }

        public void Dispose()
        {
            if (cleanup != null) cleanup.Dispose();
            listener.Close();
        }
    }
}
