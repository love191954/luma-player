param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\LumaPlayer"),
    [switch]$SkipDefaultAppsPage
)

$ErrorActionPreference = "Stop"
$sourceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$installExe = Join-Path $resolvedInstallRoot "LumaPlayer.exe"
$requiredFiles = @(
    "LumaPlayer.exe",
    "mpv.exe",
    "mpv.com",
    "d3dcompiler_43.dll",
    "input.conf",
    "README.md",
    "THIRD_PARTY_NOTICES.md"
)
$extensions = @(
    ".mp4", ".mkv", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts",
    ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv"
)

foreach ($requiredFile in $requiredFiles) {
    $sourceFile = Join-Path $sourceRoot $requiredFile
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
        throw "发布包缺少文件：$requiredFile"
    }
}

New-Item -ItemType Directory -Force -Path $resolvedInstallRoot | Out-Null
$sourceAndDestinationMatch = [StringComparer]::OrdinalIgnoreCase.Equals($sourceRoot, $resolvedInstallRoot)
if (-not $sourceAndDestinationMatch) {
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceRoot -File) {
        if ($sourceFile.Extension -in @(".ps1", ".bat")) { continue }
        Copy-Item -LiteralPath $sourceFile.FullName -Destination (Join-Path $resolvedInstallRoot $sourceFile.Name) -Force
    }

    foreach ($directoryName in @("rembg_worker", "licenses")) {
        $sourceDirectory = Join-Path $sourceRoot $directoryName
        if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
            Copy-Item -LiteralPath $sourceDirectory -Destination (Join-Path $resolvedInstallRoot $directoryName) -Recurse -Force
        }
    }
}

$currentUser = [Microsoft.Win32.Registry]::CurrentUser
$registryValueKind = [Microsoft.Win32.RegistryValueKind]::String

function Set-LumaRegistryValue {
    param(
        [string]$RelativePath,
        [string]$Name,
        [string]$Value
    )

    $key = $currentUser.CreateSubKey($RelativePath)
    if ($null -eq $key) { throw "无法创建注册表项：$RelativePath" }
    try {
        $key.SetValue($Name, $Value, $registryValueKind)
    }
    finally {
        $key.Dispose()
    }
}

$progId = "LumaPlayer.Video"
$applicationName = "Luma Player"
$openCommand = '"' + $installExe + '" "%1"'
$iconValue = '"' + $installExe + '",0'
$shortcutIconLocation = $installExe + ",0"

Set-LumaRegistryValue "Software\Classes\$progId" "" "Luma Player 视频"
Set-LumaRegistryValue "Software\Classes\$progId\DefaultIcon" "" $iconValue
Set-LumaRegistryValue "Software\Classes\$progId\shell\open\command" "" $openCommand
Set-LumaRegistryValue "Software\Classes\Applications\LumaPlayer.exe" "FriendlyAppName" $applicationName
Set-LumaRegistryValue "Software\Classes\Applications\LumaPlayer.exe\shell\open\command" "" $openCommand
Set-LumaRegistryValue "Software\LumaPlayer\Capabilities" "ApplicationName" $applicationName
Set-LumaRegistryValue "Software\LumaPlayer\Capabilities" "ApplicationDescription" "高性能 HDR 与杜比视界本地视频播放器"
Set-LumaRegistryValue "Software\RegisteredApplications" $applicationName "Software\LumaPlayer\Capabilities"

foreach ($extension in $extensions) {
    Set-LumaRegistryValue "Software\Classes\Applications\LumaPlayer.exe\SupportedTypes" $extension ""
    Set-LumaRegistryValue "Software\LumaPlayer\Capabilities\FileAssociations" $extension $progId
    Set-LumaRegistryValue "Software\Classes\$extension\OpenWithProgids" $progId ""
}

$shortcutDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $shortcutDirectory "Luma Player.lnk"
New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installExe
$shortcut.WorkingDirectory = $resolvedInstallRoot
$shortcut.IconLocation = $shortcutIconLocation
$shortcut.Description = "Luma Player HDR 与杜比视界播放器"
$shortcut.Save()
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

if (-not ("LumaShellRefresh" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class LumaShellRefresh
{
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
"@
}
[LumaShellRefresh]::SHChangeNotify(0x08000000, 0x00002000, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "Luma Player 已安装到：$resolvedInstallRoot"
Write-Host "已注册 $($extensions.Count) 种视频格式，并刷新文件图标。"

if (-not $SkipDefaultAppsPage) {
    $settingsUri = "ms-settings:defaultapps?registeredAppUser=" + [Uri]::EscapeDataString($applicationName)
    Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList @($settingsUri)
    Write-Host "请在 Windows 默认应用设置中确认 Luma Player。"
}
