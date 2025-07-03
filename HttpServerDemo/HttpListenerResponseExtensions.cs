using System.Net;
using System.Text;
using System.Threading.Tasks;
namespace HttpServerDemo;

internal static class HttpListenerResponseExtensions
{
    /// <summary>把字符串写入响应并自动设置 ContentLength64</summary>
    public static async Task WriteAsync(this HttpListenerResponse resp, string text)
    {
        byte[] buf = Encoding.UTF8.GetBytes(text);
        resp.ContentLength64 = buf.Length;
        await resp.OutputStream.WriteAsync(buf, 0, buf.Length);
    }
}
