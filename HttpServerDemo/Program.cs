// 文件: Program.cs
// 这是一个功能完备的演示程序，用于测试您提供的 HttpServer v2.1 最终修复版。

using MiniHttp;
using System;
using System.ComponentModel;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HttpServerDemo
{

    internal class Program
    {
        static void Main(string[] args)
        {
            // 使用 Task.Run 来运行异步的 MainAsync 方法，并等待其完成。
            // 这是在控制台应用程序中正确运行 async/await 的标准模式。
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            Console.WriteLine("--- MiniHttp Server v2.1 终极功能演示 ---");
            Console.WriteLine("=========================================");

            // --- 步骤 1: 加载 localhost 测试证书 ---
            Console.WriteLine("\n[1] 正在加载SSL证书...");
            // 重要提示: 请使用我上一条回复中为您生成的 localhost 证书。
            string thumbprint = "A18B66101858FDA395EB39D519A848BEE22C0EBD"; // <--- 在这里粘贴您 localhost 证书的指纹！

            X509Certificate2? certificate = LoadCertificateByThumbprint(thumbprint);

            if (certificate == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("错误: 未能加载SSL证书。请确认您已创建 localhost 证书并正确粘贴了指纹。");
                Console.ResetColor();
                Console.ReadKey();
                return;
            }
            Console.WriteLine($"成功加载证书: {certificate.Subject}");

            // --- 步骤 2: 初始化并配置 HttpServer ---
            Console.WriteLine("\n[2] 正在配置服务器路由...");
            // 使用非标准端口 8080 和 8443，避免与IIS等系统自带服务冲突。
            var server = new HttpServer(httpPort: 80, httpsPort: 443);

            // 2.1) 演示: 根目录映射 (将所有请求的根路径映射到 "WebRoot" 文件夹)
            string webRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebRoot");
            Directory.CreateDirectory(webRootPath); // 确保目录存在
            server.MapHostToRoot("*", webRootPath, browse: true); // 允许目录浏览
            Console.WriteLine($"  - 根目录已映射到: {webRootPath}");

            // 2.2) 演示: 静态子目录 (将 /assets 路径映射到 "WebRoot/assets" 文件夹)
            string assetsPath = Path.Combine(webRootPath, "assets");
            Directory.CreateDirectory(assetsPath); // 确保目录存在
            server.AddStaticFolder("/assets", assetsPath);
            Console.WriteLine($"  - 静态路径 /assets 已映射到: {assetsPath}");

            // 2.3) 演示: 简单的文本API路由
            server.AddTextRoute("*", "/hello", "Hello from the final version of MiniHttp Server!");
            Console.WriteLine("  - 已添加API: GET /hello");

            // 2.4) 演示: 动态API路由 (返回服务器时间)
            server.AddRoute("*", "GET", "/api/time", async ctx => {
                string timeStr = $"服务器当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                byte[] buffer = Encoding.UTF8.GetBytes(timeStr);
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                await ctx.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            });
            Console.WriteLine("  - 已添加API: GET /api/time");

            // 2.5) 演示: WebSocket 路由 (一个简单的回显服务器)
            server.AddWebSocket("*", "/ws", async websocket => {
                Console.WriteLine("  [WebSocket] 客户端已连接！");
                var buffer = new byte[1024 * 4];
                while (websocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), default);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client requested close", default);
                        Console.WriteLine("  [WebSocket] 客户端已断开。");
                    }
                    else
                    {
                        string receivedMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Console.WriteLine($"  [WebSocket] 收到消息: {receivedMsg}");
                        string echoMsg = $"服务器回显: {receivedMsg}";
                        byte[] echoBytes = Encoding.UTF8.GetBytes(echoMsg);
                        await websocket.SendAsync(new ArraySegment<byte>(echoBytes, 0, echoBytes.Length), result.MessageType, result.EndOfMessage, default);
                    }
                }
            });
            Console.WriteLine("  - 已添加WebSocket: /ws");

            // --- 步骤 3: 启动服务器 ---
            try
            {
                Console.WriteLine("\n[3] 正在启动服务器...");
                server.Start(certificate); // 传入证书以启用HTTPS

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n🎉🎉🎉 服务器启动成功! 🎉🎉🎉");
                Console.ResetColor();
                Console.WriteLine($"  - HTTP 服务运行于: http://localhost:80");
                Console.WriteLine($"  - HTTPS 服务运行于: https://localhost:443");
                Console.WriteLine("\n请在浏览器中访问以上地址进行测试。");
                Console.WriteLine("按任意键停止服务器...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n🔥🔥🔥 服务器启动失败! 🔥🔥🔥");
                Console.WriteLine("错误详情:");
                Exception? currentEx = ex;
                while (currentEx != null) // 递归打印所有内部异常，找到根本原因
                {
                    Console.WriteLine($"  - {currentEx.Message}");
                    currentEx = currentEx.InnerException;
                }
                Console.ResetColor();
                Console.ReadKey();
            }
            finally
            {
                server.Stop();
                Console.WriteLine("\n服务器已停止。");
            }
        }

        // 证书加载辅助方法
        private static X509Certificate2? LoadCertificateByThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint) || thumbprint.Equals("PASTE_YOUR_LOCALHOST_THUMBPRINT_HERE", StringComparison.OrdinalIgnoreCase))
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
                var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                store.Close();
                return certs.Count > 0 ? certs[0] : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载证书时出错: {ex.Message}");
                return null;
            }
        }
    }

    //多域名绑定，无法设置证书
    //internal class Program
    //{
    //    static async Task Main()
    //    {
    //        Console.WriteLine("--- MiniHttp Server Example ---");

    //        // 1. 自动创建演示所需的网站目录和文件
    //        CreateDummyWebsites();

    //        // 2. 配置并启动服务器
    //        var server = SetupServer();
    //        if (server == null)
    //        {
    //            Console.WriteLine("服务器启动失败。按任意键退出。");
    //            Console.ReadKey();
    //            return;
    //        }

    //        Console.WriteLine($"\n服务器已启动，监听端口: HTTP {server.HttpPort}, HTTPS {server.HttpsPort ?? 0}");
    //        Console.WriteLine("======================================");
    //        Console.WriteLine("你可以尝试在浏览器中访问以下地址：");
    //        Console.WriteLine("  - http://site1.test.com/");
    //        Console.WriteLine("  - http://site2.test.com/");
    //        Console.WriteLine("  - http://site1.test.com/shared/style.css");
    //        Console.WriteLine("  - http://site1.test.com/api/time");
    //        Console.WriteLine("  - http://site2.test.com/api/hostinfo");
    //        Console.WriteLine("======================================");

    //        Console.WriteLine("\n按 Enter 键停止服务器...");
    //        Console.ReadLine();

    //        server.Stop();
    //        server.Dispose();
    //        Console.WriteLine("服务器已停止。");
    //    }

    //    /// <summary>
    //    /// 配置服务器的所有路由和映射
    //    /// </summary>
    //    private static HttpServer SetupServer()
    //    {
    //        // 创建服务器实例，监听80和443端口
    //        var server = new HttpServer(80, 443);

    //        string baseDir = Path.GetFullPath("Websites");

    //        // === 核心功能 1: 域名映射到根目录 ===
    //        server.MapHostToRoot("fantian28.com", Path.Combine(baseDir, "fantian28.com"));
    //        server.MapHostToRoot("zhuimeng28.com", Path.Combine(baseDir, "zhuimeng28.com"), browse: true); // 允许目录浏览

    //        // === 核心功能 2: 静态子目录 (所有域名共享) ===
    //        server.AddStaticFolder("*", "/shared", Path.Combine(baseDir, "SharedAssets"));

    //        // === 核心功能 3: 动态API路由 ===
    //        // 一个返回服务器时间的简单API
    //        server.AddRoute("*", "GET", "/api/time", async ctx =>
    //        {
    //            string timeJson = $"{{\"serverTime\":\"{DateTime.Now:O}\"}}";
    //            var responseBytes = Encoding.UTF8.GetBytes(timeJson);

    //            ctx.Response.ContentType = "application/json";
    //            ctx.Response.ContentLength64 = responseBytes.Length;
    //            await ctx.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
    //        });

    //        // 一个返回当前请求域名的API
    //        server.AddRoute("*", "GET", "/api/hostinfo", async ctx =>
    //        {
    //            string hostJson = $"{{\"requestedHost\":\"{ctx.Request.UserHostName}\"}}";
    //            var responseBytes = Encoding.UTF8.GetBytes(hostJson);

    //            ctx.Response.ContentType = "application/json";
    //            ctx.Response.ContentLength64 = responseBytes.Length;
    //            await ctx.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
    //        });

    //        // === 核心功能 4: WebSocket ===
    //        server.AddWebSocket("*", "/ws/echo", async ws =>
    //        {
    //            Console.WriteLine($"[WebSocket] 客户端已连接到 /ws/echo");
    //            var buffer = new byte[1024 * 4];

    //            try
    //            {
    //                WebSocketReceiveResult result =
    //                    await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    //                while (!result.CloseStatus.HasValue)
    //                {
    //                    string receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
    //                    Console.WriteLine($"[WebSocket] 收到消息: {receivedMessage}");

    //                    string echoMessage = $"服务器回显: {receivedMessage}";
    //                    var echoBytes = Encoding.UTF8.GetBytes(echoMessage);
    //                    await ws.SendAsync(new ArraySegment<byte>(echoBytes, 0, echoBytes.Length), result.MessageType,
    //                        result.EndOfMessage, CancellationToken.None);

    //                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    //                }

    //                Console.WriteLine($"[WebSocket] 客户端已断开连接: {result.CloseStatus}");
    //            }
    //            catch (Exception ex)
    //            {
    //                Console.WriteLine($"[WebSocket] 连接出错: {ex.Message}");
    //            }
    //        });


    //        // === 核心功能 5: 加载SSL证书并启动 ===
    //        X509Certificate2? sanCertificate = null;
    //        try
    //        {
    //            // ** 将 "my-cert.pfx" 和 "your-password" 替换为你自己的证书信息 **
    //            string certPath = "fantian28.com.pfx";
    //            string certPass = "jtti.cc";
    //            if (File.Exists(certPath))
    //            {
    //                sanCertificate = new X509Certificate2(certPath, certPass);
    //                Console.WriteLine("SSL 证书加载成功！");
    //            }
    //            else
    //            {
    //                Console.WriteLine($"警告: 未找到证书文件 '{certPath}'。HTTPS将不可用。");
    //            }
    //        }
    //        catch (Exception ex)
    //        {
    //            Console.WriteLine($"警告: 加载SSL证书失败 - {ex.Message}。服务器将只在HTTP上运行。");
    //        }

    //        try
    //        {
    //            // 传入SAN证书来启动服务器
    //            server.Start(sanCertificate);
    //            return server;
    //        }
    //        // 专门捕获 Win32Exception
    //        catch (Win32Exception ex)
    //        {
    //            Console.ForegroundColor = ConsoleColor.Red;
    //            Console.WriteLine("\n错误: 启动服务器时发生底层 Windows API 错误！");
    //            Console.WriteLine("这是一个 Win32Exception，意味着SSL证书绑定失败或端口配置问题。");

    //            // --- 分析异常，提供详细诊断信息 ---
    //            Console.WriteLine($"\n--- 诊断信息 ---");
    //            Console.WriteLine($"错误消息: {ex.Message}"); // 例如 "文件未找到" 或 "拒绝访问"
    //            Console.WriteLine($"原生错误码 (NativeErrorCode): {ex.NativeErrorCode}"); // 例如 2 或 5

    //            // 根据错误码提供常见原因
    //            switch (ex.NativeErrorCode)
    //            {
    //                case 2: // ERROR_FILE_NOT_FOUND
    //                    Console.WriteLine("原因分析: '文件未找到'。这通常意味着您尝试删除一个不存在的 netsh 绑定。如果是在绑定时出现，问题可能更复杂。");
    //                    break;
    //                case 5: // ERROR_ACCESS_DENIED
    //                    Console.WriteLine("原因分析: '拒绝访问'。请确认您是以【管理员权限】运行此程序的！");
    //                    break;
    //                case 183: // ERROR_ALREADY_EXISTS
    //                    Console.WriteLine("原因分析: '文件已存在'。这通常意味着该端口已被其他证书绑定。请使用 `netsh http show sslcert` 命令检查。");
    //                    break;
    //                default:
    //                    Console.WriteLine("原因分析: 这是一个不常见的错误码，请根据错误消息进行网络搜索。");
    //                    break;
    //            }

    //            Console.ResetColor();
    //            Console.WriteLine("\n服务器启动失败。按任意键退出。");
    //            Console.ReadKey();
    //        }
    //        // 捕获其他所有可能的异常，作为保险
    //        catch (Exception ex)
    //        {
    //            Console.ForegroundColor = ConsoleColor.Yellow;
    //            Console.WriteLine($"\n错误: 发生未知异常 - {ex.GetType().Name}");
    //            Console.WriteLine(ex.ToString()); // 打印完整的异常信息和堆栈跟踪
    //            Console.ResetColor();
    //            Console.WriteLine("\n服务器启动失败。按任意键退出。");
    //            Console.ReadKey();
    //        }
    //        finally
    //        {
    //            // 无论成功还是失败，都确保服务器被正确停止
    //            server.Stop();
    //            Console.WriteLine("服务器已停止。");
    //        }

    //        return null;
    //    }

    //    /// <summary>
    //    /// 创建演示所需的文件夹和文件
    //    /// </summary>
    //    private static void CreateDummyWebsites()
    //    {
    //        try
    //        {
    //            string baseDir = Path.GetFullPath("Websites");
    //            string site1Dir = Path.Combine(baseDir, "fantian28.com");
    //            string site2Dir = Path.Combine(baseDir, "zhuimeng28.com");
    //            string sharedDir = Path.Combine(baseDir, "SharedAssets");

    //            Directory.CreateDirectory(site1Dir);
    //            Directory.CreateDirectory(site2Dir);
    //            Directory.CreateDirectory(sharedDir);

    //            File.WriteAllText(Path.Combine(site1Dir, "index.html"),
    //                "<!DOCTYPE html><html><head><link rel='stylesheet' href='/shared/style.css'></head><body><h1>Welcome to Site 1</h1></body></html>");

    //            File.WriteAllText(Path.Combine(site2Dir, "index.html"),
    //                "<!DOCTYPE html><html><head><link rel='stylesheet' href='/shared/style.css'></head><body><h1>Welcome to Site 2</h1><p><a href='files/'>浏览文件目录</a></p></body></html>");

    //            // 为Site2创建一些文件以供浏览
    //            Directory.CreateDirectory(Path.Combine(site2Dir, "files"));
    //            File.WriteAllText(Path.Combine(site2Dir, "files", "document.txt"), "This is a test document.");

    //            File.WriteAllText(Path.Combine(sharedDir, "style.css"),
    //                "body { font-family: sans-serif; background-color: #f0f0f0; color: #333; text-align: center; margin-top: 50px; } h1 { color: #005a9c; }");

    //            Console.WriteLine($"演示网站目录已在 '{baseDir}' 中创建。");
    //        }
    //        catch (Exception ex)
    //        {
    //            Console.WriteLine($"创建演示目录失败: {ex.Message}");
    //        }
    //    }
    //}
}
