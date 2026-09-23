using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;

namespace ClassShout.ClassIslandPlugin.Services;

/// <summary>
/// 把 ClassShout 的喊话显示为一条 ClassIsland 提醒。
///
/// 走的是 ClassIsland 自己的提醒通道，因此遮罩动效、音效、朗读这些都由
/// 用户在 ClassIsland 的「提醒」设置里统一控制 —— 喊话不该自带一套独立的提示方式，
/// 否则教室里就有两套互不相干的提醒观感。
///
/// 图标沿用 ClassIsland 内置字体的一个已知可用字形。换成喇叭字形更好，
/// 但那需要知道该图标字体里喇叭对应的码位；在没核对之前不要凭猜写一个码位，
/// 那样界面上会显示一个方框，比图标不贴切更糟。
/// </summary>
[NotificationProviderInfo(
    "6F2A1D74-3C58-4E1B-9E77-2B0A5C4D8E31",
    "ClassShout 喊话",
    "\uE958",
    "由 ClassShout 教室端发来的喊话。")]
public class ClassShoutNotificationProvider : NotificationProviderBase
{
    /// <summary>遮罩停留多久。与 ClassShout 自带弹窗的默认停留时长保持同一量级。</summary>
    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 当前这条还没收尾的喊话提醒。新喊话来的时候用它把上一条顶掉。
    /// </summary>
    private NotificationRequest? _playing;

    /// <summary>
    /// 组装一条喊话提醒。
    ///
    /// 拆成静态方法是为了能自测：遮罩上写「喊话」还是「语音消息」纯粹是给人看的文案，
    /// 写错了不会有任何异常，只会让教室里那块屏幕上出现一句不对的话。
    /// </summary>
    /// <param name="from">喊话人。</param>
    /// <param name="text">正文。文字喊话就是内容本身；语音喊话是"语音消息"以及识别结果。</param>
    /// <param name="isVoice">是不是语音喊话。</param>
    internal static NotificationRequest CreateRequest(string from, string text, bool isVoice)
    {
        // 遮罩只放一个短词：它一闪而过，塞进整句没人读得完。
        var mask = isVoice
            ? string.IsNullOrWhiteSpace(from) ? "语音消息" : $"{from} 语音消息"
            : string.IsNullOrWhiteSpace(from) ? "喊话" : $"{from} 喊话";

        return new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(mask),
            OverlayContent = NotificationContent.CreateSimpleTextContent(
                text,
                x => x.Duration = OverlayDuration),
        };
    }

    /// <summary>
    /// 显示一条喊话。
    /// </summary>
    /// <param name="from">喊话人，教室端会填成老师的姓名。</param>
    /// <param name="text">喊话内容。语音喊话时是"语音消息"以及（如果配了语音转文字）识别出的文字。</param>
    /// <param name="isVoice">是不是语音喊话。它只影响遮罩上写「喊话」还是「语音消息」。</param>
    public void ShowShout(string from, string text, bool isVoice)
    {
        // 后一条喊话把前一条顶掉。
        //
        // 教室里本来就是这样：老师新喊一句，音箱里上一句就停了。而提醒是**排队**的，
        // 不主动取消的话，第二条要等上一条的遮罩加正文播完（正文默认二十秒）才出现 ——
        // 那时候喊话早就结束了，提醒也就没意义了。
        //
        // 语音喊话尤其需要它：教室端先投一条"语音消息"，几秒后识别结果出来再投一条，
        // 不顶掉前一条的话，带识别结果的那条要排在二十秒之后。
        //
        // 提醒请求的取消令牌是链接到播放票据上的（NotificationWorkerService.CreateTicket），
        // 所以这里 Cancel 一下，当前这条会立刻收尾、队列里新的这条立刻顶上。
        // 对已经播完的请求再取消一次也是安全的：CancellationTokenSource.Cancel 是幂等的。
        try
        {
            _playing?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放掉的请求再取消，个别的实现会抛这个。喊话不该因为收尾失败而中断。
        }

        var request = CreateRequest(from, text, isVoice);
        _playing = request;
        ShowNotification(request);
    }
}
