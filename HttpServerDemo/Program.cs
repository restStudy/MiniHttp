using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiniHttp;                   // 引用 HttpServer.cs 所在命名空间

namespace HttpServerDemo
{
internal class Program
    {
        static async Task Main()
        {
            /*──────────────────────────
             * ① 创建服务器：HTTP 8080 + HTTPS 8443
             *   (如无需 https，把 httpsPort 设 null 即可)
             *─────────────────────────*/
            var server = new HttpServer(httpPort: 8080, httpsPort: 8443);

            /*──────────────────────────
             * ② 动态 HTTP 路由
             *─────────────────────────*/
            // 2-1 默认域名：固定文本
            server.AddTextRoute("*", "/", "Welcome to MiniHttp!");

            // 2-2 api.local 域名：返回 JSON
            server.AddRoute("api.local", "GET", "/now", async ctx =>
            {
                ctx.Response.ContentType = "application/json; charset=utf-8";
                string json = $"{{\"utc\":\"{DateTime.UtcNow:o}\"}}";
                await ctx.Response.WriteAsync(json);
            });

            // 2-3 POST 接口：echo body
            server.AddRoute("api.local", "POST", "/echo", async ctx =>
            {
                using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await sr.ReadToEndAsync();
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("you posted =>\n" + body);
            });

            /*──────────────────────────
             * ③ 静态目录
             *─────────────────────────*/
            // 3-1 资源 CDN：static.local → C:\cdn  (不允许列目录)
            server.AddStaticFolder("static.local", "/cdn",  @"Z:\cdn");

            // 3-2 整个 b.local 网站由 D:\siteB 托管，并允许目录浏览
            server.AddStaticFolder("b.local", "/",          @"Z:\cdn", browse:true);

            /*──────────────────────────
             * ④ WebSocket 端点
             *─────────────────────────*/
            // 4-1 任意域名 /ws/echo：回声
            server.AddWebSocket("*", "/ws/echo", EchoLoop);

            // 4-2 chat.local /chat ：最简聊天室
            server.AddWebSocket("chat.local", "/chat", ChatRoom);

            /*──────────────────────────
             * ⑤ HTTPS 证书绑定（可选）
             *─────────────────────────*/
            X509Certificate2? cert = null;
            string pfxFile = @"Z:\certs\my.pfx";
            if (File.Exists(pfxFile))
            {
                cert = new X509Certificate2(pfxFile, "p@ssw0rd");
                Console.WriteLine("已加载 PFX 证书，将自动绑定到 0.0.0.0:8443");
            }

            /*──────────────────────────
             * ⑥ 启动
             *─────────────────────────*/
            server.Start(cert);
            Console.WriteLine("Server started:");
            Console.WriteLine("  http : http://localhost:8080/");
            Console.WriteLine("  https: https://localhost:8443/");
            Console.WriteLine("按 Enter 退出……");
            Console.ReadLine();

            await Task.Run(server.Stop);
        }

        /*================== WebSocket：Echo ==================*/
        private static async Task EchoLoop(WebSocket ws)
        {
            var buf = new byte[1024];

            while (ws.State == WebSocketState.Open)
            {
                // ★ ReceiveAsync 要求 ArraySegment
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);

                if (r.MessageType == WebSocketMessageType.Close) break;

                // ★ SendAsync 同理
                await ws.SendAsync(
                    new ArraySegment<byte>(buf, 0, r.Count),
                    r.MessageType, r.EndOfMessage, CancellationToken.None);
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

/*================== WebSocket：简易聊天室 ==============*/
        private static readonly ConcurrentDictionary<WebSocket, byte> _clients = new();

        private static async Task ChatRoom(WebSocket ws)
        {
            _clients.TryAdd(ws, 0);
            var buf = new byte[1024];

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;

                    string msg  = Encoding.UTF8.GetString(buf, 0, r.Count);
                    byte[] data = Encoding.UTF8.GetBytes(msg);
                    var seg     = new ArraySegment<byte>(data);

                    // 广播
                    foreach (var cli in _clients.Keys)
                        if (cli.State == WebSocketState.Open)
                            await cli.SendAsync(seg, WebSocketMessageType.Text,
                                true, CancellationToken.None);
                }
            }
            finally
            {
                _clients.TryRemove(ws, out _);
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }

    }
}