param(
    [string]$Version = "0.4.1"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$distRoot = Join-Path $projectRoot "dist"
$outputRoot = Join-Path $projectRoot "release"
$stageRoot = Join-Path $outputRoot ("LumaPlayer-" + $Version + "-win-x64")
$zipPath = $stageRoot + ".zip"
$requiredFiles = @(
    "LumaPlayer.exe",
    "mpv.exe",
    "mpv.com",
    "d3dcompiler_43.dll",
    "input.conf",
    "luma.ico",
    "README.md"
)
$installerFiles = @(
    "Install-LumaPlayer.ps1",
    "安装 Luma Player.bat"
)

foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $distRoot $requiredFile))) {
        throw "发布目录缺少文件：$requiredFile。请先运行 .\build.ps1。"
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
$resolvedStageRoot = [System.IO.Path]::GetFullPath($stageRoot)
if (-not $resolvedStageRoot.StartsWith($resolvedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布暂存目录超出允许范围：$resolvedStageRoot"
}
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

foreach ($requiredFile in $requiredFiles) {
    Copy-Item -LiteralPath (Join-Path $distRoot $requiredFile) -Destination $stageRoot -Force
}

foreach ($installerFile in $installerFiles) {
    $installerSource = Join-Path $projectRoot $installerFile
    if (-not (Test-Path -LiteralPath $installerSource -PathType Leaf)) {
        throw "发布包缺少安装入口：$installerFile"
    }
    Copy-Item -LiteralPath $installerSource -Destination $stageRoot -Force
}

$licenseRoot = Join-Path $stageRoot "licenses\mpv"
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "GPL-2.0.txt") -Destination (Join-Path $licenseRoot "GPL-2.0.txt") -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$manifest = [ordered]@{
    product = "Luma Player"
    version = $Version
    platform = "Windows x64"
    generatedAt = (Get-Date).ToString("o")
    archive = [System.IO.Path]::GetFileName($zipPath)
    sha256 = $zipHash
    installer = [ordered]@{
        entryPoint = "安装 Luma Player.bat"
        mode = "per-user"
        requiresAdministrator = $false
    }
    files = Get-ChildItem -LiteralPath $stageRoot -Recurse -File | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stageRoot.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
}
$manifestPath = Join-Path $outputRoot ("LumaPlayer-" + $Version + "-win-x64-manifest.json")
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "发布包：$zipPath"
Write-Host "SHA-256：$zipHash"
Write-Host "清单：$manifestPath"
