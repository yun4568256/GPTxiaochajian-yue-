param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\CaapiUsageOverlay'),
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$sourceExe = Join-Path $source 'CaapiUsageOverlay.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw '请先解压完整下载包，确认 app\CaapiUsageOverlay.exe 存在。'
}

$destination = [IO.Path]::GetFullPath($InstallRoot)
$targetExe = Join-Path $destination 'CaapiUsageOverlay.exe'
if ((Test-Path -LiteralPath $targetExe) -and
    (Get-Process -Name 'CaapiUsageOverlay' -ErrorAction SilentlyContinue)) {
    throw '请先右键退出正在运行的 CAAPI 用量小助手，再重新安装。'
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force

if (-not $NoStartup) {
    $linkPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'CAAPI 用量小助手.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($linkPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $destination
    $shortcut.Description = '点击查询 CAAPI 用量'
    $shortcut.Save()
    Write-Host "已创建登录自启快捷方式：$linkPath"
}

Write-Host "已安装到：$destination"
if (-not $NoLaunch) { Start-Process -FilePath $targetExe }
