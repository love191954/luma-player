param(
    [string]$ReleaseDirectory
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedRelease = if ([String]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    Join-Path $projectRoot "dist"
}
else {
    [System.IO.Path]::GetFullPath($ReleaseDirectory)
}

if (-not (Test-Path -LiteralPath $resolvedRelease -PathType Container)) {
    throw "发布目录不存在：$resolvedRelease"
}

$requiredFiles = @(
    "LumaPlayer.exe",
    "mpv.exe",
    "mpv.com",
    "d3dcompiler_43.dll",
    "input.conf",
    "luma.ico",
    "README.md",
    "THIRD_PARTY_NOTICES.md",
    "Install-LumaPlayer.ps1",
    "安装 Luma Player.bat"
)
foreach ($requiredFile in $requiredFiles) {
    $path = Join-Path $resolvedRelease $requiredFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "发布目录缺少文件：$requiredFile"
    }
}

$iconBytes = [System.IO.File]::ReadAllBytes((Join-Path $resolvedRelease "luma.ico"))
if ($iconBytes.Length -lt 6 -or [BitConverter]::ToUInt16($iconBytes, 0) -ne 0 -or
    [BitConverter]::ToUInt16($iconBytes, 2) -ne 1) {
    throw "luma.ico 不是有效的 ICO 文件。"
}
$iconCount = [BitConverter]::ToUInt16($iconBytes, 4)
if ($iconCount -ne 7) {
    throw "luma.ico 尺寸数量错误：$iconCount"
}
$iconSizes = @()
for ($i = 0; $i -lt $iconCount; $i++) {
    $entryOffset = 6 + $i * 16
    $width = [int]$iconBytes[$entryOffset]
    if ($width -eq 0) { $width = 256 }
    $iconSizes += $width
}
if (($iconSizes -join ",") -ne "16,24,32,48,64,128,256") {
    throw "luma.ico 尺寸错误：$($iconSizes -join ',')"
}

$installerPath = Join-Path $resolvedRelease "Install-LumaPlayer.ps1"
$installerBytes = [System.IO.File]::ReadAllBytes($installerPath)
if ($installerBytes.Length -lt 3 -or $installerBytes[0] -ne 239 -or $installerBytes[1] -ne 187 -or $installerBytes[2] -ne 191) {
    throw "安装脚本必须使用带 BOM 的 UTF-8 编码，以兼容 Windows PowerShell 5.1。"
}
$installerText = Get-Content -Raw -LiteralPath $installerPath
foreach ($extension in @(
    ".mp4", ".mkv", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts",
    ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv"
)) {
    if (-not $installerText.Contains($extension)) {
        throw "安装脚本缺少扩展名：$extension"
    }
}
if (-not $installerText.Contains("SHChangeNotify") -or
    -not $installerText.Contains("LumaPlayer.Video") -or
    $installerText.Contains("UserChoice") -or
    $installerText.Contains("HKEY_LOCAL_MACHINE") -or
    $installerText.Contains("UseShellExecute")) {
    throw "安装脚本缺少关联安全检查或 Shell 刷新实现。"
}

$windowsPowerShellPath = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
if (Test-Path -LiteralPath $windowsPowerShellPath -PathType Leaf) {
    $probeVariableName = "LUMA_PLAYER_INSTALLER_PROBE_PATH"
    $previousProbeValue = [Environment]::GetEnvironmentVariable($probeVariableName, "Process")
    [Environment]::SetEnvironmentVariable($probeVariableName, $installerPath, "Process")
    $parserProbe = '$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseFile($env:LUMA_PLAYER_INSTALLER_PROBE_PATH, [ref]$tokens, [ref]$errors) | Out-Null; if ($errors.Count -gt 0) { $errors | ForEach-Object { $_.Message }; exit 1 }'
    & $windowsPowerShellPath -NoProfile -NonInteractive -Command $parserProbe
    $parserExitCode = $LASTEXITCODE
    [Environment]::SetEnvironmentVariable($probeVariableName, $previousProbeValue, "Process")
    if ($parserExitCode -ne 0) {
        throw "安装脚本无法通过 Windows PowerShell 5.1 解析。"
    }
}

$releaseName = Split-Path -Leaf $resolvedRelease
$manifestPath = Join-Path (Split-Path -Parent $resolvedRelease) ($releaseName + "-manifest.json")
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "找不到发布清单：$manifestPath"
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.installer.entryPoint -ne "安装 Luma Player.bat" -or
    [bool]$manifest.installer.requiresAdministrator) {
    throw "发布清单中的一键安装信息不正确。"
}
$manifestPaths = @($manifest.files | ForEach-Object { $_.path })
foreach ($requiredFile in $requiredFiles) {
    if ($requiredFile -notin $manifestPaths) {
        throw "发布清单缺少文件记录：$requiredFile"
    }
}

Write-Host "安装与发布内容验证通过"
Write-Host "  目录：$resolvedRelease"
Write-Host "  图标尺寸：$($iconSizes -join ', ')"
Write-Host "  清单：$manifestPath"
Write-Host "  未修改当前用户注册表。"
