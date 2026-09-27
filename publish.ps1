# Publishes GO_MIDI! as a portable, self-contained folder and zips it.
#
# The result needs no installer, no MSIX registration and no machine-wide .NET
# or Windows App SDK runtime: unpack the ZIP anywhere and run GoMidi.exe.

param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'GoMidi.csproj'
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

# Default to a folder next to the project so the script works from any checkout.
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'dist' }

$publishDir = Join-Path $repo "bin\$Platform\$Configuration\net10.0-windows10.0.26100.0\$rid\publish"
$stageDir = Join-Path $OutputRoot "GO_MIDI-WinUI-$Platform"
$zipPath = Join-Path $OutputRoot "GO_MIDI-WinUI-$Platform.zip"

Write-Host "Publishing $Configuration/$Platform ($rid)..." -ForegroundColor Cyan
& dotnet publish $project `
    -c $Configuration `
    -p:Platform=$Platform `
    -r $rid `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force

# Debug symbols are not useful to end users and roughly double the ZIP size.
Get-ChildItem $stageDir -Filter '*.pdb' -Recurse | Remove-Item -Force

# A short read-me so the extracted folder explains itself.
@"
GO_MIDI! WinUI 版（便携版）
============================

运行方式
  双击 GoMidi.exe 即可，无需安装。

说明
  * 这是免安装的便携版本，可以放在任何目录、U 盘里直接运行。
  * 首次运行会在 %LOCALAPPDATA%\GoMidi 下生成 config.json 与 logs 文件夹。
  * 全局热键为 F12（播放 / 暂停）。如果 F12 已被其他程序占用，会在「设置」中提示。
  * 需要向其他程序发送按键时，该程序若以管理员身份运行，
    则需要同样以管理员身份运行本程序，否则 Windows 会拦截按键。

F12 不起作用？
  多半是被别的软件占用了。可以在「设置 → 热键」里关闭后重新打开，
  或关闭占用 F12 的程序。
"@ | Set-Content (Join-Path $stageDir '使用说明.txt') -Encoding utf8

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$files = (Get-ChildItem $stageDir -Recurse -File).Count
Write-Host "`nPortable build ready:" -ForegroundColor Green
Write-Host "  folder : $stageDir ($files files)"
Write-Host "  zip    : $zipPath ($sizeMb MB)"
