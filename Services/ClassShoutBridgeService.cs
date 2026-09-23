using System.Globalization;
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

                if (!TryParseShout(body, out var from, out var text, out var isVoice))
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
                        _provider.ShowShout(from, text, isVoice);
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
    /// 读一个最小可用的 HTTP 请求：先读到空行为止拿头部，再按声明的传输方式读体。
    ///
    /// 刻意不做完整的 HTTP 解析：这个端口只接受本机、只有我们自己的客户端会连，
    /// 实现完整协议只会引入一堆用不到的分支。
    ///
    /// **全程按字节处理，不能先转成字符串**。这里踩过一个坑：
    /// Content-Length 是**字节数**，而字符串长度是**字符数** —— 喊话内容里有中文时
    /// （UTF-8 下一个汉字 3 字节），字符数远小于字节数，于是"还差多少字节"的判断永远成立，
    /// 服务端就一直在等下一段数据，客户端只会看到超时。
    ///
    /// internal 而不是 private：tools/SelfTest 要直接验证它（见 csproj 里的 InternalsVisibleTo）。
    /// </summary>
    internal static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(
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
        var chunked = false;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var parsed) &&
                parsed >= 0)
            {
                contentLength = parsed;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                     value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        var bodyStart = headerEnd + 4;

        // 两种传输方式都要认。
        //
        // Content-Length 好办，读够字节数就行。chunked 是**必须**支持的：
        // 只要客户端不知道长度（或者中间隔了一层代理），HTTP/1.1 就会改用它。
        // 我确实撞上过：教室端到本机插件的请求被系统代理接了一手，代理把请求体
        // 重新编码成 chunked 发过来，而这里当时只按 Content-Length 读 ——
        // 读到的是 0 字节，于是判成"没有 text 字段"，回一个 400。
        // 两端都没有日志能说明为什么喊话不见了，只有抓包看得出。
        var body = chunked
            ? await ReadChunkedBodyAsync(received.GetBuffer(), (int)received.Length, bodyStart, stream, token)
                .ConfigureAwait(false)
            : await ReadFixedBodyAsync(received, bodyStart, contentLength, stream, token).ConfigureAwait(false);

        return (requestLine[0], requestLine[1], body);
    }

    /// <summary>按 Content-Length 读完请求体。</summary>
    private static async Task<string> ReadFixedBodyAsync(
        MemoryStream received, int bodyStart, int contentLength, NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
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

        var data = received.GetBuffer();
        var available = (int)Math.Min(contentLength, received.Length - bodyStart);

        return available > 0
            ? Encoding.UTF8.GetString(data, bodyStart, available)
            : string.Empty;
    }

    /// <summary>
    /// 按 chunked 编码读完请求体。
    ///
    /// 解析的是最小子集：十六进制长度行 + 数据 + CRLF，直到长度为 0 的那一块；
    /// 尾部的 trailer 直接忽略（我们的客户端不会发，而它对我们也没有意义）。
    /// 前一块可能已经和头部一起躺在 <paramref name="initial"/> 里了，所以起点是它。
    /// </summary>
    private static async Task<string> ReadChunkedBodyAsync(
        byte[] initial, int initialLength, int bodyStart, NetworkStream stream, CancellationToken token)
    {
        var data = new byte[initialLength];
        Array.Copy(initial, data, initialLength);

        var position = bodyStart;
        var buffer = new byte[8192];
        using var body = new MemoryStream();

        // 把"手头不够就去网络上再要一点"收在一处，后面按行、按块读都走它。
        async Task<bool> EnsureAsync(int count)
        {
            while (data.Length - position < count)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return false;
                }

                var old = data.Length;
                Array.Resize(ref data, old + read);
                Array.Copy(buffer, 0, data, old, read);
            }

            return true;
        }

        async Task<string?> ReadLineAsync()
        {
            while (true)
            {
                for (var i = position; i + 1 < data.Length; i++)
                {
                    if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
                    {
                        var line = Encoding.ASCII.GetString(data, position, i - position);
                        position = i + 2;
                        return line;
                    }
                }

                if (!await EnsureAsync(data.Length - position + 1).ConfigureAwait(false))
                {
                    return null;
                }
            }
        }

        while (true)
        {
            var line = await ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return string.Empty;
            }

            // 长度行可能带扩展（"1a;foo=bar"），扩展部分与数据无关
            var sizeText = line.Split(';')[0].Trim();

            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                return string.Empty;
            }

            if (size == 0)
            {
                break;
            }

            // 数据后面还跟着一个 CRLF，一并要过来
            if (!await EnsureAsync(size + 2).ConfigureAwait(false))
            {
                return string.Empty;
            }

            body.Write(data, position, size);
            position += size + 2;
        }

        return Encoding.UTF8.GetString(body.ToArray());
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

    /// <summary>
    /// 解析喊话体。字段名与教室端发出来的一致。
    ///
    /// <c>kind</c> 是可选的：老版本的教室端不发这个字段，那就当成文字喊话，
    /// 遮罩上写「喊话」—— 和加这个字段之前的表现完全一样。
    ///
    /// internal 而不是 private：原因同 <see cref="ReadRequestAsync"/>。
    /// </summary>
    internal static bool TryParseShout(string body, out string from, out string text, out bool isVoice)
    {
        from = string.Empty;
        text = string.Empty;
        isVoice = false;

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

        // 语音喊话有两种：还没转写出结果的（voice），和有识别结果的（voiceTranscript）。
        // 对这里来说它们一样 —— 都是语音，正文都由教室端写好，插件原样显示。
        if (root.TryGetProperty("kind", out var kindElement) &&
            kindElement.ValueKind == JsonValueKind.String)
        {
            isVoice = kindElement.GetString() is "voice" or "voiceTranscript";
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
