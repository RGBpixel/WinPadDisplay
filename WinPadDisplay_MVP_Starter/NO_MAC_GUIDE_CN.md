# 没有 Mac 的 iPad 客户端安装方案

这套方案不需要你拥有 Mac：

1. Windows 继续负责 USB-C 连接和 WinPadHost。
2. GitHub Actions 使用云端 macOS/Xcode 把 SwiftUI 项目编译成**未签名 IPA**。
3. Windows 上使用 Sideloadly，用你的 Apple ID 对 IPA 重新签名并安装到 iPad。
4. iPadOS 16+ 打开 Developer Mode。
5. 使用 pymobiledevice3 将 Windows `127.0.0.1:5959` 转发到 iPad `5959`。
6. WinPadHost 连接 `127.0.0.1:5959`，发送测试帧。

## GitHub 云端编译

仓库根目录已经包含：

`.github/workflows/build-ipad.yml`

它会在 GitHub 的 macOS runner 上运行 Xcode。iPad 项目在：

`ipad/WinPadClientProject/WinPadClient.xcodeproj`

最低系统版本已经设为 iPadOS 16.0，适合当前 iPadOS 16.3.1。

上传整个 `WinPadDisplay_MVP_Starter` 目录到 GitHub 后，进入：

Actions -> Build WinPadClient IPA -> Run workflow

编译结束后下载 Artifact `WinPadClient-unsigned-IPA`，解压得到：

`WinPadClient-unsigned.ipa`

## Windows 安装到 iPad

使用 Sideloadly 导入上述 IPA，让 Sideloadly用 Apple ID 进行开发签名并安装。

免费 Apple ID 签名通常需要定期刷新；用于开发验证足够。

## iPad 端

安装后，如果系统提示开发者模式：

Settings -> Privacy & Security -> Developer Mode -> On

重启并确认。

如果提示未受信任开发者：

Settings -> General -> VPN & Device Management -> 对应 Apple ID -> Trust

打开 WinPad Client，正常应显示：

`Listening on 5959`

## USB-C 端口转发

Windows PowerShell：

```powershell
python -m pymobiledevice3 usbmux forward 5959 5959
```

保持该窗口运行。

## Windows 测试发送端

WinPadHost 中地址使用：

`127.0.0.1`

端口：

`5959`

点击“连接并发送测试画面”。
