#requires -Version 5.1
param([switch]$PrepareCompiler)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$projectRoot = $PSScriptRoot
$compilerRoot = Join-Path $projectRoot '.tools\nsis-3.13'
$compiler = Join-Path $compilerRoot 'makensis.exe'
$bootstrap = Join-Path $projectRoot '.tools\nsis-3.13.zip'
$expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
if (-not (Test-Path -LiteralPath $compiler)) {
    $systemMakensis = @('C:\Program Files (x86)\NSIS\makensis.exe', 'C:\Program Files\NSIS\makensis.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $systemMakensis) {
        $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
        if ($cmd) { $systemMakensis = $cmd.Source }
    }
    if ($systemMakensis) {
        $compiler = $systemMakensis
    } else {
        if (-not (Test-Path -LiteralPath $bootstrap)) {
            throw '请将官方 NSIS 3.13 ZIP 放入 .tools\nsis-3.13.zip；官网 https://nsis.sourceforge.io/Download'
        }
        if ((Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash -ne $expectedHash) { throw '编译器安装包 SHA-256 不匹配' }
        Expand-Archive -LiteralPath $bootstrap -DestinationPath (Join-Path $projectRoot '.tools') -Force
        if (-not (Test-Path -LiteralPath $compiler)) { throw '编译器解压失败' }
    }
}
if ($PrepareCompiler) { Write-Output "编译器已就绪：$compiler"; return }
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
$application = Join-Path $projectRoot 'artifacts\ClipDesk\ClipDesk.exe'
if (-not (Test-Path -LiteralPath $application)) { throw '请先执行 build.ps1 -Portable -Verify' }
if ((Get-Item -LiteralPath $application).VersionInfo.ProductVersion -notlike "$version*") { throw '发布目录的程序版本不匹配' }
$payload = Join-Path $projectRoot 'artifacts\ClipDesk'
New-Item -ItemType Directory -Path (Join-Path $projectRoot '.tools') -Force | Out-Null
$uninstallList = Join-Path $projectRoot '.tools\uninstall-files.nsh'
$lines = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
    if ($file.Extension -eq '.pdb') { continue }
    $relative = $file.FullName.Substring($payload.Length).TrimStart('\', '/').Replace('$', '$$').Replace('"', '$\"')
    $lines.Add('Delete "$INSTDIR\' + $relative + '"')
}
foreach ($directory in Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
    $relative = $directory.FullName.Substring($payload.Length).TrimStart('\', '/').Replace('$', '$$').Replace('"', '$\"')
    $lines.Add('RMDir "$INSTDIR\' + $relative + '"')
}
[IO.File]::WriteAllLines($uninstallList, $lines, [Text.UTF8Encoding]::new($true))
& $compiler /INPUTCHARSET UTF8 /OUTPUTCHARSET UTF8 /WX /V2 "/DAPP_VERSION=$version" "/DPROJECT_ROOT=$projectRoot" "/DUNINSTALL_LIST=$uninstallList" (Join-Path $projectRoot 'packaging\ClipDesk.nsi')
if ($LASTEXITCODE -ne 0) { throw '中文安装器构建失败' }
Write-Output "安装包已生成：$(Join-Path $projectRoot "artifacts\ClipDesk-$version-win-x64-setup.exe")"
