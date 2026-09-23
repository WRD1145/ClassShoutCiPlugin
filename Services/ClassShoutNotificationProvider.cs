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
    /// 显示一条喊话。
    /// </summary>
    /// <param name="from">喊话人，教室端会填成老师的姓名。</param>
    /// <param name="text">喊话内容。</param>
    public void ShowShout(string from, string text)
    {
        // 遮罩只放一个短词：它一闪而过，塞进整句没人读得完。
        var mask = string.IsNullOrWhiteSpace(from) ? "喊话" : $"{from} 喊话";

        ShowNotification(new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(mask),
            OverlayContent = NotificationContent.CreateSimpleTextContent(
                text,
                x => x.Duration = OverlayDuration),
        });
    }
}
