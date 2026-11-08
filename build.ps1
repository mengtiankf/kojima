# Build Kojima: publish -> Inno Setup exe -> WiX msi
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.1.0]

param(
    [string]$Version = "1.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$publish = Join-Path $root "publish"
$dist = Join-Path $root "dist"

Write-Host "== clean =="
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $publish | Out-Null
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Host "== publish shell (self-contained win-x64) =="
dotnet publish "src/Kojima.Shell/Kojima.Shell.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish shell failed" }

Write-Host "== publish agents =="
dotnet publish "src/Kojima.Agents/Kojima.Agents.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish agents failed" }

Write-Host "== copy assets =="
if (Test-Path $publish\assets) { Remove-Item $publish\assets -Recurse -Force }
Copy-Item "assets" $publish -Recurse
Copy-Item "README.md","LICENSE","THIRD_PARTY.md" $publish -Force

Write-Host "== Inno Setup =="
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found, install Inno Setup 6" }
& $iscc "/DAppVersion=$Version" "installer\kojima.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Write-Host "== WiX msi =="
$wix = @(
    "$env:USERPROFILE\.dotnet\tools\wix.exe",
    "wix.exe"
) | Where-Object { $_ -eq "wix.exe" -or (Test-Path $_) } | Select-Object -First 1
if (-not $wix) { throw "wix.exe not found, run: dotnet tool install --global wix" }
& $wix build "installer\kojima.wxs" -d "ProjectDir=$root/" -o "$dist\Kojima-$Version-x64.msi" -arch x64
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Write-Host "== done =="
Get-ChildItem $dist | Format-Table Name, Length
