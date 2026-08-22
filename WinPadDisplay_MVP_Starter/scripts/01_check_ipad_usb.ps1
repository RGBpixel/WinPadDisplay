$ErrorActionPreference = "Stop"
Write-Host "== WinPadDisplay: check USB iPad ==" -ForegroundColor Cyan

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python not found. Install Python 3.10+ first."
}

python -m pip show pymobiledevice3 *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing pymobiledevice3..." -ForegroundColor Yellow
    python -m pip install -U pymobiledevice3
}

python -m pymobiledevice3 usbmux list
