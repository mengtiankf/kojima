# Build Kojima: publish -> Inno Setup exe
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.3.4-beta.1]

param(
    [string]$Version = "1.3.4-beta.1"
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

Write-Host "== publish island (self-contained win-x64) =="
dotnet publish "src/Kojima.Island/Kojima.Island.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish island failed" }

Write-Host "== publish shell (self-contained win-x64) =="
dotnet publish "src/Kojima.Shell/Kojima.Shell.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish shell failed" }

Write-Host "== publish plugin host =="
dotnet publish "src/Kojima.PluginHost/Kojima.PluginHost.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish plugin host failed" }

Write-Host "== publish independent plugins =="
$plugins = @(
    @("Monitor", "monitor"), @("Usage", "usage"), @("Media", "media"),
    @("Weather", "weather"), @("Stocks", "stocks"), @("Clipboard", "clipboard"),
    @("Translate", "translate"), @("Messages", "messages"), @("Spectrum", "spectrum"),
    @("Updates", "updates"), @("Controls", "controls")
)
foreach ($plugin in $plugins) {
    $project = "plugins/Kojima.Plugin.$($plugin[0])/Kojima.Plugin.$($plugin[0]).csproj"
    $output = Join-Path $publish "plugins/$($plugin[1])"
    dotnet publish $project -c Release --self-contained false -p:DebugType=None -o $output
    if ($LASTEXITCODE -ne 0) { throw "publish plugin failed: $($plugin[0])" }
}

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

Write-Host "== portable zip + checksums =="
$zip = Join-Path $dist "Kojima-$Version-portable.zip"
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -Force
foreach ($file in Get-ChildItem $dist -File) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$($file.FullName).sha256", "$hash  $($file.Name)`n", [System.Text.Encoding]::ASCII)
}

Write-Host "== done (installer exe + portable zip, with .sha256) =="
Get-ChildItem $dist | Select-Object Name, Length | Format-Table

