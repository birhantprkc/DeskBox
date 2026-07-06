# DeskBox .NET 10 / Windows App SDK 2.2 / Microsoft Store 升级综合方案

日期：2026-07-06

本文档用于评估 DeskBox 从当前 `.NET 8 + Windows App SDK 2.1.3 + Inno Setup` 升级到 `.NET 10 + Windows App SDK 2.2`，并为后续上架 Microsoft Store、支持商店更新做技术规划。

结论先说：

1. 这次升级不要只当成“改几个版本号”。它会影响构建、安装器、运行时依赖、应用内更新、开机自启、Store 打包和后续分发策略。
2. 建议拆成两条发布通道：官网/GitHub Direct 版继续用 Inno 安装包和现有应用内更新；Microsoft Store 版单独走 MSIX + Store 更新。
3. Direct 版和 Store 版可以共用大部分业务代码，但更新服务、开机自启、运行时依赖检测、安装路径和部分关于页入口必须按通道分流。
4. 文件拖拽、Shell 剪贴板、多屏/DPI、系统音量、托盘这些底层兜底代码不能因为升级就删。Windows App SDK 2.2 能改善稳定性，但不能直接替代这些能力。
5. 第一版 Store 不建议同时做数据路径迁移、大规模 UI 重构、托盘库升级、NativeAOT、trimming。先让渠道稳定，之后再逐步优化。

## 一、当前项目基线

根据当前仓库文件和本机环境，DeskBox 现状如下：

| 项目 | 当前状态 |
| --- | --- |
| 主程序 | `src/DeskBox/DeskBox.csproj` |
| 更新器 | `src/DeskBox.Updater/DeskBox.Updater.csproj` |
| 测试项目 | `tests/DeskBox.Tests/DeskBox.Tests.csproj` |
| 目标框架 | `net8.0-windows10.0.22621.0` |
| Windows App SDK | `Microsoft.WindowsAppSDK 2.1.3` |
| Windows SDK BuildTools | `10.0.26100.4654` |
| MVVM Toolkit | `CommunityToolkit.Mvvm 8.4.0` |
| WinUI Toolkit 控件 | `CommunityToolkit.WinUI.* 8.2.251219` |
| 托盘库 | `H.NotifyIcon.WinUI 2.1.0` |
| 当前发布方式 | `WindowsPackageType=None`，Inno Setup 安装到 `%LocalAppData%\Programs\DeskBox` |
| 当前更新方式 | `AppUpdateService` 下载 manifest 和 Inno 安装包，再启动 `DeskBox.Updater.exe` 覆盖安装 |
| 当前安装器依赖 | 检测 `.NET 8 Runtime x64` 和 `Windows App Runtime 2.1.3 x64` |
| 本机 SDK | 当前只安装了 .NET 8 SDK / Runtime，还没有 .NET 10 SDK |

当前 `dotnet list DeskBox.sln package --outdated --include-transitive` 显示主要可升级项：

| 包 | 当前 | 最新稳定 |
| --- | --- | --- |
| `Microsoft.WindowsAppSDK` | `2.1.3` | `2.2.0` |
| `Microsoft.Windows.SDK.BuildTools` | `10.0.26100.4654` | `10.0.28000.2270` |
| `CommunityToolkit.Mvvm` | `8.4.0` | `8.4.2` |
| `H.NotifyIcon.WinUI` | `2.1.0` | `2.4.1` |
| `Microsoft.NET.Test.Sdk` | `17.8.0` | `18.7.0` |
| `xunit` | `2.5.3` | `2.9.3` |
| `xunit.runner.visualstudio` | `2.5.3` | `3.1.5` |
| `coverlet.collector` | `6.0.0` | `10.0.1` |

建议这次主升级只动必要包：

- 必动：`.NET 10`、`Microsoft.WindowsAppSDK 2.2.0`、`Microsoft.Windows.SDK.BuildTools 10.0.28000.2270`
- 可一起小步升级：`CommunityToolkit.Mvvm 8.4.2`
- 暂不一起升级：`H.NotifyIcon.WinUI 2.4.1`
- 测试包建议单独提交升级，避免主升级失败时混淆原因。

## 二、目标版本判断

截至 2026-07-06，建议目标为：

| 项目 | 目标 |
| --- | --- |
| .NET | `.NET 10 LTS` |
| .NET SDK | `10.0.301` 或后续 10.0 SDK |
| .NET Runtime | `10.0.9` 或后续 10.0 Runtime |
| Windows App SDK | `2.2.0` |
| WindowsAppSDK WinUI 传递包 | `2.2.1` |
| Windows SDK BuildTools | `10.0.28000.2270` |
| 目标框架 | `net10.0-windows10.0.22621.0` |

重要说明：

- 当前官方稳定通道是 Windows App SDK `2.2.0`，不是 `2.3`。如果后续 2.3 正式进入 Stable，需要重新跑一次官方源和 NuGet 校验，不要根据预览版或二手信息直接升级。
- `.NET 10` 是 LTS，适合作为下一轮长期基线。
- DeskBox 当前主打 Windows 11，如果 Store 版也只希望支持 Win11，后续 MSIX manifest / Partner Center 中应明确最低系统要求，避免 Win10 用户安装后遇到材质、窗口或 API 体验缺失。

推荐项目文件目标：

```xml
<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.2.0" />
<PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.28000.2270" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
```

## 三、升级前准备

开发机需要先准备：

- 安装 `.NET 10 SDK 10.0.301` 或更新的 10.0 SDK。
- 安装支持 `.NET 10 / Windows App SDK 2.2` 的 Visual Studio 或 Build Tools。
- 安装 Windows App SDK / WinUI 相关工作负载。
- 如果要做 Store 包，安装 MSIX Packaging Tools，并准备 Windows App Certification Kit。
- 保留当前 Direct/Inno 版本的可构建状态，方便回滚对比。

建议创建独立分支：

```powershell
git checkout -b codex/upgrade-dotnet10-wasdk22-store
```

本机当前只有 .NET 8 SDK，所以真正开始升级前，第一件事是安装 .NET 10 SDK。否则修改 csproj 后本机无法 restore/build。

## 四、第一阶段：先升级 Direct/Inno 版本

第一阶段目标：不碰 Store，不做 MSIX，只让当前官网/GitHub 版本在 `.NET 10 + Windows App SDK 2.2` 下能正常构建、启动、安装和更新。

### 4.1 需要修改的文件

| 文件 | 修改点 |
| --- | --- |
| `src/DeskBox/DeskBox.csproj` | `TargetFramework` 改为 `net10.0-windows10.0.22621.0`；Windows App SDK 改为 `2.2.0`；BuildTools 改为 `10.0.28000.2270`；MVVM Toolkit 可改为 `8.4.2` |
| `src/DeskBox.Updater/DeskBox.Updater.csproj` | 同步改为 `net10.0-windows10.0.22621.0` |
| `tests/DeskBox.Tests/DeskBox.Tests.csproj` | 同步改为 `net10.0-windows10.0.22621.0` |
| `installer/DeskBox.Dependencies.iss` | `.NET 8` 检测改为 `.NET 10`；Windows App Runtime `2.1.3` 检测改为 `2.2.0` |
| `installer/DeskBox.iss` | 构建命令、`AppComments`、版本相关文案同步 |
| `src/DeskBox/Services/LocalizationService.cs` | 关于页 `WinUI 3 / .NET 8` 改成 `.NET 10` |
| `README.md` / `README.zh-CN.md` | 依赖说明、构建要求、badge 同步 |
| `CHANGELOG.md` / `docs/releases/*` | 新版本发布说明同步 |

### 4.2 安装器依赖检测

当前 Inno 脚本有：

- `IsDotNet8RuntimeInstalled`
- `IsWindowsAppRuntime21Installed`
- `.NET 8 Runtime x64`
- `Windows App Runtime 2.1 x64`
- `https://aka.ms/windowsappsdk/2.1/2.1.3/windowsappruntimeinstall-x64.exe`

升级后建议：

- `IsDotNet8RuntimeInstalled` 改成 `IsDotNet10RuntimeInstalled`
- `.NET Runtime` major version 从 `8` 改成 `10`
- `.NET` 下载地址改成 10.0 Runtime x64，fallback 使用 `https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe`
- `IsWindowsAppRuntime21Installed` 改成 `IsWindowsAppRuntime22Installed`
- Windows App Runtime 检测版本从 `2.1.3.0` 改成 `2.2.0.0`
- Windows App Runtime fallback 改成 `https://aka.ms/windowsappsdk/2.2/2.2.0/windowsappruntimeinstall-x64.exe`
- 安装器文案全部从 `.NET 8 / Windows App Runtime 2.1` 改成 `.NET 10 / Windows App Runtime 2.2`

注意：安装器依赖检测必须在干净机器或虚拟机实测。这里最怕“开发机能跑，用户机器没有运行时直接启动失败”。

### 4.3 构建命令

升级后路径会从 `net8.0-windows10.0.22621.0` 变成 `net10.0-windows10.0.22621.0`。任何硬编码输出路径都要同步。

建议先跑：

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build .\src\DeskBox\DeskBox.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:minimal
dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:minimal
dotnet publish .\src\DeskBox\DeskBox.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\x64 -v:minimal
```

### 4.4 Direct 版本验收清单

必须验收：

- 主程序启动
- 设置页打开和切换
- 所有格子创建、显示、移动、锁定、隐藏、显示
- 文件格子拖入、拖出、右键、重命名、删除
- 映射格子读写权限
- 待办、随记、音乐格子
- 随记图片缩略图缓存
- 多屏、DPI、主屏切换、外接屏插拔恢复
- 开机自启
- 全局快捷键
- 托盘图标和右键菜单
- 应用内检查更新、下载、安装
- 干净机器安装缺失依赖
- 旧版本覆盖安装后保留设置和格子数据

## 五、哪些地方需要大改

### 5.1 应用分发通道需要抽象

当前只有 Direct/Inno 逻辑。上 Store 后必须引入通道概念：

```csharp
public enum AppDistributionChannel
{
    Direct,
    MicrosoftStore
}
```

建议新增：

- `AppDistributionService`
- `AppDistributionChannel CurrentChannel`
- `IsPackaged`
- `IsMicrosoftStore`

检测方式建议封装在一个地方，不要到处散落：

- 可以用 Win32 `GetCurrentPackageFullName` 判断是否有 package identity。
- Store 版可以在编译常量或 MSIX manifest 中明确渠道。
- 不建议在业务层到处 `try Package.Current`，后期会很难维护。

### 5.2 更新服务需要拆分

当前 `AppUpdateService` 适合 Direct 版：

1. 下载 manifest
2. 判断版本
3. 下载 Inno 安装包
4. 校验 SHA256
5. 启动 `DeskBox.Updater.exe`
6. 退出主程序并覆盖安装

Store 版不能继续走这套逻辑。Store 版应由 Microsoft Store 管理包更新。

建议抽象：

```csharp
public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}
```

实现拆分：

- `DirectAppUpdateService`：保留当前 manifest + Inno 安装器逻辑
- `StoreAppUpdateService`：使用 `StoreContext`
- `NoopAppUpdateService`：必要时用于未登录 Store、开发调试、不可用状态

设置页/关于页只依赖 `IAppUpdateService`，不要直接依赖 Direct 下载逻辑。

### 5.3 开机自启需要拆分

当前 `StartupService` 走 HKCU Run：

```text
Software\Microsoft\Windows\CurrentVersion\Run
```

Direct 版继续这样没问题。

Store/MSIX 版建议改成 package identity 下的 `StartupTask` 或 Store 兼容启动方案，不建议继续依赖传统 Run 项。

建议抽象：

- `IStartupService`
- `DirectStartupService`
- `StoreStartupService`

设置页仍然是同一个开关，底层按通道切换。

### 5.4 数据路径需要集中

当前项目多处直接使用：

```csharp
Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
```

涉及：

- 设置
- 格子配置
- 随记
- Todo
- 更新缓存
- 日志
- 缩略图

建议新增 `DeskBoxDataPathService`：

- Direct 版继续用 `%LocalAppData%\DeskBox`
- Store 第一版也建议继续读写 `%LocalAppData%\DeskBox`
- 后续如果迁移到 package local folder，再做一次性迁移

原因：Store 首版如果同时改变数据路径，用户从官网版切换到 Store 版时容易“设置全没了”。这会制造大量无意义反馈。

Windows App SDK 2.2 新增的 `ApplicationData.GetForUnpackaged()` 可以作为后续统一 packaged/unpackaged 数据入口的基础，但第一版不要急着迁移真实数据。

### 5.5 Store/MSIX 打包需要单独配置

当前主项目：

```xml
<WindowsPackageType>None</WindowsPackageType>
<AppxPackage>false</AppxPackage>
<EnableMsixTooling>true</EnableMsixTooling>
```

Store 版需要新增 MSIX 配置。推荐优先尝试 single-project MSIX。

当前风险点：主项目构建时会复制 `DeskBox.Updater.exe`。Single-project MSIX 对多可执行文件不友好，而且 Store 版本来也不应该带 Direct 更新器。

因此 Store 配置下应：

- 不构建 `DeskBox.Updater`
- 不复制 `DeskBox.Updater.exe`
- About/设置页更新入口切换到 Store 更新逻辑
- 隐藏 Direct 版“网盘下载 / 手动下载安装包”等入口

如果 single-project MSIX 不够灵活，再考虑 Windows Application Packaging Project。不要一开始就上更复杂的打包项目。

### 5.6 关于页捐赠入口要单独评估

DeskBox 当前关于页有微信/支付宝收款码。Direct 版没有问题，但 Store 版要谨慎。

Microsoft Store 政策对应用内收款、捐赠、外部支付有要求。尤其是：

- 自愿捐赠需要使用 Microsoft 允许的支付请求 API 或安全第三方购买 API。
- 如果用户支付后获得数字权益、功能解锁、去广告等，则通常需要走 Store 内购。
- 二维码直接收款是否能过审，需要结合 Store 政策和具体审核判断。

建议 Store 第一版：

- 如果只是“请我喝杯咖啡”，优先改成打开官网赞助说明页。
- 或者 Store 构建下隐藏二维码，只保留官网/GitHub/反馈入口。
- Direct 版继续保留二维码。

这不是技术问题，是审核风险问题。建议在 Store 技术分支里单独处理。

## 六、哪些地方只需要调整

### 6.1 README / CHANGELOG / 关于页文案

所有 `.NET 8`、`Windows App Runtime 2.1.3` 需要同步：

- README 中英文
- CHANGELOG 中英文
- docs/releases
- 官网如果后续同步发布，也要改
- 关于页 `WinUI 3 / .NET 8`
- 安装器 `AppComments`

### 6.2 输出路径

所有类似路径都要检查：

```text
net8.0-windows10.0.22621.0
```

升级后变成：

```text
net10.0-windows10.0.22621.0
```

包括：

- 打包脚本
- 文档命令
- 调试启动命令
- 旧归档文档可以不改，但发布文档要改

### 6.3 测试包

测试项目当前依赖偏旧。建议先只改 TFM，等主项目能构建后再升级测试包。

可选升级：

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.7.0" />
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
<PackageReference Include="coverlet.collector" Version="10.0.1" />
```

如果测试升级引起分析器或 runner 行为变化，要单独处理，不要和 WinUI 主升级混在一起。

### 6.4 H.NotifyIcon.WinUI 暂缓

`H.NotifyIcon.WinUI` 有新版本，但它影响托盘图标、菜单、显示/隐藏窗口、退出等核心交互。

建议：

- `.NET 10 + WASDK 2.2` 升级时先不动
- Direct/Store 基础稳定后，单独开一次托盘库升级
- 升级托盘库时重点测：右键菜单、双击、退出、重启后图标是否丢失、Store/MSIX 下是否正常

### 6.5 CommunityToolkit.WinUI 控件暂缓

当前 Toolkit WinUI 控件包稳定版仍保持 `8.2.251219`。如果看到 8.3 preview，不建议正式发版使用。

这部分暂时不动，除非某个控件 bug 在稳定版中明确修复。

## 七、升级后可以利用的新能力

### 7.1 .NET 10 带来的收益

对 DeskBox 比较实际的收益：

- Runtime/JIT 优化：对启动、后台循环、列表处理、图片缩略图缓存、配置加载都有间接收益。
- System.Text.Json 增强：后续可以优化配置、更新 manifest、随记/待办数据读写。
- Process 能力增强：后续可用于更新器、子进程启动、日志诊断。
- C# 14 新语法：可以逐步改善 ViewModel 和 helper 代码可读性。

可逐步采用，不建议升级当天大规模重写：

- `field` backed properties：可用于需要验证/清洗的属性。
- null-conditional assignment：可减少少量 UI 状态判空代码。
- extension members：可整理 helper，但不要为新语法而重构。

不建议作为本次目标：

- NativeAOT：WinUI 3、XAML、WinRT、反射和第三方库场景风险很高。
- trimming：DeskBox 的 XAML、WinRT、反射、本地化和 Toolkit 依赖较多，当前收益不确定，风险偏高。

### 7.2 Windows App SDK 2.2 带来的收益

对 DeskBox 有价值的点：

1. `ApplicationData.GetForUnpackaged()`
   - 可以作为未来统一 Direct/Store 数据路径的基础。
   - 第一版建议只封装，不立刻迁移真实数据。

2. WinUI 稳定性修复
   - `RenderTargetBitmap` 相关崩溃修复。
   - `ScrollView` 销毁时计时器 use-after-free 修复。
   - `ThemeSettings` 后台线程销毁崩溃修复。
   - `InputPointerSource` 指针取消崩溃修复。
   - 对设置页、随记列表、弹窗、窗口销毁、多窗口有间接收益。

3. Store/MSIX 基础更稳
   - 对后续 Store 打包、package identity、数据路径封装更友好。

4. 标准窗口标题栏新 API 可以继续评估
   - `TitleBar.IsDragRegion`
   - `TitleBar.AutoRefreshDragRegions`
   - `TitleBar.RecomputeDragRegions()`

这些对设置页、引导页这类标准窗口更有价值。桌面格子的自定义标题栏更复杂，包含隐藏、悬浮、锁定、拖动、右键菜单和重命名，不建议直接替换。

### 7.3 Store 更新能力

Store 版可以使用：

- `StoreContext.GetDefault()`
- `GetAppAndOptionalStorePackageUpdatesAsync()`
- `RequestDownloadAndInstallStorePackageUpdatesAsync()`
- 可选的静默下载/安装 API

可做体验：

- 关于页显示“来自 Microsoft Store”
- 检查商店更新
- 展示下载/安装进度
- 更新不可用时打开 Store
- 与 Direct 版的安装包下载逻辑完全分开

## 八、哪些旧代码可以评估减少

### 可以考虑逐步收口

1. 分散的数据路径代码
   - 当前多处直接取 `%LocalAppData%`。
   - 可用 `DeskBoxDataPathService` 集中。
   - 未来再接 `ApplicationData.GetForUnpackaged()`。

2. Store 版的更新器 helper
   - Store 版不需要 `DeskBox.Updater.exe`。
   - Store 构建下可以完全排除。

3. Store 版的 Inno 依赖检测
   - Store/MSIX 不走 Inno。
   - Direct 版保留。

4. 标准窗口的自定义拖拽区
   - 设置页、引导页可以评估用新的 TitleBar API。
   - 桌面格子暂不动。

5. 部分 backdrop 延迟刷新逻辑
   - 项目里有多处 `QueueBackdropRefresh()`、延迟刷新、托盘显示时临时抑制背景。
   - Windows App SDK 2.2 的稳定性修复可能减少某些异常，但不能直接删。
   - 升级后需要用浅色/深色、透明度、托盘显示/隐藏、多屏切换实测后再决定。

### 不建议删除

这些代码本质上是在补 Windows 桌面/Shell/多屏场景的复杂性，不是框架升级能替代的：

- `DragDropPermissionService`
- `WidgetWindow` 里的 native file drop subclass
- `ShellClipboardHelper`
- `Win32Helper`
- `GlobalHotkeyService` 的 `RegisterHotKey` 和 fallback
- 多屏/DPI/主屏切换定位修正
- 剪贴板重试逻辑
- 随记图片缩略图缓存
- CoreAudio 系统音量 COM 逻辑
- 当前托盘兜底逻辑

这些是 DeskBox 的核心稳定性来源。升级后最多做回归测试和局部收口，不要一刀切。

## 九、Microsoft Store 上架与商店更新方案

### 9.1 推荐路线

推荐 Store 版本走：

```text
MSIX package + package identity + StoreContext 更新
```

不推荐直接把 Inno EXE/MSI 当 Store 包上传后仍靠自己更新。那样可以进入 Store 分发，但不符合“后续支持商店更新”的目标。

### 9.2 Direct 版和 Store 版的关系

建议两个通道并存：

| 通道 | 安装方式 | 更新方式 | 适合用户 |
| --- | --- | --- | --- |
| Direct | 官网/GitHub Inno 安装包 | DeskBox 自己检查、下载、安装 | 国内网络、GitHub/官网用户、需要网盘兜底 |
| Microsoft Store | Store/MSIX | Store 自动更新 + 应用内 StoreContext 检查 | 希望系统托管更新的普通用户 |

两个通道不要互相覆盖安装：

- Direct 版不要覆盖 Store 版。
- Store 版不要覆盖 Direct 版。
- 关于页显示渠道，方便用户反馈问题时定位。
- 如果用户想从 Direct 切到 Store，第一版可引导“先退出/卸载 Direct 版，再安装 Store 版”，但数据仍读取 `%LocalAppData%\DeskBox`。

### 9.3 Store 版更新体验

Store 版关于页“应用更新”建议：

- 自动检查：走 Store API 或仅提示 Store 自动更新状态。
- 手动检查：调用 StoreContext 检查更新。
- 有更新：显示版本/进度，调用 Store 安装。
- 失败：显示自然语言错误，并提供“打开 Microsoft Store”。
- 不显示国内网盘下载，不显示 Inno 安装包下载。

Direct 版保持：

- 自动检查
- 手动检查
- 下载进度
- 安装包校验
- 启动 `DeskBox.Updater.exe`
- 国内网盘兜底

### 9.4 Store 版 .NET Runtime 策略

Direct 版建议继续 framework-dependent：

```xml
<SelfContained>false</SelfContained>
<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
```

原因：官网安装包体积更小，缺运行时时由 Inno 下载。

Store 版建议优先评估 self-contained .NET：

```xml
<SelfContained>true</SelfContained>
<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
```

原因：

- 用户从 Store 安装时不应该额外思考 .NET Runtime。
- 包会变大，但安装成功率更重要。
- Windows App Runtime 可以继续由系统/Store 处理。

最终要用实际 MSIX 包体、首次安装体验和 Store 审核结果决定。

### 9.5 Store 包配置建议

建议新增构建配置或 MSBuild 属性：

```text
Release-Direct
Release-Store
```

或：

```powershell
-p:DeskBoxDistribution=Direct
-p:DeskBoxDistribution=Store
```

Store 构建下：

- 不构建 updater 项目
- 不复制 `DeskBox.Updater.exe`
- 启用 MSIX
- 使用 Store 渠道标记
- About 页显示 Microsoft Store 渠道
- 使用 `StoreAppUpdateService`
- 使用 `StoreStartupService`
- 可能隐藏捐赠二维码

Direct 构建下：

- 保持 Inno
- 保持 updater
- 保持网盘兜底
- 保持当前 HKCU Run 开机自启

### 9.6 Partner Center 上架流程

大致流程：

1. 注册/登录 Partner Center。
2. 预留产品名 `DeskBox`。
3. 配置应用基本信息、年龄分级、隐私政策、支持链接、官网链接。
4. 准备中英文 Store 文案和截图。
5. 生成 MSIX / MSIX upload 包。
6. 运行 Windows App Certification Kit。
7. 上传包并提交审核。
8. 后续发版时在 Partner Center 对已发布应用 `Start update`，上传新包。

Store 更新后：

- 用户默认由 Microsoft Store 自动更新。
- 用户也可以在 Microsoft Store 手动更新。
- DeskBox 应用内可以提供“检查商店更新”入口，但不应下载自己的 exe 覆盖安装。

### 9.7 Store 审核重点

需要提前检查：

- 隐私政策：DeskBox 有 OCR、剪贴板、文件操作、本地数据，需要说明清楚。
- 文件访问：说明为什么需要用户拖拽/选择文件夹。
- 后台行为：开机自启、托盘、全局快捷键要在 UI 中可关闭。
- 外部下载：Store 版不要引导用户下载 Inno 安装包。
- 捐赠二维码：Store 版建议谨慎处理，见上文。
- 许可证：你已经调整过开源协议，Store 文案要避免和 GitHub LICENSE 表述冲突。

## 十、风险矩阵

| 项目 | 风险 | 原因 | 建议 |
| --- | --- | --- | --- |
| .NET 8 -> .NET 10 | 中 | SDK、restore、runtime 行为变化 | 单独提交，先过构建和测试 |
| Windows App SDK 2.1.3 -> 2.2.0 | 中 | WinUI 多窗口、Popup、DPI、XAML 行为可能变化 | 重点测设置页、格子、弹窗、多屏 |
| Inno 依赖检测 | 中高 | 干净机器安装失败会直接影响用户 | 必须虚拟机/新机器实测 |
| Store/MSIX | 高 | 分发模型、权限、更新模型变化 | 单独分支，不和 Direct 升级混做 |
| Store 更新 | 中高 | 要替换当前 exe 更新逻辑 | 抽象 `IAppUpdateService` |
| 开机自启 | 中 | Direct 和 Store 推荐实现不同 | 抽象 `IStartupService` |
| 数据路径 | 高 | 用户配置不能丢 | Store 首版继续读旧 `%LocalAppData%\DeskBox` |
| 文件拖拽 | 高 | DeskBox 核心能力，MSIX 下必须实测 | 不删旧兜底，逐项回归 |
| 托盘 | 中 | 第三方库 + MSIX 场景需要验证 | 暂不升级托盘库 |
| 捐赠入口 | 中 | Store 审核政策风险 | Store 构建可隐藏或改官网链接 |

## 十一、建议实施顺序

### Step 0：备份和基线

- 确认当前 1.2.1 可构建。
- 保留一个发版 tag 或备份包。
- 新建升级分支。
- 记录当前安装器和更新流程。

### Step 1：安装 .NET 10 SDK

- 安装 `.NET 10 SDK 10.0.301+`。
- 跑 `dotnet --info`。
- 确认 Visual Studio / Build Tools 可识别 .NET 10。

### Step 2：Direct 版框架升级

- 改三个 csproj 的 TFM。
- 改 Windows App SDK 和 BuildTools。
- 暂不动 Store。
- 先解决编译错误。
- 启动应用做基础回归。

### Step 3：Inno 运行时依赖升级

- 改 `.NET 10 Runtime` 检测和下载。
- 改 `Windows App Runtime 2.2` 检测和下载。
- 干净机器验证安装。
- 验证旧版本覆盖安装。

### Step 4：Direct 版完整回归

- 文件格子
- 随记/Todo/音乐
- 多屏/DPI
- 开机自启
- 快捷键
- 托盘
- 应用内更新

### Step 5：引入分发通道抽象

- `AppDistributionService`
- `IAppUpdateService`
- `IStartupService`
- `DeskBoxDataPathService`

先在 Direct 版下不改变行为，只把边界抽出来。

### Step 6：Store 技术分支

- 新增 Store 构建配置。
- 尝试 single-project MSIX。
- Store 构建排除 updater。
- About/更新入口切 StoreContext。
- 开机自启切 Store 方案。
- 运行 WACK。

### Step 7：Partner Center 预发布

- 准备 Store 文案和截图。
- 配置隐私政策/官网/支持链接。
- 上传包。
- 做私有测试或隐藏上架。
- 确认 Store 更新路径。

## 十二、版本策略建议

如果只是 Direct 版底层升级：

- 可以发 `1.2.2`。
- 更新日志写“底层运行时升级到 .NET 10 / Windows App SDK 2.2，提升长期维护和稳定性”。

如果同时引入 Store 分流、更新服务重构、启动服务重构：

- 更适合发 `1.3.0`。
- 因为这是分发架构变化，不只是 bugfix。

Store 首版建议：

- 展示版本和渠道，例如：
  - `版本 1.3.0 · Direct · WinUI 3 / .NET 10`
  - `版本 1.3.0 · Microsoft Store · WinUI 3 / .NET 10`
- 内部构建号可与 Store 包版本规则对齐。

## 十三、最终建议

最稳路线：

1. 先做 `.NET 10 + Windows App SDK 2.2` 的 Direct 版升级。
2. 确认官网安装包和现有应用内更新稳定。
3. 再抽象 Direct/Store 分发通道。
4. 再做 Store MSIX、StoreContext 更新、StartupTask、Store 包审核。
5. Store 首版不要同时迁移数据路径，不要同时重写拖拽，不要同时升级托盘库，不要启用 NativeAOT/trimming。

这次升级的重点是给 DeskBox 建立更长期的运行时基线，以及未来 Direct/Store 双通道发布能力。只要边界拆清楚，后面维护会舒服很多；如果一开始把官网更新、Store 更新、安装器、数据路径混在一起，后续用户反馈会非常难排查。

## 十四、参考来源

- [.NET 10 What's new](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
- [.NET 10 compatibility / breaking changes](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0)
- [C# 14 What's new](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)
- [.NET release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json)
- [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [Windows App SDK 2.0 / 2.2 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0)
- [Package and deploy Windows apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/)
- [Single-project MSIX](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix)
- [Deploy packaged apps with Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-packaged-apps)
- [Deploy unpackaged apps with Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
- [StoreContext API](https://learn.microsoft.com/en-us/uwp/api/windows.services.store.storecontext)
- [Download and install package updates from the Store](https://learn.microsoft.com/en-us/windows/uwp/packaging/self-install-package-updates)
- [Publish an update to your app on the Store](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/publish-update-to-your-app-on-store)
- [Microsoft Store Policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
