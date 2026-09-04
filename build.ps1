param(
    [switch]$SkipMpvDownload
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $projectRoot "src"
$outputDir = Join-Path $projectRoot "dist"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$iconPath = Join-Path $sourceDir "luma.ico"
$associationSpecPath = Join-Path $sourceDir "FileAssociationSpec.cs"

if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 Windows 自带的 C# 编译器：$csc"
}
if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "缺少播放器图标：$iconPath。请先运行 tools\generate_luma_icon.py。"
}
if (-not (Test-Path -LiteralPath $associationSpecPath)) {
    throw "缺少文件关联契约：$associationSpecPath"
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:x64 `
    /win32icon:"$iconPath" `
    /win32manifest:"$(Join-Path $sourceDir 'App.manifest')" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /out:"$(Join-Path $outputDir 'LumaPlayer.exe')" `
    $associationSpecPath `
    "$(Join-Path $sourceDir 'LumaPlayer.cs')"

if ($LASTEXITCODE -ne 0) {
    throw "Luma Player 编译失败，退出代码：$LASTEXITCODE"
}

if (-not $SkipMpvDownload) {
    & (Join-Path $projectRoot "download-mpv.ps1") -Destination $outputDir
}

Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $outputDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "input.conf") -Destination $outputDir -Force
Copy-Item -LiteralPath $iconPath -Destination $outputDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") -Destination $outputDir -Force
Write-Host "构建完成：$(Join-Path $outputDir 'LumaPlayer.exe')"
