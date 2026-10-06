param(
    [switch]$NoRestore,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'downloads')
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

$variants = @(
    @{ Name = 'caapi-version'; Project = 'CaapiUsageOverlay.csproj'; Archive = 'CAAPI-Usage-Overlay-win.zip' },
    @{ Name = 'openai-official-version'; Project = 'OpenAiUsageOverlay.csproj'; Archive = 'OpenAI-Usage-Overlay-win.zip' }
)

foreach ($variant in $variants) {
    $source = Join-Path $PSScriptRoot $variant.Name
    $stage = [IO.Path]::GetFullPath((Join-Path $output ('.package-' + $variant.Name)))
    if (-not $stage.StartsWith($output + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw '临时打包路径不在输出目录内。'
    }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    $app = Join-Path $stage 'app'
    New-Item -ItemType Directory -Path $app -Force | Out-Null

    & dotnet clean (Join-Path $source $variant.Project) -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw "清理旧构建失败：$($variant.Name)" }
    $publishArgs = @('publish', (Join-Path $source $variant.Project), '-c', 'Release',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $app)
    if ($NoRestore) { $publishArgs += '--no-restore' }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$($variant.Name)" }

    Get-ChildItem -LiteralPath $app -Filter '*.pdb' -File | Remove-Item -Force
    foreach ($filename in @('README.md', 'Install.ps1', 'Uninstall.ps1')) {
        Copy-Item -LiteralPath (Join-Path $source $filename) -Destination $stage -Force
    }
    foreach ($filename in @('LICENSE', 'ASSETS.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $filename) -Destination $stage -Force
    }
    $archive = Join-Path $output $variant.Archive
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Host "已生成：$archive"
}
