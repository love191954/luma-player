param(
    [string]$PlayerPath
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedPlayerPath = if ([String]::IsNullOrWhiteSpace($PlayerPath)) {
    Join-Path $projectRoot "dist\LumaPlayer.exe"
}
else {
    [System.IO.Path]::GetFullPath($PlayerPath)
}
$reportDir = Join-Path $projectRoot "diagnostics"

if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
    throw "播放器不存在：$resolvedPlayerPath"
}

New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $reportDir ("startup-ui-report-" + $stamp + ".json")

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class LumaStartupProbe {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    public static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static long GetExtendedStyle(IntPtr hWnd) {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, -20).ToInt64() : GetWindowLongPtr32(hWnd, -20).ToInt64();
    }

    public static int GetOpacity(IntPtr hWnd) {
        const long WS_EX_LAYERED = 0x00080000L;
        if ((GetExtendedStyle(hWnd) & WS_EX_LAYERED) == 0) return 255;
        uint colorKey;
        byte alpha;
        uint flags;
        return GetLayeredWindowAttributes(hWnd, out colorKey, out alpha, out flags) ? alpha : 0;
    }

    public static string[] GetVisibleTextBounds(IntPtr parent) {
        List<string> values = new List<string>();
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr ignored) {
            if (!IsWindowVisible(child)) return true;
            StringBuilder text = new StringBuilder(256);
            GetWindowText(child, text, text.Capacity);
            if (text.Length == 0) return true;
            RECT rect;
            if (!GetWindowRect(child, out rect)) return true;
            values.Add(text.ToString() + "|" + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom);
            return true;
        }, IntPtr.Zero);
        return values.ToArray();
    }
}
"@

$expectedTexts = @(
    "已播放 00:00", "剩余 00:00", "总时长 00:00", "打开", "−10 秒", "播放",
    "+10 秒", "静音", "音量", "倍速 1×", "音轨", "字幕", "关联格式", "全屏", "等待打开视频"
)
$process = $null

function Convert-BoundsSnapshot {
    param([string[]]$Rows)
    $result = [ordered]@{}
    foreach ($row in $Rows) {
        $separator = $row.LastIndexOf('|')
        if ($separator -le 0) { continue }
        $text = $row.Substring(0, $separator)
        if ($text -notin $expectedTexts) { continue }
        $result[$text] = $row.Substring($separator + 1)
    }
    return $result
}

try {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($resolvedPlayerPath)
    if ($null -eq $process) { throw "无法启动 Luma Player。" }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $handle = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline -and $handle -eq [IntPtr]::Zero) {
        $process.Refresh()
        $handle = $process.MainWindowHandle
        if ($handle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 2 }
    }
    if ($handle -eq [IntPtr]::Zero) { throw "10 秒内没有发现播放器主窗口。" }

    $sawTransparentWindow = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $opacity = [LumaStartupProbe]::GetOpacity($handle)
        if ($opacity -eq 0) { $sawTransparentWindow = $true }
        if ([LumaStartupProbe]::IsWindowVisible($handle) -and $opacity -gt 0) { break }
        Start-Sleep -Milliseconds 2
    }
    if ([LumaStartupProbe]::GetOpacity($handle) -le 0) { throw "播放器窗口没有进入可见状态。" }

    $firstVisibleMilliseconds = $timer.Elapsed.TotalMilliseconds
    $firstSnapshot = Convert-BoundsSnapshot -Rows ([LumaStartupProbe]::GetVisibleTextBounds($handle))
    $missingAtFirstVisible = @($expectedTexts | Where-Object { -not $firstSnapshot.Contains($_) })

    Start-Sleep -Milliseconds 500
    $stableSnapshot = Convert-BoundsSnapshot -Rows ([LumaStartupProbe]::GetVisibleTextBounds($handle))
    $changedBounds = @($expectedTexts | Where-Object {
        -not $firstSnapshot.Contains($_) -or -not $stableSnapshot.Contains($_) -or $firstSnapshot[$_] -ne $stableSnapshot[$_]
    })

    $report = [ordered]@{
        generatedAt = (Get-Date).ToString("o")
        playerPath = $resolvedPlayerPath
        firstVisibleMilliseconds = [Math]::Round($firstVisibleMilliseconds, 3)
        transparentLayoutStageObserved = $sawTransparentWindow
        allControlsPresentAtFirstVisibleFrame = $missingAtFirstVisible.Count -eq 0
        layoutStableForFirst500Milliseconds = $changedBounds.Count -eq 0
        missingAtFirstVisibleFrame = $missingAtFirstVisible
        changedBounds = $changedBounds
        firstVisibleBounds = $firstSnapshot
        stableBounds = $stableSnapshot
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Write-Host "启动 UI 验证完成"
    Write-Host "  首次可见：$($report.firstVisibleMilliseconds) 毫秒"
    Write-Host "  第一帧控件完整：$($report.allControlsPresentAtFirstVisibleFrame)"
    Write-Host "  前 500 毫秒布局稳定：$($report.layoutStableForFirst500Milliseconds)"
    Write-Host "  报告：$reportPath"

    if (-not $report.allControlsPresentAtFirstVisibleFrame -or -not $report.layoutStableForFirst500Milliseconds) {
        throw "启动 UI 验收失败。"
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(2000)) { $process.Kill() }
    }
    if ($null -ne $process) { $process.Dispose() }
}
