#requires -Version 5.1
param([switch]$Portable, [switch]$Verify, [switch]$NativeChecks)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$projectRoot = $PSScriptRoot
$bundledDotnet = Join-Path (Split-Path $projectRoot -Parent) '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.build-home\nuget'
$appProject = Join-Path $projectRoot 'src\ClipDesk.App\ClipDesk.App.csproj'
$checkProject = Join-Path $projectRoot 'tests\ClipDesk.Checks\ClipDesk.Checks.csproj'
$packageSource = Join-Path $projectRoot '.packages'
$sourceArgs = if (Test-Path -LiteralPath $packageSource) { @('--source', $packageSource) } else { @() }
& $dotnetExe build $appProject -c Release --nologo -warnaserror @sourceArgs
if ($LASTEXITCODE -ne 0) { throw '桌面程序构建失败' }
if ($Verify) {
    & $dotnetExe build $checkProject -c Release --nologo -warnaserror @sourceArgs
    if ($LASTEXITCODE -ne 0) { throw '检查程序构建失败' }
    $checkRoot = Join-Path $projectRoot ('test-data\protocol-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    & $dotnetExe (Join-Path $projectRoot 'tests\ClipDesk.Checks\bin\Release\net10.0\ClipDesk.Checks.dll') $checkRoot
    if ($LASTEXITCODE -ne 0) { throw '数据或协议检查未通过，请查看检查目录' }
    $windowCheckProject = Join-Path $projectRoot 'tests\ClipDesk.WindowChecks\ClipDesk.WindowChecks.csproj'
    & $dotnetExe build $windowCheckProject -c Release --nologo -warnaserror @sourceArgs
    if ($LASTEXITCODE -ne 0) { throw '窗口检查程序构建失败' }
    & $dotnetExe (Join-Path $projectRoot 'tests\ClipDesk.WindowChecks\bin\Release\net10.0-windows\ClipDesk.WindowChecks.dll')
    if ($LASTEXITCODE -ne 0) { throw '快捷键或原生输入框识别检查未通过' }
    & (Join-Path $projectRoot 'tests\ClipDesk.EditorChecks\run.ps1')
}
if ($Portable) {
    $outputPath = Join-Path $projectRoot 'artifacts\ClipDesk'
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
    & $dotnetExe publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --nologo -warnaserror -o $outputPath @sourceArgs
    if ($LASTEXITCODE -ne 0) { throw '便携版发布失败' }
    Copy-Item -LiteralPath (Join-Path $projectRoot '使用说明.md'),(Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $outputPath -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $outputPath -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $outputPath -Recurse -Force
    [xml]$versionProps = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $releaseVersion = $versionProps.Project.PropertyGroup.Version
    $zipPath = Join-Path $projectRoot "artifacts\ClipDesk-$releaseVersion-win-x64.zip"
    Compress-Archive -LiteralPath $outputPath -DestinationPath $zipPath -Force
    Write-Output "便携包已生成：$zipPath"
}
if ($NativeChecks) {
    $previousData = $env:CLIPDESK_DATA
    try {
        $env:CLIPDESK_DATA = Join-Path $projectRoot ('test-data\native-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
        & $dotnetExe (Join-Path $projectRoot 'src\ClipDesk.App\bin\Release\net10.0-windows\ClipDesk.dll') --native-check
        if ($LASTEXITCODE -ne 0) { throw "原生检查未通过，请查看 $env:CLIPDESK_DATA\native-check.json" }
        Write-Output "原生检查结果：$env:CLIPDESK_DATA\native-check.json"
    } finally { $env:CLIPDESK_DATA = $previousData }
}
