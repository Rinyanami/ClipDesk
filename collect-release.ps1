#requires -Version 5.1
param([string]$PackageRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'app package'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$projectRoot = $PSScriptRoot
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
$destination = Join-Path $PackageRoot 'ClipDesk'
$packageFiles = @("ClipDesk-$version-win-x64-setup.exe", "ClipDesk-$version-win-x64.zip")
foreach ($name in $packageFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "artifacts\$name"))) { throw "缺少发布文件：$name" }
}
$null = New-Item -ItemType Directory -Path $destination -Force
$items = foreach ($name in $packageFiles) {
    $from = Join-Path $projectRoot "artifacts\$name"
    $to = Join-Path $destination $name
    Copy-Item -LiteralPath $from -Destination $to -Force
    $sourceHash = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw "复制后的校验不一致：$name" }
    [pscustomobject]@{ file = "ClipDesk\$name"; bytes = (Get-Item -LiteralPath $to).Length; sha256 = $targetHash }
}
function Write-Utf8Atomic([string]$Path, [string]$Content) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
$source = [ordered]@{
    app = 'ClipDesk'; version = $version; built_at = [DateTimeOffset]::Now.ToString('o')
    source = '本机 ClipDesk 工程；.NET 10 / WPF / SQLite'; source_project = $projectRoot
    package_type = '中文当前用户安装器和自带运行时便携 ZIP'; signature = 'NotSigned'
    files = @($items); contains_user_data_or_keys = $false
    checks = '101 项数据与模拟同步检查、18 项窗口检查通过；其他实测结果以随包 docs/验收记录.md 为准'
    limitations = '真实前台粘贴、七牛接口和两台电脑同步尚未实测；生成安装包不代表已经安装'
    installation_record = '参见工程 docs/本机安装记录.md'
}
Write-Utf8Atomic (Join-Path $destination 'source.json') ($source | ConvertTo-Json -Depth 8)
$manifestPath = Join-Path $PackageRoot 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = [pscustomobject]@{
    app = 'ClipDesk'; version = $version; file = $items[0].file; bytes = $items[0].bytes; sha256 = $items[0].sha256
    package_type = '中文当前用户安装器；另备自带运行时便携 ZIP'; signature = 'NotSigned'
    source_page = '本地工程构建'; details = 'ClipDesk\source.json'
}
$manifest.applications = @($manifest.applications | Where-Object { $_.app -ne 'ClipDesk' }) + @($entry)
$manifest.extra_files = @($manifest.extra_files | Where-Object { $_ -notmatch '^ClipDesk[/\\]' }) + @($items[1].file)
$manifest.updated_at = [DateTimeOffset]::Now.ToString('o')
Write-Utf8Atomic $manifestPath ($manifest | ConvertTo-Json -Depth 10)
$hashPath = Join-Path $PackageRoot 'SHA256SUMS.txt'
$existingHashes = @(Get-Content -LiteralPath $hashPath -Encoding UTF8 | Where-Object { $_ -notmatch 'ClipDesk[/\\]' })
$newHashes = @($items | ForEach-Object { $_.sha256 + '  ' + $_.file })
Write-Utf8Atomic $hashPath (($existingHashes + $newHashes) -join "`r`n")
$indexPath = Join-Path $PackageRoot '00-先看这里.txt'
$index = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8
$totalBytes = ($manifest.applications | Measure-Object -Property bytes -Sum).Sum
$index = [regex]::Replace($index, '(?m)^共 \d+ 款软件，(?:主安装包)?约 [\d.]+ GiB[^\r\n]*', ('共 ' + $manifest.applications.Count + ' 款软件，主安装包约 ' + [Math]::Round($totalBytes / 1GB, 2) + ' GiB（另有便携版）。'))
$index = $index.Replace('除自己编译的 Snapaste 外', '除自己编译的 Snapaste 和 ClipDesk 外')
$index = [regex]::Replace($index, '(?m)^ClipDesk \|.*(?:\r?\n)?', '')
$line = "ClipDesk | $version | 中文安装器 + 便携 ZIP | ClipDesk\ClipDesk-$version-win-x64-setup.exe"
$index = $index.Replace('怎么使用', $line + "`r`n`r`n怎么使用")
Write-Utf8Atomic $indexPath $index
$updaterPath = Join-Path $PackageRoot 'Update-Local.ps1'
if (Test-Path -LiteralPath $updaterPath) {
    $updaterContent = Get-Content -LiteralPath $updaterPath -Raw -Encoding UTF8
    $updaterContent = [regex]::Replace($updaterContent, "(?m)^\`$clipVersion\s*=\s*'[^']*'", "`$clipVersion = '$version'")
    [IO.File]::WriteAllText($updaterPath, $updaterContent, [Text.UTF8Encoding]::new($true))
}
Write-Output "已复制发布包并核对 SHA-256：$destination"
