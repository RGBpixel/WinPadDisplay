$ErrorActionPreference = "Stop"
Write-Host "== WinPadDisplay: USB port forwarding ==" -ForegroundColor Cyan
Write-Host "Keep the iPad connected by USB-C and keep this window open." -ForegroundColor Yellow
Write-Host "Forward Windows 127.0.0.1:5959 -> iPad TCP 5959" -ForegroundColor Yellow
python -m pymobiledevice3 usbmux forward 5959 5959
