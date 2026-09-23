using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;
using ClassShout.ClassIslandPlugin.Services;

namespace ClassShout.ClassIslandPlugin.SelfTest;

/// <summary>
/// 插件里"从本机 HTTP 投递解析出一条喊话"这段逻辑的回归测试。
///
/// 跑法（在插件仓库根目录）：dotnet run --project tools\SelfTest
/// 全部通过退出码 0，任何一条失败退出码 1。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        Console.WriteLine("ClassShout ←→ ClassIsland 插件 · 报文解析自测");
        Console.WriteLine();

        // ---------- 1. Content-Length 的请求 ----------
        await AssertFixedLengthBodyAsync();

        // ---------- 2. chunked 的请求（含把中文切开的块边界） ----------
        await AssertChunkedBodyAsync();

        // ---------- 3. kind 字段怎么影响"这是不是语音" ----------
        AssertKindParsing();

        // ---------- 4. 提醒上的那两行字 ----------
        AssertNotificationText();

        Console.WriteLine();
        Console.WriteLine($"结果：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>教室端真正会发的那种请求：带 Content-Length，正文是一段 UTF-8 JSON。</summary>
    private static async Task AssertFixedLengthBodyAsync()
    {
        const string json = """{"from":"张老师","text":"语音消息","kind":"voice"}""";
        var body = Encoding.UTF8.GetBytes(json);

        var head = "POST /shout HTTP/1.1\r\n"
                   + "Host: 127.0.0.1:45902\r\n"
                   + "Content-Type: application/json; charset=utf-8\r\n"
                   + $"Content-Length: {body.Length}\r\n"
                   + "Connection: close\r\n\r\n";

        var raw = Combine(Encoding.ASCII.GetBytes(head), body);
        var parsed = await SendAndParseAsync(raw, splitBody: false);

        Check("Content-Length：请求被识别为 /shout 上的 POST",
            parsed is { Method: "POST", Path: "/shout" },
            parsed is null ? "解析结果为空" : $"{parsed.Value.Method} {parsed.Value.Path}");

        Check("Content-Length：正文逐字节完整（中文按字节数而不是字符数读）",
            parsed?.Body == json,
            parsed is null ? "(无正文)" : $"正文长度 {parsed.Value.Body.Length} 字符");
    }

    /// <summary>
    /// chunked 的请求。
    ///
    /// 块边界**故意切在一个汉字的三个字节中间**：这样"按块拼接"如果写成了
    /// "每块单独解码再拼字符串"，就会得到替换字符，断言会立刻发现。
    /// </summary>
    private static async Task AssertChunkedBodyAsync()
    {
        const string json = """{"from":"张老师","text":"语音消息\n识别结果：同学们把书翻到第三十七页","kind":"voiceTranscript"}""";
        var body = Encoding.UTF8.GetBytes(json);

        // 切在中间那一段（"语音"两个字之间、以及一个多字节字符内部）
        var cut1 = body.Length / 3;
        var cut2 = cut1 + (body.Length / 3) + 1;

        var builder = new StringBuilder();
        builder.Append("POST /shout HTTP/1.1\r\n");
        builder.Append("Host: 127.0.0.1:45902\r\n");
        builder.Append("Content-Type: application/json; charset=utf-8\r\n");
        builder.Append("Transfer-Encoding: chunked\r\n");
        builder.Append("\r\n");

        var pieces = new[] { body[..cut1], body[cut1..cut2], body[cut2..] };
        foreach (var piece in pieces)
        {
            builder.Append(piece.Length.ToString("x"));
            builder.Append("\r\n");
            builder.Append(Encoding.UTF8.GetString(piece));
            builder.Append("\r\n");
        }

        builder.Append("0\r\n\r\n");

        var parsed = await SendAndParseAsync(Encoding.UTF8.GetBytes(builder.ToString()), splitBody: true);

        Check("chunked：请求被识别为 /shout 上的 POST",
            parsed is { Method: "POST", Path: "/shout" },
            parsed is null ? "解析结果为空" : $"{parsed.Value.Method} {parsed.Value.Path}");

        Check("chunked：分块正文被拼回原样（含跨块的中文）",
            parsed?.Body == json,
            parsed is null ? "(无正文)" : $"正文={Show(parsed.Value.Body)}");
    }

    /// <summary>kind 决定遮罩上写「喊话」还是「语音消息」，老客户端不发这个字段。</summary>
    private static void AssertKindParsing()
    {
        var cases = new (string Body, bool ExpectedVoice, string Label)[]
        {
            ("""{"from":"张老师","text":"现在讲第三题","kind":"text"}""", false, "kind=text"),
            ("""{"from":"张老师","text":"语音消息","kind":"voice"}""", true, "kind=voice"),
            ("""{"from":"张老师","text":"识别结果：翻到三十七页","kind":"voiceTranscript"}""", true, "kind=voiceTranscript"),
            ("""{"from":"张老师","text":"现在讲第三题"}""", false, "老客户端（没有 kind 字段）"),
            ("""{"from":"张老师","text":"现在讲第三题","kind":"将来才有的值"}""", false, "不认识的 kind"),
        };

        foreach (var (body, expectedVoice, label) in cases)
        {
            var ok = ClassShoutBridgeService.TryParseShout(body, out var from, out var text, out var isVoice);

            Check($"{label}：解析成功且取到内容",
                ok && from == "张老师" && text.Length > 0,
                ok ? $"from={from} text={Show(text)}" : "解析失败");

            Check($"{label}：判成{(expectedVoice ? "语音" : "文字")}喊话",
                isVoice == expectedVoice,
                isVoice ? "语音" : "文字");
        }

        Check("没有 text 字段的请求被拒",
            !ClassShoutBridgeService.TryParseShout("""{"from":"张老师"}""", out _, out _, out _),
            "缺少 text 时返回 false");
    }

    /// <summary>
    /// 提醒上到底显示了什么。
    ///
    /// 遮罩是"谁 + 什么类型的喊话"，正文是教室端发来的内容。
    /// 这两处都是纯文案，错了不会有异常，只会在教室的屏幕上门出现一句不对的话 ——
    /// 所以它们必须有断言，而不是靠"我读了一遍觉得对"。
    /// </summary>
    private static void AssertNotificationText()
    {
        const string transcript = "同学们把书翻到第三十七页";

        var textShout = ClassShoutNotificationProvider.CreateRequest("张老师", "现在讲第三题", isVoice: false);
        Check("文字喊话的遮罩写「张老师 喊话」",
            MaskText(textShout) == "张老师 喊话", MaskText(textShout) ?? "(没有遮罩文字)");
        Check("文字喊话的正文就是原话",
            OverlayText(textShout) == "现在讲第三题", OverlayText(textShout) ?? "(没有正文)");

        var voiceShout = ClassShoutNotificationProvider.CreateRequest("张老师", "语音消息", isVoice: true);
        Check("语音喊话的遮罩写「张老师 语音消息」",
            MaskText(voiceShout) == "张老师 语音消息", MaskText(voiceShout) ?? "(没有遮罩文字)");
        Check("没配转写时正文只有「语音消息」",
            OverlayText(voiceShout) == "语音消息", OverlayText(voiceShout) ?? "(没有正文)");

        var content = $"语音消息{Environment.NewLine}识别结果：{transcript}";
        var withTranscript = ClassShoutNotificationProvider.CreateRequest("张老师", content, isVoice: true);
        Check("带识别结果时遮罩不变（还是「张老师 语音消息」）",
            MaskText(withTranscript) == "张老师 语音消息", MaskText(withTranscript) ?? "(没有遮罩文字)");
        Check("带识别结果时正文是两行、含识别出来的字",
            OverlayText(withTranscript) == content && OverlayText(withTranscript)!.Contains(transcript, StringComparison.Ordinal),
            Show(OverlayText(withTranscript) ?? "(无正文)"));

        // 姓名拿不到时不能显示成" 喊话"这种前面挂个空格的怪样子
        var anonymous = ClassShoutNotificationProvider.CreateRequest(string.Empty, "语音消息", isVoice: true);
        Check("没有姓名时遮罩只写「语音消息」",
            MaskText(anonymous) == "语音消息", MaskText(anonymous) ?? "(没有遮罩文字)");
    }

    /// <summary>取遮罩上的文字。内容是个模板数据对象，字段名见 ClassIsland 的提醒模板。</summary>
    private static string? MaskText(NotificationRequest request)
        => (request.MaskContent.Content as TwoIconsMaskTemplateData)?.Text;

    /// <summary>取正文文字。</summary>
    private static string? OverlayText(NotificationRequest request)
        => (request.OverlayContent?.Content as SimpleTextTemplateData)?.Text;

    /// <summary>
    /// 起一个真实的 TCP 连接，把原始字节写进去，再让被测的服务端逻辑去解析它。
    ///
    /// 刻意手写字节而不是用 HttpClient：HttpClient 会替我们决定用哪种传输方式，
    /// 而这里要测的恰恰是"对面用了哪种传输方式时我们能不能读懂"。
    /// </summary>
    /// <param name="raw">要发过去的完整请求字节。</param>
    /// <param name="splitBody">是否把请求拆成两次写，模拟"头部与正文分开到达"。</param>
    private static async Task<(string Method, string Path, string Body)?> SendAndParseAsync(byte[] raw, bool splitBody)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = Task.Run(async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();

            if (splitBody)
            {
                // 前半段必然会先到一个包，让服务端有机会"手里只有一部分数据"地去解析
                var half = raw.Length / 2;
                await stream.WriteAsync(raw.AsMemory(0, half));
                await stream.FlushAsync();
                await Task.Delay(50);
                await stream.WriteAsync(raw.AsMemory(half));
            }
            else
            {
                await stream.WriteAsync(raw);
            }

            await stream.FlushAsync();

            // 等服务端读完再关，免得数据还没到就被 RST 掉
            await Task.Delay(200);
        });

        using var server = await listener.AcceptTcpClientAsync();
        var result = await ClassShoutBridgeService.ReadRequestAsync(server.GetStream(), CancellationToken.None);

        await client;
        listener.Stop();
        return result;
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        Array.Copy(first, combined, first.Length);
        Array.Copy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    /// <summary>把换行显示出来，否则失败信息里两行正文看起来跟一行一样。</summary>
    private static string Show(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    private static void Check(string what, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [通过] {what} —— {detail}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {what} —— {detail}");
        }
    }
}
