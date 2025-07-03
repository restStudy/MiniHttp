// =========================================================================
//  MiniHttp.HttpServer  –  轻量级 HTTP/HTTPS/WebSocket 服务器
//  目标框架：.NET Framework 4.8      语言版本：C# 8.0+
// =========================================================================

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttp
{
    public sealed class HttpServer : IDisposable
    {
        #region 1. 字段 / 构造 / 启停

        private readonly HttpListener _listener = new HttpListener();
        private CancellationTokenSource? _cts;

        public int HttpPort { get; }
        public int? HttpsPort { get; }

        public bool IsRunning => _listener.IsListening;

        public HttpServer(int httpPort = 80, int? httpsPort = null)
        {
            HttpPort = httpPort;
            HttpsPort = httpsPort;
        }

        public void Start(X509Certificate2? httpsCert = null)
        {
            if (IsRunning) return;

            // 如需 HTTPS 且传入证书，则自动绑定（首次需管理员）
            if (HttpsPort.HasValue && httpsCert != null)
                SslBinder.Bind(HttpsPort.Value, httpsCert);

            if (HttpPort > 0)
                _listener.Prefixes.Add($"http://+:{HttpPort}/");
            if (HttpsPort.HasValue)
                _listener.Prefixes.Add($"https://+:{HttpsPort.Value}/");

            if (_listener.Prefixes.Count == 0)
                throw new InvalidOperationException("未指定任何前缀。");

            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));

            ThreadPool.SetMinThreads(200, 200); // 预热线程池（可酌情调整）
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            _listener.Stop();
        }

        public void Dispose() => Stop();

        #endregion

        #region 2. 路由容器

        private readonly ConcurrentDictionary<(string, string, string), Func<HttpListenerContext, Task>> _httpRoutes
            = new ConcurrentDictionary<(string, string, string), Func<HttpListenerContext, Task>>();

        private readonly ConcurrentDictionary<(string, string), Func<WebSocket, Task>> _wsRoutes
            = new ConcurrentDictionary<(string, string), Func<WebSocket, Task>>();

        private readonly ConcurrentDictionary<(string, string), StaticDirEntry> _staticDirs
            = new ConcurrentDictionary<(string, string), StaticDirEntry>();

        private sealed class StaticDirEntry
        {
            public string PhysicalDir = null!;
            public bool EnableBrowse;
        }

        #endregion

        #region 3. 注册 API

        /*----- 动态 HTTP -----*/
        public void AddRoute(string? host, string method, string path,
            Func<HttpListenerContext, Task> handler)
        {
            host ??= "*";
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
                throw new ArgumentException("path 必须以 / 开头");
            _httpRoutes[(method.ToUpperInvariant(), host.ToLowerInvariant(), path)] =
                handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void AddTextRoute(string? host, string path, string text,
            string ct = "text/plain; charset=utf-8") =>
            AddRoute(host, "GET", path,
                ctx => WriteTextAsync(ctx, text, ct));

        /*----- WebSocket -----*/
        public void AddWebSocket(string? host, string path, Func<WebSocket, Task> onConnected)
        {
            host ??= "*";
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
                throw new ArgumentException("path 必须以 / 开头");
            _wsRoutes[(host.ToLowerInvariant(), path)] =
                onConnected ?? throw new ArgumentNullException(nameof(onConnected));
        }

        /*----- 静态目录 -----*/
        public void AddStaticFolder(string? host, string requestPrefix,
            string physicalDir, bool browse = false)
        {
            host ??= "*";
            if (string.IsNullOrWhiteSpace(requestPrefix) || requestPrefix[0] != '/')
                throw new ArgumentException("requestPrefix 必须以 / 开头");
            requestPrefix = requestPrefix.TrimEnd('/'); // "/static"
            var fullDir = Path.GetFullPath(physicalDir);
            if (!Directory.Exists(fullDir))
                throw new DirectoryNotFoundException(fullDir);

            _staticDirs[(host.ToLowerInvariant(), requestPrefix)] = new StaticDirEntry
                { PhysicalDir = fullDir, EnableBrowse = browse };
        }

        public void AddStaticFolder(string requestPrefix, string physicalDir, bool browse = false) =>
            AddStaticFolder("*", requestPrefix, physicalDir, browse);

        #endregion

        #region 4. Accept & Process

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => ProcessAsync(ctx));
            }
        }

        private async Task ProcessAsync(HttpListenerContext ctx)
        {
            string host = (ctx.Request.UserHostName ?? "*").ToLowerInvariant();
            string path = ctx.Request.Url!.AbsolutePath;
            string method = ctx.Request.HttpMethod.ToUpperInvariant();

            /*1) WebSocket*/
            if (ctx.Request.IsWebSocketRequest)
            {
                if (TryWs(host, path, out var wsHandler))
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    await wsHandler(wsCtx.WebSocket);
                }
                else ctx.Response.StatusCode = 404;

                SafeClose(ctx);
                return;
            }

            /*2) 静态目录*/
            if (TryStatic(host, path, out var dir, out var rel))
            {
                await ServeStaticAsync(ctx, dir, rel);
                return;
            }

            /*3) 动态路由*/
            if (_httpRoutes.TryGetValue((method, host, path), out var h1) ||
                _httpRoutes.TryGetValue((method, "*", path), out h1))
            {
                await h1(ctx);
                SafeClose(ctx);
                return;
            }

            /*4) 404*/
            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "404 Not Found");
            SafeClose(ctx);
        }

        #endregion

        #region 5. 路由查找辅助

        private bool TryWs(string h, string p, out Func<WebSocket, Task> handler)
        {
            if (_wsRoutes.TryGetValue((h, p), out handler) ||
                _wsRoutes.TryGetValue(("*", p), out handler))
                return true;
            handler = null!;
            return false;
        }

        private bool TryStatic(string h, string p,
            out StaticDirEntry entry, out string rel)
        {
            foreach (var kv in _staticDirs)
            {
                var (hostKey, prefixKey) = kv.Key; // 拆包

                if (hostKey != "*" && hostKey != h) continue;
                if (p.StartsWith(prefixKey, StringComparison.OrdinalIgnoreCase))
                {
                    rel = p.Substring(prefixKey.Length).TrimStart('/');
                    entry = kv.Value;
                    return true;
                }
            }

            rel = null!;
            entry = null!;
            return false;
        }

        #endregion

        #region 6. 静态文件

        private async Task ServeStaticAsync(HttpListenerContext ctx,
            StaticDirEntry dir, string relPath)
        {
            string local = Path.GetFullPath(Path.Combine(dir.PhysicalDir, relPath));
            if (!local.StartsWith(dir.PhysicalDir, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                SafeClose(ctx);
                return;
            }

            if (Directory.Exists(local))
            {
                foreach (var def in new[] { "index.html", "default.htm" })
                {
                    var defFile = Path.Combine(local, def);
                    if (File.Exists(defFile))
                    {
                        local = defFile;
                        goto SEND;
                    }
                }

                if (dir.EnableBrowse)
                {
                    await WriteTextAsync(ctx, RenderDir(local, ctx.Request.Url!.AbsolutePath),
                        "text/html; charset=utf-8");
                }
                else ctx.Response.StatusCode = 403;

                SafeClose(ctx);
                return;
            }

            if (!File.Exists(local))
            {
                ctx.Response.StatusCode = 404;
                SafeClose(ctx);
                return;
            }

            SEND:
            var fi = new FileInfo(local);

            string etag = $"\"{fi.Length}-{fi.LastWriteTimeUtc.Ticks}\"";
            ctx.Response.Headers["ETag"] = etag;
            ctx.Response.Headers["Last-Modified"] = fi.LastWriteTimeUtc.ToString("R");

            if (ctx.Request.Headers["If-None-Match"] == etag ||
                (DateTime.TryParse(ctx.Request.Headers["If-Modified-Since"], out var ims) &&
                 Math.Abs((ims - fi.LastWriteTimeUtc).TotalSeconds) < 1))
            {
                ctx.Response.StatusCode = 304;
                SafeClose(ctx);
                return;
            }

            long start = 0, end = fi.Length - 1;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            var range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
            {
                var sp = range.Substring(6).Split('-');
                start = long.Parse(sp[0]);
                if (sp.Length > 1 && long.TryParse(sp[1], out var e)) end = e;
                if (start > end)
                {
                    ctx.Response.StatusCode = 416;
                    SafeClose(ctx);
                    return;
                }

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fi.Length}";
            }

            ctx.Response.ContentType = GetMime(local);
            ctx.Response.ContentLength64 = end - start + 1;

            using var fs = File.OpenRead(local);
            fs.Seek(start, SeekOrigin.Begin);
            byte[] buf = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long remain = end - start + 1;
                while (remain > 0)
                {
                    int read = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remain));
                    if (read == 0) break;
                    await ctx.Response.OutputStream.WriteAsync(buf, 0, read);
                    remain -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
                SafeClose(ctx);
            }
        }

        private static string RenderDir(string path, string req)
        {
            var sb = new StringBuilder($"<h3>Index of {req}</h3><ul>");
            foreach (var d in Directory.GetDirectories(path))
                sb.Append($"<li><a href=\"{req.TrimEnd('/')}/{Path.GetFileName(d)}/\">{Path.GetFileName(d)}/</a></li>");
            foreach (var f in Directory.GetFiles(path))
                sb.Append($"<li><a href=\"{req.TrimEnd('/')}/{Path.GetFileName(f)}\">{Path.GetFileName(f)}</a></li>");
            sb.Append("</ul>");
            return sb.ToString();
        }

        #endregion

        #region 7. 工具

        private static async Task WriteTextAsync(HttpListenerContext ctx, string txt,
            string ct = "text/plain; charset=utf-8")
        {
            var b = Encoding.UTF8.GetBytes(txt);
            ctx.Response.ContentType = ct;
            ctx.Response.ContentLength64 = b.Length;
            await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length);
        }

        private static void SafeClose(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.Close();
            }
            catch
            {
            }
        }

        private static readonly Dictionary<string, string> _mime = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript", [".css"] = "text/css",
            [".png"] = "image/png", [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
            [".svg"] = "image/svg+xml", [".json"] = "application/json",
            [".txt"] = "text/plain; charset=utf-8"
        };

        private static string GetMime(string file)
            => _mime.TryGetValue(Path.GetExtension(file), out var m) ? m : "application/octet-stream";

        #endregion

        #region 8. SSL 绑定(可选)

        private static class SslBinder
        {
            public static void Bind(int port, X509Certificate2 cert)
            {
                string ipPort = $"0.0.0.0:{port}";
                var cfg = new HTTP_SERVICE_CONFIG_SSL_SET
                {
                    Key = new HTTP_SERVICE_CONFIG_SSL_KEY(ipPort),
                    Param = HTTP_SERVICE_CONFIG_SSL_PARAM.Create(cert)
                };
                uint ret = HttpSetServiceConfiguration(IntPtr.Zero,
                    HTTP_SERVICE_CONFIG_ID.HttpServiceConfigSSLCertInfo,
                    ref cfg, Marshal.SizeOf(cfg), IntPtr.Zero);

                const uint ERR_EXISTS = 183;
                if (ret != 0 && ret != ERR_EXISTS)
                    throw new Win32Exception((int)ret, "SSL 证书绑定失败。");
            }

            private enum HTTP_SERVICE_CONFIG_ID
            {
                HttpServiceConfigSSLCertInfo = 1
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct HTTP_SERVICE_CONFIG_SSL_SET
            {
                public HTTP_SERVICE_CONFIG_SSL_KEY Key;
                public HTTP_SERVICE_CONFIG_SSL_PARAM Param;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct HTTP_SERVICE_CONFIG_SSL_KEY
            {
                public IntPtr pIpPort;

                public HTTP_SERVICE_CONFIG_SSL_KEY(string s) =>
                    pIpPort = Marshal.StringToHGlobalUni(s);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct HTTP_SERVICE_CONFIG_SSL_PARAM
            {
                public uint SslHashLength;
                public IntPtr pSslHash;
                public Guid AppId;
                public IntPtr pStoreName;

                public uint DefaultCertCheckMode,
                    DefaultRevocationFreshnessTime,
                    DefaultRevocationUrlRetrievalTimeout;

                public IntPtr pDefaultSslCtlIdentifier, pDefaultSslCtlStoreName;
                public uint DefaultFlags;

                public static HTTP_SERVICE_CONFIG_SSL_PARAM Create(X509Certificate2 cert)
                {
                    byte[] hash = cert.GetCertHash();
                    IntPtr hashPtr = Marshal.AllocHGlobal(hash.Length);
                    Marshal.Copy(hash, 0, hashPtr, hash.Length);

                    return new HTTP_SERVICE_CONFIG_SSL_PARAM
                    {
                        SslHashLength = (uint)hash.Length,
                        pSslHash = hashPtr,
                        AppId = Guid.NewGuid(),
                        pStoreName = Marshal.StringToHGlobalUni("MY")
                    };
                }
            }

            [DllImport("httpapi.dll", SetLastError = true)]
            private static extern uint HttpSetServiceConfiguration(
                IntPtr service, HTTP_SERVICE_CONFIG_ID id,
                ref HTTP_SERVICE_CONFIG_SSL_SET info, int cbInfo, IntPtr overlapped);
        }

        #endregion
    }
}