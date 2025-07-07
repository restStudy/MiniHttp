// 文件: Program.cs (终极多站点平台 - 修复版 v1.1)

using MiniHttp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HttpServerDemo
{
    class Program
    {
        // [修复] 使用 ConcurrentDictionary 替代 ConcurrentBag，方便安全地移除客户端。
        // Key 是 WebSocket 对象，Value 可以是任意占位符。
        static readonly ConcurrentDictionary<WebSocket, byte> WsClients = new ConcurrentDictionary<WebSocket, byte>();

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            Console.WriteLine("--- MiniHttp 终极多站点平台 ---");
            Console.WriteLine("===============================");

            //创建本地多域名测试：
            //New-SelfSignedCertificate -DnsName "fantian28.com", "zhuimeng28.com" -CertStoreLocation "cert:\LocalMachine\My" -FriendlyName "Multi-Domain Dev Cert" -NotAfter (Get-Date).AddYears(5)
            // 

            // --- 步骤 1: 加载多域名证书 ---
            Console.WriteLine("\n[1] 正在加载多域名(SAN)证书...");
            string thumbprint = "A0201E99D353C3095EB0CE4B840FA8DE0F2EDFFD"; // <--- 粘贴您创建的多域名证书指纹
            X509Certificate2? certificate = LoadCertificateByThumbprint(thumbprint);
            if (certificate == null) { Console.ReadKey(); return; }
            Console.WriteLine($"成功加载证书: {certificate.Subject} (FriendlyName: {certificate.FriendlyName})");

            // --- 步骤 2: 初始化并进行平台化配置 ---
            Console.WriteLine("\n[2] 正在配置平台路由...");
            var server = new HttpServer(httpPort: 80, httpsPort: 443);
            string websitesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Websites");
            Directory.CreateDirectory(websitesRoot);

            // 2.1) fantian28.com - 主站配置
            string fantianRoot = Path.Combine(websitesRoot, "fantian28.com");
            Directory.CreateDirectory(fantianRoot);
            server.MapHostToRoot("fantian28.com", fantianRoot);
            Console.WriteLine($"  - 域名 'fantian28.com' 已映射到: {fantianRoot}");

            server.AddRoute("fantian28.com", "GET", "/api/v1/info", ctx => WriteTextAsync(ctx, "Hello from fantian28.com GET API!"));
            Console.WriteLine("  - 已添加API: GET fantian28.com/api/v1/info");

            server.AddRoute("fantian28.com", "POST", "/api/v1/user", async ctx => {
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                {
                    string json = await reader.ReadToEndAsync();
                    await WriteTextAsync(ctx, $"Received JSON: {json}");
                }
            });
            Console.WriteLine("  - 已添加API: POST fantian28.com/api/v1/user");

            // 2.2) zhuimeng28.com - 副站配置
            string zhuimengRoot = Path.Combine(websitesRoot, "zhuimeng28.com");
            Directory.CreateDirectory(zhuimengRoot);
            server.MapHostToRoot("zhuimeng28.com", zhuimengRoot);
            Console.WriteLine($"  - 域名 'zhuimeng28.com' 已映射到: {zhuimengRoot}");

            // 2.3) 共享目录配置 (对所有域名有效)
            string sharedRoot = Path.Combine(websitesRoot, "SharedAssets");
            Directory.CreateDirectory(sharedRoot);
            server.AddStaticFolder("*", "/shared", sharedRoot);
            Console.WriteLine($"  - 共享目录 '*/shared' 已映射到: {sharedRoot}");

            // 2.4) 下载目录配置 (仅对 fantian28.com 有效)
            string downloadsRoot = Path.Combine(websitesRoot, "Downloads");
            Directory.CreateDirectory(downloadsRoot);
            server.AddRoute("fantian28.com", "GET", "/downloads/user_manual.zip", ctx =>
                ServeDownloadableFileAsync(ctx, Path.Combine(downloadsRoot, "user_manual.zip")));
            Console.WriteLine($"  - 下载服务 'fantian28.com/downloads' 已配置");

            // 2.5) 文件上传接口 (对所有域名有效)
            string uploadsRoot = Path.Combine(websitesRoot, "Uploads");
            Directory.CreateDirectory(uploadsRoot);
            server.AddRoute("*", "POST", "/upload", ctx => HandleFileUploadAsync(ctx, uploadsRoot));
            Console.WriteLine("  - 文件上传接口 '*/upload' 已配置");

            // 2.6) 共享WebSocket聊天服务 (对所有域名有效)
            server.AddWebSocket("*", "/ws/chat", HandleWebSocketChatAsync);
            Console.WriteLine("  - 共享WebSocket聊天室 '*/ws/chat' 已配置");

            // --- 步骤 3: 启动服务器 ---
            try
            {
                Console.WriteLine("\n[3] 正在启动服务器...");
                server.Start(certificate);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n🎉🎉🎉 终极多站点平台启动成功! 🎉🎉🎉");
                Console.ResetColor();
                Console.WriteLine("请在浏览器中测试 (推荐使用HTTPS):");
                Console.WriteLine("  - 主站: https://fantian28.com:443");
                Console.WriteLine("  - 副站: https://zhuimeng28.com:443");
                Console.WriteLine("\n按任意键停止服务器...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n🔥🔥🔥 服务器启动失败! 🔥🔥🔥");
                for (var e = ex; e != null; e = e.InnerException) Console.WriteLine($"  - {e.Message}");
                Console.ResetColor();
                Console.ReadKey();
            }
            finally
            {
                server.Stop();
                Console.WriteLine("\n服务器已停止。");
            }
        }

        // --- 辅助方法 ---

        static Task WriteTextAsync(HttpListenerContext ctx, string txt, string ct = "text/plain; charset=utf-8")
        {
            var b = Encoding.UTF8.GetBytes(txt);
            ctx.Response.ContentType = ct;
            ctx.Response.ContentLength64 = b.Length;
            return ctx.Response.OutputStream.WriteAsync(b, 0, b.Length);
        }

        static async Task ServeDownloadableFileAsync(HttpListenerContext ctx, string filePath)
        {
            if (!File.Exists(filePath))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            var fi = new FileInfo(filePath);
            ctx.Response.ContentLength64 = fi.Length;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fi.Name}\"");
            using (var fs = File.OpenRead(filePath))
            {
                await fs.CopyToAsync(ctx.Response.OutputStream);
            }
        }

        // [修复] 重写整个文件上传处理方法，使其能正确处理二进制数据。
        static async Task HandleFileUploadAsync(HttpListenerContext ctx, string uploadDir)
        {
            try
            {
                // multipart/form-data 的解析是复杂的，这里是简化的实现
                var boundaryBytes = Encoding.UTF8.GetBytes("--" + ctx.Request.ContentType.Split(';')[1].Split('=')[1]);
                using (var ms = new MemoryStream())
                {
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    var bodyBytes = ms.ToArray();

                    var boundaryPositions = FindAllOccurrences(bodyBytes, boundaryBytes);
                    if (boundaryPositions.Count < 2) throw new Exception("Invalid multipart data.");

                    for (int i = 0; i < boundaryPositions.Count - 1; i++)
                    {
                        var start = boundaryPositions[i];
                        var end = boundaryPositions[i + 1];
                        var partBytes = new byte[end - start];
                        Array.Copy(bodyBytes, start, partBytes, 0, partBytes.Length);

                        var partAsString = Encoding.UTF8.GetString(partBytes);
                        if (partAsString.Contains("filename=\""))
                        {
                            var headerMatch = System.Text.RegularExpressions.Regex.Match(partAsString, "filename=\"(.*?)\"");
                            var filename = headerMatch.Groups[1].Value;
                            var safeFilename = Path.GetFileName(filename); // 清理路径，防止目录遍历攻击

                            // 找到文件内容的起始位置 (\r\n\r\n)
                            var contentStartIndex = FindSequence(partBytes, new byte[] { 13, 10, 13, 10 }) + 4;
                            // 文件内容是到下一个 boundary 之前的 \r\n
                            var fileLength = partBytes.Length - contentStartIndex - 2;

                            if (fileLength <= 0) continue;

                            var fileData = new byte[fileLength];
                            Array.Copy(partBytes, contentStartIndex, fileData, 0, fileLength);

                            var savePath = Path.Combine(uploadDir, safeFilename);
                            File.WriteAllBytes(savePath, fileData); // 使用正确的 WriteAllBytes

                            await WriteTextAsync(ctx, $"文件 '{safeFilename}' 上传成功。");
                            return;
                        }
                    }
                }
                throw new Exception("未在请求中找到文件部分。");
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteTextAsync(ctx, $"文件上传失败: {ex.Message}");
            }
        }

        static async Task HandleWebSocketChatAsync(WebSocket ws)
        {
            WsClients.TryAdd(ws, 0); // 安全地添加
            Console.WriteLine($"[WS] 新客户端加入, 当前总数: {WsClients.Count}");

            var buffer = new byte[1024 * 4];
            while (ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    // 广播消息给所有其他客户端
                    var broadcastTasks = WsClients.Keys
                        .Where(c => c.State == WebSocketState.Open)
                        .Select(c => c.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), WebSocketMessageType.Text, true, CancellationToken.None));
                    await Task.WhenAll(broadcastTasks);
                }
                catch (WebSocketException) { break; }
            }

            WsClients.TryRemove(ws, out _); // 安全地移除
            Console.WriteLine($"[WS] 客户端断开, 当前总数: {WsClients.Count}");
        }

        // [修复] 确保所有代码路径都有返回值
        static X509Certificate2? LoadCertificateByThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint) || thumbprint.Contains("PASTE"))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("警告: 未提供有效的证书指纹。HTTPS将不可用。");
                Console.ResetColor();
                return null;
            }
            try
            {
                var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Trim(), false);
                store.Close();
                if (certs.Count > 0) return certs[0];

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"错误: 未在 'LocalMachine/My' 存储中找到指纹为 '{thumbprint}' 的证书。");
                Console.ResetColor();
                return null;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"加载证书时发生异常: {ex.Message}");
                Console.ResetColor();
                return null; // 修复点: 确保 catch 块也返回值
            }
        }

        // --- 用于文件上传解析的辅助工具方法 ---
        private static List<int> FindAllOccurrences(byte[] haystack, byte[] needle)
        {
            var positions = new List<int>();
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle))
                {
                    positions.Add(i);
                }
            }
            return positions;
        }

        private static int FindSequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle)) return i;
            }
            return -1;
        }
    }
}
