# LightSwitch

<p align="center">
  <img src="LightSwitch/Assets/StoreLogo.png" width="96" alt="LightSwitch 图标" />
</p>

自动切换 Windows 浅色 / 深色主题的独立托盘工具。源自 [Microsoft PowerToys](https://github.com/microsoft/PowerToys) 的 LightSwitch 模块，使用 C# / WinUI 3 完全重写，不依赖 PowerToys，安装即用。

## 功能

- **四种主题计划模式**
  - 关闭：不自动切换，仅手动控制
  - 固定时间：自定义浅色、深色开始时间
  - 日出到日落：按当地日出日落时间自动切换，支持分钟级偏移
  - 跟随夜览：与系统夜览（Night Light）状态联动
- **日出日落自动定位**：调用 Windows 系统定位获取经纬度，设置页实时显示当日日出日落时间，也支持手动输入坐标
- **随主题切换壁纸**：分别为浅色、深色模式指定壁纸（JPG / PNG / BMP），主题切换时同步更换，思路参考 [Auto Dark Mode](https://github.com/AutoDarkMode/Windows-Auto-Night-Mode)
- **全局快捷键**：默认 `Win + Ctrl + Shift + D` 立即切换，可在设置中自定义或清除
- **切换范围可选**：系统主题（任务栏、开始菜单）与应用主题可分别控制
- **开机自启动**：设置中一键开关
- **托盘常驻**：右键切换主题 / 切换模式 / 打开设置，双击快速切换
- **Fluent 界面**：WinUI 3 设置窗口，Windows 11 下启用 Mica 材质，深浅色跟随系统

## 系统要求

- Windows 10 版本 1809（内部版本 17763）及以上，支持 Windows 11
- x64（ARM64 需自行编译）
- 安装包自包含 Windows App SDK 运行时，无需另行安装

## 安装

1. 从 [Releases](../../releases) 下载并解压 `LightSwitch_x.y.0_x64.zip`
2. 双击其中的 `.msix` 文件，在弹出的安装窗口点击"安装"
3. 从开始菜单搜索"LightSwitch 主题切换"启动

若提示"证书不受信任"，先双击随附的 `.cer` 证书 →"安装证书"→"本地计算机"→ 放入"受信任的根证书颁发机构"，再安装 msix（仅此一次）。也可以右键 `Install.ps1` →"使用 PowerShell 运行"，自动完成证书导入和安装。

## 使用

启动后驻留系统托盘：

| 操作 | 效果 |
|---|---|
| 双击托盘图标 | 立即切换浅色 / 深色 |
| 右键托盘图标 | 打开菜单：切换、切换计划模式、设置、退出 |
| `Win + Ctrl + Shift + D` | 全局快捷键切换 |

配置保存在 `%LocalAppData%\LightSwitch\settings.json`，日志在 `%LocalAppData%\LightSwitch\logs\`。

## 从源码构建

需要 .NET 8 SDK（打包需要 Windows SDK 构建工具，随 NuGet 自动还原）：

```powershell
# 编译
dotnet build LightSwitch/LightSwitch.csproj -c Release -r win-x64

# 打包 MSIX（需先创建签名证书，见下）
dotnet publish LightSwitch/LightSwitch.csproj -c Release -r win-x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:UapAppxPackageBuildMode=SideloadOnly `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<你的证书指纹>
```

创建自签名证书（主题必须为 `CN=LightSwitch`，与 `Package.appxmanifest` 中的 Publisher 一致）：

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=LightSwitch" `
  -KeyUsage DigitalSignature -FriendlyName "LightSwitch" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$pwd = ConvertTo-SecureString -String "<密码>" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "LightSwitch.pfx" -Password $pwd
# 将上方命令输出的指纹填入 -p:PackageCertificateThumbprint
```

## 项目结构

```
LightSwitch/
├── App.xaml / App.xaml.cs          # 应用入口、托盘图标、单实例、命令绑定
├── SettingsWindow.xaml / .cs       # 设置窗口（Mica、DPI 感知、快捷键捕获）
├── Services/
│   ├── SchedulerService.cs         # 调度核心：定时评估、手动覆盖、外部变更检测
│   ├── SettingsService.cs          # settings.json 读写 + 文件监视
│   ├── ThemeService.cs             # 注册表读写主题 + 广播 WM_SETTINGCHANGE
│   ├── SunCalculator.cs            # 太阳位置算法（日出日落计算）
│   ├── LocationService.cs          # Windows 系统定位
│   ├── NightLightService.cs        # 夜览状态读取与注册表监听
│   ├── HotkeyService.cs            # 全局快捷键（RegisterHotKey）
│   ├── WallpaperService.cs         # 壁纸切换（SystemParametersInfo）
│   ├── StartupService.cs           # 开机自启（HKCU Run）
│   └── Logger.cs                   # 文件日志
└── Native/NativeMethods.cs         # Win32 互操作
```

## 许可证

基于 [MIT](LICENSE) 许可证发布，源自 [Microsoft PowerToys](https://github.com/microsoft/PowerToys) 项目。
