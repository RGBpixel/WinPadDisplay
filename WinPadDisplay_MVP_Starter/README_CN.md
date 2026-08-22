# WinPadDisplay MVP Starter

目标：让 iPad 作为 Windows 11 的扩展屏，优先使用 USB-C 数据线。

这不是最终版 Duet 克隆，而是一个按风险拆分的可运行工程起点：

1. Windows 通过 IddCx 创建真正的虚拟第二显示器。
2. Windows 获取该显示器帧并低延迟编码。
3. USB-C 传输层把视频流送到 iPad。
4. iPad 解码显示，并把触摸/Apple Pencil 输入传回 Windows。

## 本包已经包含

- Win11 WPF 主控程序骨架（.NET 8）
- iPad USB 设备检测（调用 pymobiledevice3）
- Windows -> iPad 的测试帧传输代码（长度前缀 JPEG 协议）
- iPad SwiftUI/NWListener 测试客户端
- USB tunnel 检查脚本
- Win11 IddCx 驱动接入说明和推荐显示模式
- 后续 H.264/HEVC、触控回传的接口规划

## 第一个验收目标

先不要急着做完整扩展屏。先证明：

- Win11 能识别 iPad USB 连接
- USB tunnel 能建立
- Windows 能通过 tunnel 连到 iPad 客户端
- iPad 能连续收到 Windows 发来的测试画面

这个测试通过以后，再把测试画面替换成 IddCx 虚拟显示器帧。

## Windows 环境

- Windows 11 x64
- Visual Studio 2022
- .NET 8 SDK
- Python 3.10+（仅 MVP USB tunnel 验证使用）
- Apple Devices / Apple Mobile Device 驱动
- Windows Driver Kit (WDK) 11（第二阶段做虚拟显示驱动）

## iPad 环境

- USB-C iPad
- iPadOS 17.4+ 推荐
- 开启开发者模式用于自建客户端测试
- 编译/签名 iPad App 需要 Xcode/macOS（正式项目必须考虑这一点）

## 运行顺序

### A. Windows 检查 iPad

以管理员 PowerShell 运行：

```powershell
cd scripts
.\01_check_ipad_usb.ps1
```

如果能看到 DeviceName / ProductVersion / ConnectionType=USB，说明苹果 USB 驱动链路正常。

### B. 建立 USB tunnel

```powershell
.\02_start_usb_tunnel.ps1
```

记录输出的 RSD IPv6 地址，例如：

```text
fd7b:e5b:6f53::1
```

### C. 在 iPad 上运行 WinPadClient

Xcode 创建 iPadOS SwiftUI 项目，把 `ipad/WinPadClient/ContentView.swift` 替换进去，然后运行到 iPad。
界面出现 `Listening on 5959` 后保持 App 在前台。

### D. Windows 运行 WinPadHost

打开：

```text
windows/WinPadHost/WinPadHost.csproj
```

在 Visual Studio 中启动。把 RSD IPv6 地址填入“iPad 地址”，端口保持 5959，点击“连接并发送测试画面”。

如果 iPad 画面持续变化，说明 Windows -> USB tunnel -> iPad 的最关键通信链路成立。

## 第二阶段：真正扩展屏

使用微软 IddCx Indirect Display Driver。不要用“屏幕截图伪装第二屏”的办法，因为那样 Windows 不会把 iPad 当成真正的扩展桌面。

推荐初始模式：

- 1920×1440 @ 60Hz
- 1920×1280 @ 60Hz
- 1600×1200 @ 60Hz
- 1280×960 @ 60Hz

之后根据具体 iPad 型号自动上报原生/缩放分辨率。

## 第三阶段：视频链路

测试 JPEG 仅用于验证传输，不是最终方案。最终使用：

- D3D11 Texture
- Media Foundation H.264 Low-Latency Encoder
- Annex-B NALU / 自定义轻量帧协议
- iPad VideoToolbox 硬件解码

建议延迟目标：

- USB 端到端 25–50 ms
- 1080p/1440p 60 fps
- H.264 12–30 Mbps 动态码率

## 第四阶段：输入回传

iPad -> Windows：

- 单指：鼠标移动/点击
- 双指：滚轮
- Apple Pencil：坐标、压力、倾角（第二版）
- Windows 端：SendInput 起步，后续升级成虚拟 HID/绝对坐标映射

