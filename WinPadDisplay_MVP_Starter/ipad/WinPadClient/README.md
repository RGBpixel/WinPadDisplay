# WinPadClient iPad MVP

Xcode -> New Project -> iOS/iPadOS App -> SwiftUI。

1. 用这里的 `ContentView.swift` 替换项目默认文件。
2. Deployment Target 使用 iPadOS 16.0+；当前 iPadOS 16.3.1 可运行。
3. Info.plist 添加：
   - Privacy - Local Network Usage Description
   - 值：`WinPad uses the local/USB network to receive display frames from your Windows PC.`
4. Signing & Capabilities 选择你自己的 Apple Developer Team。
5. 真机运行到 iPad。

MVP 使用 TCP 5959 + JPEG，只验证通信路径。
最终版应改为 VideoToolbox H.264/HEVC 解码。
