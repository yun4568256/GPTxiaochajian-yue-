$ErrorActionPreference = 'Stop'
$base = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$target = [IO.Path]::GetFullPath((Join-Path $base 'CaapiUsageOverlay'))
if (-not $target.StartsWith($base + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw '卸载路径不在当前用户的 Programs 目录内。'
}
if (Get-Process -Name 'CaapiUsageOverlay' -ErrorAction SilentlyContinue) {
    throw '请先右键退出 CAAPI 用量小助手，再卸载。'
}

$linkPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'CAAPI 用量小助手.lnk'
if (Test-Path -LiteralPath $linkPath) {
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($linkPath)
    $expectedExe = Join-Path $target 'CaapiUsageOverlay.exe'
    if ($shortcut.TargetPath -eq $expectedExe) { Remove-Item -LiteralPath $linkPath -Force }
}
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Write-Host 'CAAPI 用量小助手已卸载。网站授权信息仍留在本机数据目录，可在卸载前从程序右键菜单清除。'
