# ClassShout 联动（ClassIsland 插件）

把 [ClassShout](https://github.com/WRD1145/ClassShout) 教室端的喊话显示为 **ClassIsland 提醒** ——
于是喊话出现在学生一整天都在看的那块屏上，而且走的是 ClassIsland 自己的提醒通道，
遮罩动效、音效、朗读都跟着你在「提醒」里的设置走，不会多出一套观感不同的提示方式。

## 它怎么工作

```
ClassShout 教室端  ──POST http://127.0.0.1:45902/shout──▶  本插件  ──▶  ClassIsland 提醒
```

- 插件在**本机回环地址**上监听 `45902`，收到 `{"from":"张老师","text":"…"}` 就发一条提醒。
- 只绑 `127.0.0.1`：只有这台电脑上的进程能投递，同网段其它机器连不上。
- 用的是 `TcpListener` 手写的最小 HTTP，**不是 `HttpListener`** —— 后者在 Windows 上绑
  `127.0.0.1` 一般需要 URL 保留（urlacl）或管理员权限，而插件不该要求任何特权。
  顺带保留了"能用 `curl` 直接测"这个排障便利。

## 安装

需要 **ClassIsland 2.1.1.1 或更高**（`manifest.yml` 里的 `apiVersion` 决定的）。

1. 编译：

   ```powershell
   dotnet build -c Release
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

不起 ClassShout 也能验证插件本身是否就绪：

```powershell
# 用文件送请求体，避免命令行编码把中文变成问号
'{"from":"张老师","text":"同学们请安静"}' | Set-Content -Encoding utf8NoBOM body.json
curl.exe -X POST http://127.0.0.1:45902/shout -H "Content-Type: application/json" --data-binary "@body.json"
```

应当看到遮罩显示「张老师 喊话」，随后是正文。

> **中文乱码的一个常见来源**：直接用 `curl -d '{"text":"中文"}'` 时，PowerShell 会把中文
> 按系统代码页转成 `?` 再交给 curl，**字节在到达插件之前就已经坏了**。
> 这不是插件的编码问题 —— 请在 ClassShout 教室里实际发一条来对照。

## 排障

插件目录下的 `classshout-bridge.log` 记录了投递路径上的异常。
**没有这个文件就说明没有异常**；如果服务器端返回 200 而提醒没出现，
先确认「ClassShout 喊话」这个提供方在提醒设置里是启用的。

## 开发环境

按 [ClassIsland 插件开发文档](https://docs.classisland.tech/dev/) 配置：克隆 ClassIsland 源码
（本项目按 `2.1.1.1` 标签）并运行 `pwsh ./tools/plugin/build.ps1`，它会构建 Debug 版宿主
并设置 `ClassIsland_DebugBinaryFile` / `ClassIsland_DebugBinaryDirectory` 两个环境变量；
之后 `Properties/launchSettings.json` 里的启动配置会用 `-epp` 直接从插件输出目录加载。

## 许可证

[MIT](LICENSE)。本插件只与 ClassIsland 的公开插件 API 交互，不修改 ClassIsland 本体。
