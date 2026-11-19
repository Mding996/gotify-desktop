# Gotify Desktop

基于 C#、WPF 和 .NET 8 的 Windows 10 / 11 x64 原生 Gotify 客户端。免安装，下载 Release 中的单文件 EXE 即可运行。

当前版本：**1.8.1**。

## 下载与运行

从本仓库的 **Releases** 下载 Windows x64 ZIP，解压后运行其中的单文件 `GotifyDesktop.exe`。无需单独安装 .NET 运行时。

升级前从旧版托盘菜单选择“退出”，再运行新版一次，以更新 Windows 通知的启动路径。原有本机配置和消息继续使用。

## 功能

- WebSocket 实时收取消息，自动重连，连接后拉取最近消息，支持加载更早消息。
- 搜索、应用和时间筛选、未读管理、消息详情、Markdown 基础阅读及安全链接。
- 发送标题、正文和优先级；在 Gotify 3.x 上直接选择应用发送，也保留独立 Application Token 发送。
- 验证码识别，消息列表一键复制；原生 Windows 通知提供单个“复制验证码”按钮。
- 每条通知绑定自己的验证码；复制保留前导零，不打开主窗口，不改变未读状态。程序完整退出后，保留通知也可启动短进程复制并退出。
- 默认仅对优先级 8～10 提醒，支持按应用配置、暂停通知、免打扰及普通消息合并摘要。
- 关闭窗口隐藏到系统托盘，可选开机启动，浅色／深色主题及原生标题栏同步。
- 账号、应用、客户端及 Token 保险箱管理，支持服务器消息删除等操作，敏感操作有确认与账号验证。
- Token 使用当前 Windows 用户的 DPAPI 加密。历史消息仅在本机保存，最多 5000 条。

## 连接配置

可使用账号登录，也可在设置中分别配置接收使用的 **Client Token** 和发送使用的 **Application Token**。

首次使用请填写自己的 Gotify 服务器地址，例如 `https://gotify.example.com`；默认地址为空。本仓库不包含任何可用 Token、账号密码或个人消息。

详细配置、升级、存储位置和操作说明见 [使用说明](使用说明.md)。

## 从源码构建

需要 Windows 和 .NET 8 SDK。在仓库根目录运行：

```powershell
.\build-release.ps1
```

产物位于 `publish\GotifyDesktop.exe`。如只需构建、不生成完整单文件，可运行：

```powershell
dotnet build .\GotifyDesktop.csproj -c Release
```

离线发布时，先将目标运行时和发布所需的 NuGet 包放入本地源，再运行：

```powershell
.\build-release.ps1 -Offline -OfflineSource C:\path\to\offline-nuget
```

## 验证情况

1.8.1 发布 EXE 在 Windows 10 x64 上通过 **330 项回归检查**及 **9 项原生复制和协议调用检查**。测试使用虚构消息和隔离配置，没有向真实 Gotify 账号发送测试短信。

- [回归结果](test-results.json)
- [原生复制验证](copy-native-results.json)
- [构建与验证边界](BUILD_STATUS.md)
- [内存优化测量](MEMORY_COMPARISON.md)

Windows 10 已完成虚构通知的实际复制按钮点击验证。Windows 11 尚未进行实机验证，详见 [验证记录](VALIDATION.md)。

## 图标与项目关系

这是独立开发的 Gotify Windows 客户端，与 Gotify 官方项目没有隶属关系。

Gotify 原图标来自 [gotify.net](https://gotify.net/img/logo.png)，官方服务端项目见 [gotify/server](https://github.com/gotify/server)。


## 许可证

本项目代码使用 [MIT License](LICENSE)。Gotify 图标单独使用 CC BY 4.0，详见 [第三方声明](THIRD_PARTY_NOTICES.md)。


单文件 EXE 内嵌完整许可证；ZIP 同时附带 LICENSE、THIRD_PARTY_NOTICES.md 和 licenses 目录。公开发行准备见 [检查记录](PUBLIC_READINESS.md)。

