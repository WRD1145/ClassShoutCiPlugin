using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassShout.ClassIslandPlugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassShout.ClassIslandPlugin;

/// <summary>
/// 插件入口。
///
/// 这个插件做两件事，分别由两个托管服务承担：
///   · <see cref="ClassShoutNotificationProvider"/> —— 一个提醒提供方，
///     于是喊话会走 ClassIsland 自己的提醒通道（遮罩、音效、朗读都跟着它的设置走），
///     而不是我们自己再画一套弹窗；
///   · <see cref="ClassShoutBridgeService"/> —— 监听本机的一个端口，接收 ClassShout 教室端的喊话。
///
/// 监听为什么不放在提醒提供方里：<c>NotificationProviderBase</c> 虽然实现了
/// <c>IHostedService</c>，但它的 <c>StartAsync</c> 并不是 virtual，
/// 派生类覆写不了。硬要把监听塞进构造函数，就会失去"宿主启动/停止"这条生命周期，
/// 端口也回收不干净。所以另起一个托管服务，用依赖注入拿到提醒提供方来发提醒。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // AddNotificationProvider 会把它同时注册进 DI 与宿主服务列表，
        // 所以下面那个托管服务可以直接在构造函数里要到它。
        services.AddNotificationProvider<ClassShoutNotificationProvider>();
        services.AddHostedService<ClassShoutBridgeService>();
    }
}
