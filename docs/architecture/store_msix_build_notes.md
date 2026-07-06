# DeskBox Store/MSIX 构建说明

本文档记录 Microsoft Store 技术分支的本地构建入口。当前 Direct/Inno 仍是默认发布通道，Store 包需要显式传入 `DeskBoxDistribution=Store`。

## 构建入口

```powershell
.\scripts\build-store-msix.ps1
```

默认行为：

- 构建 `Release x64`
- 使用 `DeskBoxDistribution=Store`
- 跳过 `DeskBox.Updater`
- 生成自包含 .NET 包，避免 Store 用户额外安装 .NET Desktop Runtime
- 关闭 MSIX 签名，适合本机先验证包结构
- 输出到 `artifacts\store-msix\`

ARM64 测试包：

```powershell
.\scripts\build-store-msix.ps1 -Platform ARM64
```

带证书签名：

```powershell
.\scripts\build-store-msix.ps1 -SignPackage -PackageCertificateKeyFile "path\to\DeskBox.pfx"
```

## 上架前必须替换

`src\DeskBox\Package.appxmanifest` 里目前使用占位身份：

- `Identity Name="DeskBox.Desktop"`
- `Publisher="CN=DeskBox"`
- `PublisherDisplayName="DeskBox"`

正式上架前需要按 Partner Center 分配的 Package/Publisher Identity 修改，否则无法提交到 Microsoft Store。

## 当前边界

Store 构建会启用：

- `DESKBOX_STORE` 编译常量
- `StoreAppUpdateService`
- `StoreStartupService`
- `Package.appxmanifest`
- Store 专用 tile/logo/splash 资源

Direct 构建继续使用：

- `WindowsPackageType=None`
- Inno 安装包
- `AppUpdateService`
- `DirectStartupService`
- `DeskBox.Updater`

## 后续验证

真正上架前还需要：

1. 使用 Partner Center 真实身份重新打包。
2. 运行 Windows App Certification Kit。
3. 实测文件拖拽、托盘、开机自启、系统音量、多屏/DPI。
4. 根据 Microsoft Store 政策确认关于页捐赠二维码是否隐藏或替换为官网说明。
