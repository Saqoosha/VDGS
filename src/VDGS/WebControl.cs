using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace VDGS
{
    /// <summary>
    /// Small HTTP control surface so the mod can be driven from a browser instead of
    /// from in-game keys.
    ///
    /// Every keyboard binding tried so far collided with something: F7 saves the scene,
    /// the arrow keys drive the track editor, and the numpad does not exist on a laptop.
    /// On top of that the game has no place to draw a HUD, so an in-game workflow gives
    /// no feedback at all. Moving control out of the process solves all of it at once,
    /// and works from another machine over Tailscale while Parsec shows the game.
    ///
    /// Splat data is NOT uploaded through here - captures run to hundreds of megabytes
    /// and belong next to the game already (tools/deploy.sh). This only lists what is
    /// on disk and records which capture belongs to which track.
    ///
    /// Requests are handled on a listener thread, but Unity objects may only be touched
    /// on the main thread, so actions are queued and drained from Update().
    /// </summary>
    internal class WebControl : IDisposable
    {
        internal const int kDefaultPort = 8777;

        private readonly HttpListener m_Listener = new HttpListener();
        private readonly Queue<Action> m_MainThreadWork = new Queue<Action>();
        private readonly object m_Lock = new object();
        private Thread m_Thread;
        private volatile bool m_Running;

        /// <summary>Supplied by the plugin; all of these run on the main thread.</summary>
        internal Func<object> StatusProvider;
        internal Action<string> LoadSplat;      // null/empty unloads everything
        internal Action<string[]> BindCurrent;  // binds the given splats to the live track
        internal Action<string> UnbindTrack;    // null clears the live track's binding

        internal string Url { get; private set; }

        internal bool Start(int port, StringBuilder log)
        {
            try
            {
                // '+' would need an URL ACL / admin rights; localhost does not, and the
                // machine is reached over Tailscale via port forwarding or a direct bind.
                Url = "http://*:" + port + "/";
                m_Listener.Prefixes.Add(Url);
                m_Listener.Start();
            }
            catch (Exception e)
            {
                log?.AppendLine("web control: wildcard bind failed (" + e.Message + "), trying localhost");
                try
                {
                    m_Listener.Prefixes.Clear();
                    Url = "http://localhost:" + port + "/";
                    m_Listener.Prefixes.Add(Url);
                    m_Listener.Start();
                }
                catch (Exception e2)
                {
                    log?.AppendLine("web control: failed to listen - " + e2.Message);
                    return false;
                }
            }

            m_Running = true;
            m_Thread = new Thread(Loop) { IsBackground = true, Name = "VDGS web" };
            m_Thread.Start();
            log?.AppendLine("web control listening on " + Url);
            return true;
        }

        private void Loop()
        {
            while (m_Running)
            {
                HttpListenerContext ctx;
                try { ctx = m_Listener.GetContext(); }
                catch { if (m_Running) continue; else break; }

                try { Handle(ctx); }
                catch (Exception e)
                {
                    try { Respond(ctx, 500, "{\"error\":" + JsonConvert.ToString(e.Message) + "}"); }
                    catch { }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path.Length == 0) path = "/";

            switch (path)
            {
                case "/":
                case "/index.html":
                    Respond(ctx, 200, WebUi.Html, "text/html; charset=utf-8");
                    return;

                case "/api/status":
                    Respond(ctx, 200, JsonConvert.SerializeObject(RunOnMain(StatusProvider), Formatting.Indented));
                    return;

                case "/api/load":
                {
                    var body = ReadBody(ctx);
                    var req = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);
                    string splat = null;
                    req?.TryGetValue("splat", out splat);
                    QueueOnMain(() => LoadSplat?.Invoke(splat));
                    Respond(ctx, 200, "{\"ok\":true}");
                    return;
                }

                case "/api/unload":
                    QueueOnMain(() => LoadSplat?.Invoke(null));
                    Respond(ctx, 200, "{\"ok\":true}");
                    return;

                case "/api/bind":
                {
                    var body = ReadBody(ctx);
                    var req = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(body);
                    List<string> splats = null;
                    req?.TryGetValue("splats", out splats);
                    var arr = splats != null ? splats.ToArray() : new string[0];
                    QueueOnMain(() => BindCurrent?.Invoke(arr));
                    Respond(ctx, 200, "{\"ok\":true}");
                    return;
                }

                case "/api/unbind":
                {
                    // No track given means "the one loaded right now"; a name lets the
                    // bindings list delete any entry, including tracks that are not open.
                    var body = ReadBody(ctx);
                    string track = null;
                    if (!string.IsNullOrEmpty(body))
                    {
                        var req = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);
                        req?.TryGetValue("track", out track);
                    }
                    var t = track;
                    QueueOnMain(() => UnbindTrack?.Invoke(t));
                    Respond(ctx, 200, "{\"ok\":true}");
                    return;
                }

                default:
                    Respond(ctx, 404, "{\"error\":\"not found\"}");
                    return;
            }
        }

        private static string ReadBody(HttpListenerContext ctx)
        {
            using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static void Respond(HttpListenerContext ctx, int status, string body,
                                    string contentType = "application/json; charset=utf-8")
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            // The UI may be served from a different origin while developing.
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private void QueueOnMain(Action a)
        {
            lock (m_Lock) m_MainThreadWork.Enqueue(a);
        }

        /// <summary>
        /// Runs a producer on the main thread and waits for it. Status has to be read
        /// from live Unity objects, which is illegal off the main thread.
        /// </summary>
        private object RunOnMain(Func<object> f)
        {
            if (f == null) return null;

            object result = null;
            var done = new ManualResetEventSlim(false);
            QueueOnMain(() =>
            {
                try { result = f(); }
                finally { done.Set(); }
            });

            // If the game is paused or shutting down the pump may never run; do not hang.
            if (!done.Wait(TimeSpan.FromSeconds(5)))
                return new Dictionary<string, object> { { "error", "main thread did not respond" } };
            return result;
        }

        /// <summary>Call once per frame from the plugin's Update.</summary>
        internal void Pump()
        {
            while (true)
            {
                Action a;
                lock (m_Lock)
                {
                    if (m_MainThreadWork.Count == 0) return;
                    a = m_MainThreadWork.Dequeue();
                }
                try { a(); }
                catch (Exception e) { VdgsPlugin.Log.LogError("web action failed: " + e); }
            }
        }

        public void Dispose()
        {
            m_Running = false;
            try { m_Listener.Stop(); } catch { }
            try { m_Listener.Close(); } catch { }
        }
    }
}
