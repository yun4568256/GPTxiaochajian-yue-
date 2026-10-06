$ErrorActionPreference = 'Stop'
$base = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$target = [IO.Path]::GetFullPath((Join-Path $base 'OpenAiUsageOverlay'))
if (-not $target.StartsWith($base + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw '卸载路径不在当前用户的 Programs 目录内。'
}
if (Get-Process -Name 'OpenAiUsageOverlay' -ErrorAction SilentlyContinue) {
    throw '请先右键退出 OpenAI 用量小助手，再卸载。'
}

$linkPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenAI 官方用量小助手.lnk'
if (Test-Path -LiteralPath $linkPath) {
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($linkPath)
    $expectedExe = Join-Path $target 'OpenAiUsageOverlay.exe'
    if ($shortcut.TargetPath -eq $expectedExe) { Remove-Item -LiteralPath $linkPath -Force }
}
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Write-Host 'OpenAI 用量小助手已卸载。本机加密密钥和设置仍留在本机数据目录，可在卸载前从程序右键菜单清除密钥。'
