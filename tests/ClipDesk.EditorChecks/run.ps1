$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$bundledDotnet = Join-Path (Split-Path $projectRoot -Parent) '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.build-home\nuget'
$packageSource = Join-Path $projectRoot '.packages'
$sourceArgs = if (Test-Path -LiteralPath $packageSource) { @('--source', $packageSource) } else { @() }
$checkProject = Join-Path $PSScriptRoot 'ClipDesk.EditorChecks.csproj'
& $dotnetExe build $checkProject -c Release --nologo -warnaserror --disable-build-servers @sourceArgs
if ($LASTEXITCODE -ne 0) { throw '编辑器检查构建失败' }
$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 7)
$checkRoot = Join-Path $projectRoot ('test-data\editor-' + $stamp)
$checkDll = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\ClipDesk.EditorChecks.dll'
& $dotnetExe $checkDll $checkRoot
if ($LASTEXITCODE -ne 0) { throw "编辑器检查失败，请查看 $checkRoot" }
Write-Output "编辑器检查结果：$checkRoot\editor-check.json"
