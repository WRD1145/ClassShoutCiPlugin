# ClassShout 联动（ClassIsland 插件）

把 [ClassShout](https://github.com/WRD1145/ClassShout) 教室端的喊话显示为 **ClassIsland 提醒** ——
于是喊话出现在学生一整天都在看的那块屏上，而且走的是 ClassIsland 自己的提醒通道，
遮罩动效、音效、朗读都跟着你在「提醒」里的设置走，不会多出一套观感不同的提示方式。

## 它怎么工作

```
ClassShout 教室端  ──POST http://127.0.0.1:45902/shout──▶  本插件  ──▶  ClassIsland 提醒
```

- 插件在**本机回环地址**上监听 `45902`，收到
  `{"from":"张老师","text":"…","kind":"voice"}` 就发一条提醒。
- 只绑 `127.0.0.1`：只有这台电脑上的进程能投递，同网段其它机器连不上。
- 用的是 `TcpListener` 手写的最小 HTTP，**不是 `HttpListener`** —— 后者在 Windows 上绑
  `127.0.0.1` 一般需要 URL 保留（urlacl）或管理员权限，而插件不该要求任何特权。
  顺带保留了"能用 `curl` 直接测"这个排障便利。
  两种传输方式都认：`Content-Length` 与 `chunked`。

### 报文里的 `kind`

遮罩上写「喊话」还是「语音消息」由它决定：

| `kind` | 遮罩 | 正文 | 什么时候发 |
|---|---|---|---|
| `text` | `张老师 喊话` | 老师发的字 | 文字喊话 |
| `voice` | `张老师 语音消息` | `语音消息` | 语音开始播放（教室端没配语音转文字时，这就是最终形态） |
| `voiceTranscript` | `张老师 语音消息` | `语音消息` + 换行 + `识别结果：…` | 语音转文字的结果出来了 |
| `image` | `张老师 图片消息` | 随图那句说明（没有说明时是一句占位） | 图片喊话 —— ClassIsland 的提醒里放不下图，所以只显示说明那句话，图在教室端的大字区 |

这个字段是**可选**的：老版本教室端不发它，插件就当成 `text`，行为和加它之前完全一样。

同一时刻只显示一条喊话：新喊话到来时会把上一条顶掉。ClassIsland 的提醒是排队的，
不顶的话，`voiceTranscript` 那条要等上一条的遮罩加正文播完（正文默认二十秒）才出现，
那时喊话早就结束了。

## 安装

需要 **ClassIsland 2.1.0.1 或更高**（`manifest.yml` 里的 `apiVersion` 决定的）。

> **目标框架必须与宿主一致**，否则插件装进去也加载不起来（表现是"装了但没反应"）：
> ClassIsland **2.1.0.x 跑在 `net8.0-windows`** 上，**2.1.1 起换成了 `net10.0-windows`**。
> 本项目按所选的宿主版本自动取框架（见 csproj 里的 `ClassIslandHostTfm`），
> 所以针对哪个版本编译就得到哪个框架的产物，不必手动改。

1. 编译：

   ```powershell
   dotnet build -c Release
   # 产物在 bin\Release\net8.0-windows\（默认针对 2.1.0.1）
   #
   # 针对 2.1.1.1 的宿主：
   dotnet build -c Release -p:ClassIslandPluginSdkVersion=2.1.1.1
   # 产物在 bin\Release\net10.0-windows\
   ```

2. 把产物目录整个复制到 ClassIsland 的插件目录，**目录名用插件 id**：

   ```
   <ClassIsland 数据目录>\Plugins\classshout.bridge\
   ```

   里面应当有 `manifest.yml`、`ClassShout.ClassIslandPlugin.dll`、`icon.png`。

3. 重启 ClassIsland。首次加载后，[【应用设置】→【提醒】](classisland://app/settings/notification)
   里会出现一个名为「ClassShout 喊话」的提醒提供方 —— 它需要在那里保持启用。

## 自测

**一、不用起 ClassIsland：报文解析的回归**

```powershell
dotnet run --project tools\SelfTest
```

它直接验证"从本机 HTTP 投递里解析出一条喊话"这段逻辑，全部通过退出码 0：
两种传输方式（`Content-Length` / `chunked`）、中文按字节数读、`kind` 的四类取值与缺失/未知时的兼容、
以及提醒上那两行文案。不需要 ClassIsland 宿主，因此在 CI 或没装 ClassIsland 的机器上也能跑。

**二、起了 ClassIsland：端到端看一眼**

```powershell
# 用文件送请求体，避免命令行编码把中文变成问号
'{"from":"张老师","text":"同学们请安静","kind":"text"}' | Set-Content -Encoding utf8NoBOM body.json
curl.exe -X POST http://127.0.0.1:45902/shout -H "Content-Type: application/json" --data-binary "@body.json"

# 语音喊话（没配转写）
'{"from":"张老师","text":"语音消息","kind":"voice"}' | Set-Content -Encoding utf8NoBOM body.json
curl.exe -X POST http://127.0.0.1:45902/shout -H "Content-Type: application/json" --data-binary "@body.json"
```

第一条应当看到遮罩「张老师 喊话」，第二条是「张老师 语音消息」。

> **中文乱码的一个常见来源**：直接用 `curl -d '{"text":"中文"}'` 时，PowerShell 会把中文
> 按系统代码页转成 `?` 再交给 curl，**字节在到达插件之前就已经坏了**。
> 这不是插件的编码问题 —— 请在 ClassShout 教室里实际发一条来对照。

## 排障

插件目录下的 `classshout-bridge.log` 记录了投递路径上的异常。
**没有这个文件就说明没有异常**；如果服务器端返回 200 而提醒没出现，
先确认「ClassShout 喊话」这个提供方在提醒设置里是启用的。

投递是一个本机回环请求，正常是毫秒级。教室端一侧还有两条保护：
超时 2 秒、失败不阻塞喊话本身（插件没装、ClassIsland 没运行都是正常情况）。

**踩过的两个坑**（都只在"喊话没出现"这一种表现下暴露，两端都没有日志）：

1. **字符数当字节数比**。`Content-Length` 是字节数，而中文一个汉字 3 字节；
   按字符数判断"还差多少字节"会永远成立，服务端一直等下一段数据，客户端只会看到超时。
   现在全程按字节处理。
2. **只认 `Content-Length`**。教室端到插件的请求走的是回环地址，但机器上设着 `HTTP_PROXY` 时，
   .NET 的 HttpClient 默认会连它一起走；包被代理重新编码成 `chunked` 之后，
   只认 `Content-Length` 的这一侧就读成了空体，判成 400。现在 `chunked` 也认，
   教室端那一侧也显式关掉了代理 —— 两头都不再依赖"对面恰好用了哪种编码"。

## 开发环境

按 [ClassIsland 插件开发文档](https://docs.classisland.tech/dev/) 配置：克隆 ClassIsland 源码
（本项目按 `2.1.1.1` 标签）并运行 `pwsh ./tools/plugin/build.ps1`，它会构建 Debug 版宿主
并设置 `ClassIsland_DebugBinaryFile` / `ClassIsland_DebugBinaryDirectory` 两个环境变量；
之后 `Properties/launchSettings.json` 里的启动配置会用 `-epp` 直接从插件输出目录加载。

> 用调试宿主实测时要先**退出正在运行的 ClassIsland**：它用命名互斥体
> （`Global\ClassIsland.Lock`）做单实例，第二个实例会直接退出并把焦点交给已运行的那个，
> 表现是"启动脚本跑完了，但插件端口没起来、界面也没出现"。

## 许可证

**GNU General Public License v3.0**（[GPL-3.0](LICENSE)）。

本插件只与 ClassIsland 的公开插件 API 交互，不修改 ClassIsland 本体 ——
所以它的许可与 ClassIsland 本体的许可（LGPLv3）互不影响。
