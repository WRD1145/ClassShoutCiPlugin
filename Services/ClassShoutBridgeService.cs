using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassShout.ClassIslandPlugin.Services;

/// <summary>
/// 接收 ClassShout 教室端喊话的本机监听。
///
/// 为什么是"插件监听、教室端来连"这个方向：
/// ClassIsland 的跨进程通信（IPC）只能**从外部调用 ClassIsland**，而它公开的远程服务里
/// 没有"显示一条提醒"这一项（只有课程、档案、Uri 导航）。所以要让外部进程触发提醒，
/// 必须由插件自己开一个入口。
///
/// 为什么用 TcpListener 手写最小的 HTTP，而不是 HttpListener：
/// Windows 上 HttpListener 绑定 127.0.0.1 一般需要 URL 保留（urlacl）或管理员权限，
/// 而插件是要装到老师机器上的，不该要求任何特权。手写这几十行反而更省事，
/// 而且保留"能用 curl 测"这个好处 —— 排障时这一条很值钱。
///
/// 只绑定回环地址：只有本机的进程能投递喊话，同网段的其它机器连不上。
/// 这既是安全边界，也说明白了"它不是一个对外服务"。
/// </summary>
internal sealed class ClassShoutBridgeService : IHostedService
{
    /// <summary>默认端口。教室端与插件都按它走，一般情况下不需要改。</summary>
    public const int DefaultPort = 45902;

    private readonly IServiceProvider _services;

    private ClassShoutNotificationProvider? _provider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 构造函数只拿 <see cref="IServiceProvider"/>，不直接要提醒提供方。
    ///
    /// 这里踩过一次坑：提醒提供方是用 <c>AddHostedService&lt;T&gt;()</c> 注册的，
    /// 而那个方法只把它登记成 <c>IHostedService</c>、**不登记它自己的类型**，
    /// 于是"构造函数里注入 ClassShoutNotificationProvider"会让整个宿主
    /// 在启动阶段抛 <c>Unable to resolve service</c> —— 一个插件把 ClassIsland 拖垮了。
    ///
    /// 也不能直接注入 <c>IEnumerable&lt;IHostedService&gt;</c>：本类型自己就是一个托管服务，
    /// 构造它的过程中去解析全部托管服务会把自己绕进去。所以推迟到 StartAsync ——
    /// 那时宿主已经把全部托管服务构造完了，取到的是现成实例，不存在递归。
    /// </summary>
    public ClassShoutBridgeService(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 拿不到提供方就干脆不监听：宁可这个桥不工作，
            // 也不能让 ClassIsland 起不来 —— 插件坏掉不该拖垮宿主。
            var provider = _services.GetServices<IHostedService>()
                .OfType<ClassShoutNotificationProvider>()
                .FirstOrDefault();

            if (provider is null)
            {
                return Task.CompletedTask;
            }

            _provider = provider;
            _listener = new TcpListener(IPAddress.Loopback, DefaultPort);
            _listener.Start();

            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // 端口被占用同样不该让 ClassIsland 起不来。
            // 这时喊话仍会走 ClassShout 自己的弹窗（教室端那边会记录投递失败）。
            _listener = null;
            _provider = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // 关停路径上的异常无需上报
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);

                if (request is null)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", token).ConfigureAwait(false);
                    return;
                }

                var (method, path, body) = request.Value;

                if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                    !path.StartsWith("/shout", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                    return;
                }

                if (!TryParseShout(body, out var from, out var text))
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", token).ConfigureAwait(false);
                    return;
                }

                if (_provider is null)
                {
                    await WriteResponseAsync(stream, 503, "Service Unavailable", token).ConfigureAwait(false);
                    return;
                }

                // 提醒必须回到 UI 线程去发。这个监听跑在线程池线程上，
                // 直接调 ShowNotification 会抛异常（表现是客户端收到一个空响应，
                // 而服务端这边如果不记日志，就完全看不出发生了什么）。
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _provider.ShowShout(from, text);
                    }
                    catch (Exception ex)
                    {
                        LogFailure("显示提醒失败", ex);
                    }
                });

                await WriteResponseAsync(stream, 200, "OK", token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 兜底捕获并落盘。这里刻意不限定异常类型：
                // 投递路径上任何未预料的异常都会让客户端收到"空响应"，
                // 而这种失败在两端都看不见 —— 没有日志就只能靠猜。
                //
                // 也不在这里回 500：stream 是 try 里的局部变量，出了作用域就取不到，
                // 为了回一个错误码把它提到外层并不划算。
                LogFailure("处理投递时出错", ex);
            }
        }
    }

    /// <summary>
    /// 把失败写进插件目录下的日志文件。
    ///
    /// 为什么不走 ClassIsland 的日志：那需要注入它的 logger 服务，而这里已经因为
    /// "注入不到类型"把宿主搞崩过一次；这条路径上我宁可少依赖一点宿主。
    /// 文件放在插件自己旁边，排障时打开就能看。
    /// </summary>
    private static void LogFailure(string what, Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "classshout-bridge.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {what}：{ex}{Environment.NewLine}");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // 连日志都写不了就算了，绝不能因为记日志再抛一次
        }
    }

    /// <summary>
    /// 读一个最小可用的 HTTP 请求：先读到空行为止拿头部，再按 Content-Length 读体。
    ///
    /// 刻意不做完整的 HTTP 解析：这个端口只接受本机、只有我们自己的客户端会连，
    /// 实现完整协议只会引入一堆用不到的分支。
    ///
    /// **全程按字节处理，不能先转成字符串**。这里踩过一个坑：
    /// Content-Length 是**字节数**，而字符串长度是**字符数** —— 喊话内容里有中文时
    /// （UTF-8 下一个汉字 3 字节），字符数远小于字节数，于是"还差多少字节"的判断永远成立，
    /// 服务端就一直在等下一段数据，客户端只会看到超时。
    /// </summary>
    private static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(
        NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var received = new MemoryStream();
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            received.Write(buffer, 0, read);

            if (received.Length > 64 * 1024)
            {
                return null;
            }

            headerEnd = IndexOfHeaderEnd(received.GetBuffer(), (int)received.Length);
        }

        var data = received.GetBuffer();
        var head = Encoding.ASCII.GetString(data, 0, headerEnd);
        var lines = head.Split("\r\n");

        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), out var parsed) &&
                parsed >= 0)
            {
                contentLength = parsed;
            }
        }

        var bodyStart = headerEnd + 4;
        var needed = bodyStart + contentLength;

        while (received.Length < needed)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
        }

        var available = (int)Math.Min(contentLength, received.Length - bodyStart);
        var body = available > 0
            ? Encoding.UTF8.GetString(data, bodyStart, available)
            : string.Empty;

        return (requestLine[0], requestLine[1], body);
    }

    /// <summary>在字节流里找头部的结束位置（CRLFCRLF）。</summary>
    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>解析喊话体。字段名与教室端发出来的一致。</summary>
    private static bool TryParseShout(string body, out string from, out string text)
    {
        from = string.Empty;
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("text", out var textElement))
        {
            return false;
        }

        text = textElement.GetString()?.Trim() ?? string.Empty;

        if (root.TryGetProperty("from", out var fromElement))
        {
            from = fromElement.GetString()?.Trim() ?? string.Empty;
        }

        return text.Length > 0;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string reason, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(reason);
        var header = $"HTTP/1.1 {status} {reason}\r\n"
                     + "Content-Type: text/plain; charset=utf-8\r\n"
                     + $"Content-Length: {payload.Length}\r\n"
                     + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
