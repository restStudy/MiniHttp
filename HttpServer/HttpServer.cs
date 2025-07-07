// =========================================================================
//  MiniHttp.HttpServer  –  v2.2 (最终典藏版)
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
    /// <summary>
    /// 一个轻量级的、基于 HttpListener 的多功能 HTTP/HTTPS 服务器。
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        #region 1. 字段 / 构造 / 启停

        private readonly HttpListener _listener = new HttpListener();
        private CancellationTokenSource? _cts;

        public int HttpPort { get; }
        public int? HttpsPort { get; }
        public bool IsRunning => _listener.IsListening;
        private bool _isSslBound;

        public HttpServer(int httpPort = 80, int? httpsPort = null)
        {
            HttpPort = httpPort;
            HttpsPort = httpsPort;
        }

        public void Start(X509Certificate2? httpsCert = null)
        {
            if (IsRunning) return;

            if (HttpsPort.HasValue && httpsCert != null)
            {
                // 【强制清场修正】在绑定前，先尝试进行一次清理。
                try
                {
                    SslBinder.Unbind(IPAddress.Any, HttpsPort.Value);
                    Console.WriteLine($"[Info] 启动前预清理端口 {HttpsPort.Value} 成功。");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] 启动前预清理端口失败（可能端口本就干净）: {ex.Message}");
                }

                try
                {
                    SslBinder.Bind(IPAddress.Any, HttpsPort.Value, httpsCert);
                    _isSslBound = true;
                }
                catch (Exception ex)
                {
                    throw new Exception($"为所有IP地址绑定SSL证书到端口 {HttpsPort.Value} 时失败。", ex);
                }
            }

            if (HttpPort > 0) _listener.Prefixes.Add($"http://+:{HttpPort}/");
            if (HttpsPort.HasValue) _listener.Prefixes.Add($"https://+:{HttpsPort.Value}/");
            if (_listener.Prefixes.Count == 0) throw new InvalidOperationException("未指定任何前缀。");

            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            ThreadPool.SetMinThreads(200, 200);
        }

        public void Stop()
        {
            if (!IsRunning && !_isSslBound) return;

            try
            {
                if (IsRunning)
                {
                    _cts?.Cancel();
                    _listener.Stop();
                }
            }
            finally
            {
                if (_isSslBound && HttpsPort.HasValue)
                {
                    try
                    {
                        SslBinder.Unbind(IPAddress.Any, HttpsPort.Value);
                        Console.WriteLine($"[Info] SSL 证书已成功从端口 {HttpsPort.Value} 解除绑定。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] 自动解除SSL证书绑定失败: {ex.Message}");
                        Console.WriteLine(
                            $"[Warning] 您可能需要手动运行 'netsh http delete sslcert ipport=0.0.0.0:{HttpsPort.Value}' 来清理。");
                    }

                    _isSslBound = false;
                }
            }
        }

        public void Dispose() => Stop();

        #endregion

        #region 2. 路由容器

        private readonly ConcurrentDictionary<(string, string, string), Func<HttpListenerContext, Task>> _httpRoutes
            = new ConcurrentDictionary<(string, string, string), Func<HttpListenerContext, Task>>();

        private readonly ConcurrentDictionary<(string, string), Func<WebSocket, Task>> _wsRoutes
            = new ConcurrentDictionary<(string, string), Func<WebSocket, Task>>();

        private readonly ConcurrentDictionary<string, StaticDirEntry> _hostRoots
            = new ConcurrentDictionary<string, StaticDirEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<(string, string), StaticDirEntry> _staticDirs
            = new ConcurrentDictionary<(string, string), StaticDirEntry>();

        private sealed class StaticDirEntry
        {
            public string PhysicalDir = null!;
            public bool EnableBrowse;
        }

        #endregion

        #region 3. 注册 API

        public void MapHostToRoot(string host, string physicalDir, bool browse = false)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("host 不能为空。");

            var fullDir = Path.GetFullPath(physicalDir);
            if (!Directory.Exists(fullDir))
                throw new DirectoryNotFoundException(fullDir);

            _hostRoots[host.ToLowerInvariant()] = new StaticDirEntry
                { PhysicalDir = fullDir, EnableBrowse = browse };
        }

        public void AddRoute(string? host, string method, string path, Func<HttpListenerContext, Task> handler)
        {
            host ??= "*";
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
                throw new ArgumentException("path 必须以 / 开头");
            _httpRoutes[(method.ToUpperInvariant(), host.ToLowerInvariant(), path)] =
                handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void AddTextRoute(string? host, string path, string text, string ct = "text/plain; charset=utf-8") =>
            AddRoute(host, "GET", path, ctx => WriteTextAsync(ctx, text, ct));

        public void AddWebSocket(string? host, string path, Func<WebSocket, Task> onConnected)
        {
            host ??= "*";
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
                throw new ArgumentException("path 必须以 / 开头");
            _wsRoutes[(host.ToLowerInvariant(), path)] =
                onConnected ?? throw new ArgumentNullException(nameof(onConnected));
        }

        public void AddStaticFolder(string? host, string requestPrefix, string physicalDir, bool browse = false)
        {
            host ??= "*";
            if (string.IsNullOrWhiteSpace(requestPrefix) || requestPrefix[0] != '/')
                throw new ArgumentException("requestPrefix 必须以 / 开头");
            requestPrefix = requestPrefix.TrimEnd('/');
            var fullDir = Path.GetFullPath(physicalDir);
            if (!Directory.Exists(fullDir))
                throw new DirectoryNotFoundException(fullDir);

            _staticDirs[(host.ToLowerInvariant(), requestPrefix)] = new StaticDirEntry
                { PhysicalDir = fullDir, EnableBrowse = browse };
        }

        public void AddStaticFolder(string requestPrefix, string physicalDir, bool browse = false) =>
            AddStaticFolder("*", requestPrefix, physicalDir, browse);

        #endregion

        #region 4. Accept & Process (已修复)

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

                _ = Task.Run(() => ProcessAsync(ctx), token);
            }
        }

        // 在 HttpServer.cs 中找到并替换此方法
        private async Task ProcessAsync(HttpListenerContext ctx)
        {
            string host = (ctx.Request.UserHostName ?? "*").ToLowerInvariant();
            string path = ctx.Request.Url!.AbsolutePath;
            string method = ctx.Request.HttpMethod.ToUpperInvariant();

            try
            {
                // === 新的路由处理顺序 ===

                // 1. WebSocket (保持最高优先级)
                if (ctx.Request.IsWebSocketRequest)
                {
                    if (TryWs(host, path, out var wsHandler))
                    {
                        var wsCtx = await ctx.AcceptWebSocketAsync(null);
                        await wsHandler(wsCtx.WebSocket);
                    }
                    else ctx.Response.StatusCode = 404;

                    return;
                }

                // 2. 动态API路由 (优先级提升！)
                if (_httpRoutes.TryGetValue((method, host, path), out var h1) ||
                    _httpRoutes.TryGetValue((method, "*", path), out h1))
                {
                    await h1(ctx);
                    return;
                }

                // 3. 域名根目录映射 (现在是第三优先级)
                if (_hostRoots.TryGetValue(host, out var hostDirEntry))
                {
                    await ServeStaticAsync(ctx, hostDirEntry, path.TrimStart('/'));
                    return;
                }

                if (_hostRoots.TryGetValue("*", out var wildcardDirEntry))
                {
                    await ServeStaticAsync(ctx, wildcardDirEntry, path.TrimStart('/'));
                    return;
                }

                // 4. 静态子目录 (优先级降低)
                if (TryStatic(host, path, out var dir, out var rel))
                {
                    await ServeStaticAsync(ctx, dir, rel);
                    return;
                }

                // 5. 404 Not Found
                ctx.Response.StatusCode = 404;
                await WriteTextAsync(ctx, "404 Not Found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Processing request for {ctx.Request.Url}: {ex.Message}\n{ex.StackTrace}");
                try
                {
                    ctx.Response.StatusCode = 500;
                    await WriteTextAsync(ctx, "500 Internal Server Error");
                }
                catch (Exception responseEx)
                {
                    Console.WriteLine($"[Error] Could not send 500 response: {responseEx.Message}");
                }
            }
            finally
            {
                SafeClose(ctx);
            }
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
                var (hostKey, prefixKey) = kv.Key;

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
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript",
            [".css"] = "text/css",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".svg"] = "image/svg+xml",
            [".json"] = "application/json",
            [".txt"] = "text/plain; charset=utf-8"
        };

        private static string GetMime(string file)
            => _mime.TryGetValue(Path.GetExtension(file), out var m) ? m : "application/octet-stream";

        #endregion

        #region 8. SSL 绑定 (最终典藏版)

        internal static class SslBinder
        {
            [DllImport("httpapi.dll", SetLastError = true)]
            private static extern uint HttpInitialize(HttpApiVersion version, uint flags, IntPtr pReserved);

            [DllImport("httpapi.dll", SetLastError = true)]
            private static extern uint HttpSetServiceConfiguration(IntPtr s, HttpServiceConfigId id, IntPtr p, int l,
                IntPtr o);

            [DllImport("httpapi.dll", SetLastError = true)]
            private static extern uint HttpDeleteServiceConfiguration(IntPtr s, HttpServiceConfigId id, IntPtr p, int l,
                IntPtr o);

            [DllImport("httpapi.dll", SetLastError = true)]
            private static extern uint HttpTerminate(uint flags, IntPtr pReserved);

            [StructLayout(LayoutKind.Sequential, Pack = 2)]
            private struct HttpApiVersion
            {
                public ushort HttpApiMajorVersion;
                public ushort HttpApiMinorVersion;

                public HttpApiVersion(ushort major, ushort minor)
                {
                    HttpApiMajorVersion = major;
                    HttpApiMinorVersion = minor;
                }
            }

            private enum HttpServiceConfigId
            {
                HttpServiceConfigSSLCertInfo = 1
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct HttpServiceConfigSslKey
            {
                public IntPtr pIpPort;

                public HttpServiceConfigSslKey(IntPtr pIpPort)
                {
                    this.pIpPort = pIpPort;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            private struct HttpServiceConfigSslParam
            {
                public int SslHashLength;
                public IntPtr pSslHash;
                public Guid AppId;
                public IntPtr pSslCertStoreName;
                public uint DefaultCertCheckMode;
                public int DefaultRevocationFreshnessTime;
                public int DefaultRevocationUrlRetrievalTimeout;
                public IntPtr pDefaultSslCtlIdentifier;
                public IntPtr pDefaultSslCtlStoreName;
                public uint DefaultFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct HttpServiceConfigSslSet
            {
                public HttpServiceConfigSslKey KeyDesc;
                public HttpServiceConfigSslParam ParamDesc;
            }

            private const uint HTTP_INITIALIZE_CONFIG = 0x00000002;
            private const uint NO_ERROR = 0;

            public static void Bind(IPAddress ipAddress, int port, X509Certificate2 certificate)
            {
                Execute(ipAddress, port,
                    (pSslSet, size) => HttpSetServiceConfiguration(IntPtr.Zero,
                        HttpServiceConfigId.HttpServiceConfigSSLCertInfo, pSslSet, size, IntPtr.Zero), certificate);
            }

            public static void Unbind(IPAddress ipAddress, int port)
            {
                Execute(ipAddress, port,
                    (pSslSet, size) => HttpDeleteServiceConfiguration(IntPtr.Zero,
                        HttpServiceConfigId.HttpServiceConfigSSLCertInfo, pSslSet, size, IntPtr.Zero));
            }

            private static void Execute(IPAddress ipAddress, int port, Func<IntPtr, int, uint> httpApiFunc,
                X509Certificate2? certificate = null)
            {
                var version = new HttpApiVersion(1, 0);
                var retVal = HttpInitialize(version, HTTP_INITIALIZE_CONFIG, IntPtr.Zero);
                if (retVal != NO_ERROR) throw new Win32Exception((int)retVal, "HttpInitialize failed.");

                var ipEndPoint = new IPEndPoint(ipAddress, port);
                SocketAddress sockAddr = ipEndPoint.Serialize();
                var sockAddrPtr = Marshal.AllocHGlobal(sockAddr.Size);
                GCHandle? hashHandle = null;
                var pSslSet = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(HttpServiceConfigSslSet)));
                IntPtr pCertStoreName = IntPtr.Zero;

                try
                {
                    for (int i = 0; i < sockAddr.Size; i++) Marshal.WriteByte(sockAddrPtr, i, sockAddr[i]);

                    var sslSet = new HttpServiceConfigSslSet { KeyDesc = new HttpServiceConfigSslKey(sockAddrPtr) };

                    if (certificate != null)
                    {
                        var certHash = certificate.GetCertHash() ??
                                       throw new ArgumentException("Certificate has no hash.");
                        hashHandle = GCHandle.Alloc(certHash, GCHandleType.Pinned);
                        pCertStoreName = Marshal.StringToHGlobalUni("MY");
                        sslSet.ParamDesc = new HttpServiceConfigSslParam
                        {
                            SslHashLength = certHash.Length,
                            pSslHash = hashHandle.Value.AddrOfPinnedObject(),
                            pSslCertStoreName = pCertStoreName,
                            AppId = Guid.NewGuid()
                        };
                    }

                    Marshal.StructureToPtr(sslSet, pSslSet, false);
                    retVal = httpApiFunc(pSslSet, Marshal.SizeOf(typeof(HttpServiceConfigSslSet)));

                    const int ERROR_FILE_NOT_FOUND = 2; // This is OK for delete operations.
                    if (retVal != NO_ERROR && retVal != ERROR_FILE_NOT_FOUND)
                    {
                        throw new Win32Exception((int)retVal, "HTTP API call failed.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(sockAddrPtr);
                    Marshal.FreeHGlobal(pSslSet);
                    if (hashHandle?.IsAllocated == true) hashHandle.Value.Free();
                    if (pCertStoreName != IntPtr.Zero) Marshal.FreeHGlobal(pCertStoreName);
                    HttpTerminate(0, IntPtr.Zero);
                }
            }
        }

        #endregion
    }
}