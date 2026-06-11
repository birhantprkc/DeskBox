# DeskBox

DeskBox 是一个基于 WinUI 3 的 Windows 桌面整理工具。它可以创建轻量桌面组件，用于收纳文件、映射文件夹，并通过系统托盘快速管理组件。

## 为什么做这个产品

很多传统桌面管理工具会接管桌面：替换原来的桌面交互、重建一套文件入口，甚至让桌面变成另一个完整的管理容器。我不太想走这条路。DeskBox 的目标是尽可能保留 Windows 原生桌面的质感和行为，只在文件整理这件事上补一层更轻的自动化能力。

所以 DeskBox 选择了“移动式整理”的思路：桌面仍然是桌面，文件仍然是普通文件，组件只是帮助你把文件移动、复制或映射到合适的位置。它不会试图成为一个新的桌面 Shell，也不会强迫你改变 Windows 原本的使用方式。

界面层面我比较重视 WinUI 3 的设计一致性。项目中的设置页、桌面组件、对话交互和窗口质感都尽量围绕 Windows App SDK、WinUI 3、Mica、DWM 圆角等原生能力构建，目标是让它看起来像一个 Windows 应用，而不是套了一层桌面皮肤的网页工具。

![Uploading 暗色.png…]()

![Uploading 浅色.png…]()

## 功能

- 桌面文件组件，使用接近 Windows 原生的亚克力、DWM 圆角和窗口质感。
- 收纳组件，可将拖入的文件移动或复制到专属收纳文件夹。
- 文件夹映射组件，可直接展示已有文件夹内容。
- 系统托盘支持新建组件、显示/隐藏组件、打开设置、开机启动和退出。
- 设置页采用紧凑的 WinUI 风格单页布局。

## 环境要求

- Windows 11。
- 安装器会检测 .NET 8 Runtime x64 和 Windows App Runtime 2.1 x64，缺失时自动联网下载并静默安装。
- 安装器需要管理员权限，用于按需安装系统运行时依赖。

目前项目只在 Windows 11 下测试过。Windows 10 或其他系统版本没有做完整验证，如果遇到兼容性问题，欢迎提交 Issue 或反馈复现路径。

开发环境需要 .NET 8 SDK。推荐使用安装了 Windows App SDK 工作负载的 Visual Studio 2022。

## 构建

```powershell
dotnet restore .\DeskBox.sln
dotnet build .\src\DeskBox\DeskBox.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:minimal
```

应用输出目录：

```text
src\DeskBox\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64
```

## 测试

```powershell
dotnet test .\DeskBox.sln -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:minimal
```

当前测试项目覆盖了核心文件转移、路径处理和收纳历史行为。

## 安装包

项目提供一个 Inno Setup 安装脚本：

```text
installer\DeskBox.iss  单安装包，安装时按需获取 .NET 8 Runtime 和 Windows App Runtime。
```

安装包本身不内置完整运行时，体积更小。若目标电脑缺少 .NET 8 Runtime 或 Windows App Runtime，安装过程会显示运行环境准备页，并联网下载、静默安装缺失依赖；离线环境可先手动安装这两个运行时后再运行安装器。

卸载时安装器会先停止正在运行的 DeskBox 进程。如果检测到默认收纳目录中仍有文件或文件夹，会在卸载前提示用户确认；卸载过程不会删除用户移动到收纳目录中的内容。

构建 Release x64 输出并生成安装包：

```powershell
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\x64 -v:minimal
& 'C:\Program Files\Inno Setup 7\ISCC.exe' .\installer\DeskBox.iss
```

安装脚本读取目录：

```text
artifacts\publish\DeskBox\x64
```

## 开发说明

- 应用设置保存在 `%LocalAppData%\DeskBox\data`。
- 默认收纳路径为 `%UserProfile%\DeskBox`。
- `bin`、`obj`、`Output`、`artifacts` 和 `TestResults` 等生成目录已被 Git 忽略。

## 开发者

- 开发者：朱天雨
- 开源仓库：<https://github.com/Tianyu199509/DeskBox>

## 开源协议

本项目使用 MIT 协议开源，详见 [LICENSE](LICENSE)。
