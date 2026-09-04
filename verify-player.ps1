param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$VideoFile,

    [int]$SampleSeconds = 12,

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
$videoPath = [System.IO.Path]::GetFullPath($VideoFile)
$reportDir = Join-Path $projectRoot "diagnostics"

if (-not (Test-Path -LiteralPath $resolvedPlayerPath)) {
    throw "播放器不存在：$resolvedPlayerPath。请先运行 .\build.ps1。"
}
if (-not (Test-Path -LiteralPath $videoPath)) {
    throw "测试视频不存在：$videoPath"
}
if ($SampleSeconds -lt 5 -or $SampleSeconds -gt 60) {
    throw "SampleSeconds 必须在 5 到 60 之间。"
}

New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $reportDir ("embedded-report-" + $stamp + ".json")
$player = $null
$mpv = $null
$pipe = $null
$reader = $null
$writer = $null
$requestId = 0

function Get-EmbeddedProperty {
    param([Parameter(Mandatory = $true)][string]$Name)

    $script:requestId++
    $payload = @{
        command = @("get_property", $Name)
        request_id = $script:requestId
    } | ConvertTo-Json -Compress -Depth 5
    $script:writer.WriteLine($payload)
    $script:writer.Flush()

    while ($true) {
        $readTask = $script:reader.ReadLineAsync()
        if (-not $readTask.Wait(5000)) {
            throw "读取嵌入式 mpv 属性超时：$Name"
        }
        if ($null -eq $readTask.Result) {
            throw "嵌入式 mpv 控制管道已关闭。"
        }
        $message = $readTask.Result | ConvertFrom-Json
        if ($message.request_id -eq $script:requestId) {
            if ($message.error -ne "success") { return $null }
            return $message.data
        }
    }
}

function Send-EmbeddedCommand {
    param([Parameter(Mandatory = $true)][object[]]$Command)

    $payload = @{ command = $Command } | ConvertTo-Json -Compress -Depth 5
    $script:writer.WriteLine($payload)
    $script:writer.Flush()
}

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class LumaPlayerWindowProbe {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(ref CURSORINFO info);

    public static string[] GetVisibleChildTexts(IntPtr parent) {
        List<string> values = new List<string>();
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr ignored) {
            if (!IsWindowVisible(child)) return true;
            StringBuilder text = new StringBuilder(256);
            GetWindowText(child, text, text.Capacity);
            if (text.Length > 0) values.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return values.ToArray();
    }

    public static bool IsCursorVisibleNow() {
        CURSORINFO info = new CURSORINFO();
        info.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        return GetCursorInfo(ref info) && (info.flags & 1) == 1;
    }
}
"@

try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($resolvedPlayerPath)
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add($videoPath)
    $player = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $player) { throw "无法启动 Luma Player。" }

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $mpv) {
        Start-Sleep -Milliseconds 200
        $mpv = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($player.Id)" |
            Where-Object { $_.Name -eq "mpv.exe" } |
            Select-Object -First 1
    }
    if ($null -eq $mpv) { throw "20 秒内没有发现 Luma Player 的 mpv 子进程。" }

    $pipeMatch = [regex]::Match($mpv.CommandLine, 'LumaPlayer-\d+-[a-f0-9]+')
    if (-not $pipeMatch.Success) { throw "无法从 mpv 命令行识别控制管道。" }
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".", $pipeMatch.Value, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous
    )
    $pipe.Connect(10000)
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.UTF8Encoding]::new($false), $false, 8192, $true)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 8192, $true)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true

    $durationDeadline = [DateTime]::UtcNow.AddSeconds(15)
    $duration = $null
    while ([DateTime]::UtcNow -lt $durationDeadline -and $null -eq $duration) {
        $duration = Get-EmbeddedProperty -Name "duration"
        if ($null -eq $duration) { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $duration) { throw "嵌入式播放器没有完成视频载入。" }

    $player.Refresh()
    $normalRect = [LumaPlayerWindowProbe+RECT]::new()
    [LumaPlayerWindowProbe]::GetWindowRect($player.MainWindowHandle, [ref]$normalRect) | Out-Null
    $visibleControlTexts = @([LumaPlayerWindowProbe]::GetVisibleChildTexts($player.MainWindowHandle))
    $expectedControlTexts = @("打开", "−10 秒", "暂停", "+10 秒", "静音", "音量", "倍速 1×", "音轨", "字幕", "关联格式", "全屏")
    $controlsVisible = @($expectedControlTexts | Where-Object { $_ -notin $visibleControlTexts }).Count -eq 0
    $timeDisplayVisible = @(@("已播放", "剩余", "总时长") | Where-Object {
        $prefix = $_
        @($visibleControlTexts | Where-Object { $_.StartsWith($prefix) }).Count -gt 0
    }).Count -eq 3

    Send-EmbeddedCommand -Command @("set_property", "pause", $true)
    Start-Sleep -Milliseconds 250
    $pauseWorked = [bool](Get-EmbeddedProperty -Name "pause")
    Send-EmbeddedCommand -Command @("set_property", "pause", $false)

    Send-EmbeddedCommand -Command @("seek", 60, "absolute+exact")
    Start-Sleep -Milliseconds 500
    $seekPosition = [double](Get-EmbeddedProperty -Name "time-pos")
    $seekWorked = [Math]::Abs($seekPosition - 60) -lt 1.0

    Send-EmbeddedCommand -Command @("set_property", "volume", 37)
    Start-Sleep -Milliseconds 150
    $volumeWorked = [Math]::Abs([double](Get-EmbeddedProperty -Name "volume") - 37) -lt 0.5
    Send-EmbeddedCommand -Command @("set_property", "mute", $true)
    Start-Sleep -Milliseconds 150
    $muteWorked = [bool](Get-EmbeddedProperty -Name "mute")
    Send-EmbeddedCommand -Command @("set_property", "mute", $false)
    Send-EmbeddedCommand -Command @("set_property", "volume", 80)

    $speedResults = [ordered]@{}
    foreach ($speed in @(2, 3, 4)) {
        Send-EmbeddedCommand -Command @("set_property", "speed", $speed)
        Start-Sleep -Milliseconds 150
        $speedResults["${speed}x"] = [Math]::Abs([double](Get-EmbeddedProperty -Name "speed") - $speed) -lt 0.01
    }
    Send-EmbeddedCommand -Command @("set_property", "speed", 1)
    Start-Sleep -Milliseconds 150
    $speedResults["1x"] = [Math]::Abs([double](Get-EmbeddedProperty -Name "speed") - 1) -lt 0.01
    $speedControlWorked = @($speedResults.Values | Where-Object { -not $_ }).Count -eq 0

    $subtitlePath = Join-Path $reportDir ("subtitle-probe-" + $stamp + ".srt")
    @"
1
00:00:59,000 --> 00:01:05,000
Luma Player subtitle probe
"@ | Set-Content -LiteralPath $subtitlePath -Encoding UTF8
    Send-EmbeddedCommand -Command @("sub-add", $subtitlePath, "select")
    Start-Sleep -Milliseconds 300
    $subtitleTracks = @(Get-EmbeddedProperty -Name "track-list" | Where-Object { $_.type -eq "sub" })
    $selectedSubtitleTracks = @($subtitleTracks | Where-Object { $_.selected })
    $subtitleWorked = $subtitleTracks.Count -ge 1 -and $selectedSubtitleTracks.Count -eq 1

    Send-EmbeddedCommand -Command @("script-message", "luma-toggle-fullscreen")
    Start-Sleep -Milliseconds 3200
    $player.Refresh()
    $fullscreenRect = [LumaPlayerWindowProbe+RECT]::new()
    [LumaPlayerWindowProbe]::GetWindowRect($player.MainWindowHandle, [ref]$fullscreenRect) | Out-Null
    $screenWidth = [LumaPlayerWindowProbe]::GetSystemMetrics(0)
    $screenHeight = [LumaPlayerWindowProbe]::GetSystemMetrics(1)
    $fullscreenWorked = ($fullscreenRect.Right - $fullscreenRect.Left) -eq $screenWidth -and
        ($fullscreenRect.Bottom - $fullscreenRect.Top) -eq $screenHeight
    $widMatch = [regex]::Match($mpv.CommandLine, '--wid=(\d+)')
    if (-not $widMatch.Success) { throw "无法从 mpv 命令行识别视频渲染窗口。" }
    $videoSurfaceRect = [LumaPlayerWindowProbe+RECT]::new()
    [LumaPlayerWindowProbe]::GetWindowRect([IntPtr]([Int64]$widMatch.Groups[1].Value), [ref]$videoSurfaceRect) | Out-Null
    $fullscreenVideoSurfaceWorked = $videoSurfaceRect.Left -eq $fullscreenRect.Left -and
        $videoSurfaceRect.Top -eq $fullscreenRect.Top -and
        $videoSurfaceRect.Right -eq $fullscreenRect.Right -and
        $videoSurfaceRect.Bottom -eq $fullscreenRect.Bottom
    $fullscreenVisibleTexts = @([LumaPlayerWindowProbe]::GetVisibleChildTexts($player.MainWindowHandle))
    $fullscreenControlsHidden = @($expectedControlTexts | Where-Object { $_ -in $fullscreenVisibleTexts }).Count -eq 0
    $fullscreenCursorHidden = -not [LumaPlayerWindowProbe]::IsCursorVisibleNow()
    Send-EmbeddedCommand -Command @("script-message", "luma-exit-fullscreen")
    Start-Sleep -Milliseconds 500

    $beforePosition = Get-EmbeddedProperty -Name "time-pos"
    $beforeFrameDrops = [int](Get-EmbeddedProperty -Name "frame-drop-count")
    $beforeDecoderDrops = [int](Get-EmbeddedProperty -Name "decoder-frame-drop-count")
    Start-Sleep -Seconds $SampleSeconds
    $afterPosition = Get-EmbeddedProperty -Name "time-pos"

    $videoParams = Get-EmbeddedProperty -Name "video-params"
    $targetParams = Get-EmbeddedProperty -Name "video-target-params"
    $frameDrops = [int](Get-EmbeddedProperty -Name "frame-drop-count")
    $decoderDrops = [int](Get-EmbeddedProperty -Name "decoder-frame-drop-count")
    $tracks = Get-EmbeddedProperty -Name "track-list"
    $audioTracks = @($tracks | Where-Object { $_.type -eq "audio" })
    $selectedAudio = @($audioTracks | Where-Object { $_.selected })
    $report = [ordered]@{
        generatedAt = (Get-Date).ToString("o")
        playerProcessResponding = [bool]$player.Responding
        playerWindowTitle = $player.MainWindowTitle
        videoFile = $videoPath
        playerPath = $resolvedPlayerPath
        sampleSeconds = $SampleSeconds
        embeddedMpvCommandLine = $mpv.CommandLine
        playback = [ordered]@{
            beforePosition = $beforePosition
            afterPosition = $afterPosition
            advancedSeconds = [Math]::Round(([double]$afterPosition - [double]$beforePosition), 3)
            paused = Get-EmbeddedProperty -Name "pause"
            hardwareDecoder = Get-EmbeddedProperty -Name "hwdec-current"
            videoOutput = Get-EmbeddedProperty -Name "current-vo"
            audioOutput = Get-EmbeddedProperty -Name "current-ao"
            audioCodec = Get-EmbeddedProperty -Name "audio-codec-name"
            audioTrackCount = $audioTracks.Count
            selectedAudioTrackCount = $selectedAudio.Count
            frameDropCount = $frameDrops
            decoderFrameDropCount = $decoderDrops
            sampleFrameDropCount = $frameDrops - $beforeFrameDrops
            sampleDecoderFrameDropCount = $decoderDrops - $beforeDecoderDrops
            avSyncSeconds = Get-EmbeddedProperty -Name "avsync"
        }
        color = [ordered]@{
            dolbyVisionDetected = [bool]($videoParams.colormatrix -eq "dolbyvision")
            sourcePrimaries = $videoParams.primaries
            sourceTransfer = $videoParams.gamma
            targetPrimaries = $targetParams.primaries
            targetTransfer = $targetParams.gamma
        }
        integration = [ordered]@{
            embeddedWindowIdConfigured = [bool]($mpv.CommandLine -match '--wid=\d+')
            gpuNextConfigured = [bool]($mpv.CommandLine -match '--vo=gpu-next')
            d3d11Configured = [bool]($mpv.CommandLine -match '--gpu-api=d3d11')
            colorHintConfigured = [bool]($mpv.CommandLine -match '--target-colorspace-hint=auto')
            dedicatedInputConfig = [bool]($mpv.CommandLine -match '--input-conf=')
            builtInOscDisabled = [bool]($mpv.CommandLine -match '--osc=no')
            pauseControlWorked = $pauseWorked
            seekControlWorked = $seekWorked
            seekPositionSeconds = $seekPosition
            volumeControlWorked = $volumeWorked
            muteControlWorked = $muteWorked
            speedControlWorked = $speedControlWorked
            speedOptions = $speedResults
            videoMessageFullscreenWorked = $fullscreenWorked
            externalSubtitleWorked = $subtitleWorked
            subtitleTrackCount = $subtitleTracks.Count
            controlButtonsVisible = $controlsVisible
            timeDisplayVisible = $timeDisplayVisible
            visibleControlTexts = $visibleControlTexts
            fullscreenControlsHidden = $fullscreenControlsHidden
            fullscreenCursorHidden = $fullscreenCursorHidden
            fullscreenVideoSurfaceCoversWindow = $fullscreenVideoSurfaceWorked
        }
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Write-Host "嵌入式播放器验证完成"
    Write-Host "  窗口响应：$($report.playerProcessResponding)"
    Write-Host "  播放推进：$($report.playback.advancedSeconds) 秒 / 采样 $SampleSeconds 秒"
    Write-Host "  解码/渲染：$($report.playback.hardwareDecoder) / $($report.playback.videoOutput)"
    Write-Host "  音频：$($report.playback.audioCodec) / $($report.playback.audioOutput)"
    Write-Host "  杜比视界：$($report.color.dolbyVisionDetected)"
    Write-Host "  色彩：$($report.color.sourcePrimaries) / $($report.color.sourceTransfer) → $($report.color.targetPrimaries) / $($report.color.targetTransfer)"
    Write-Host "  掉帧：稳定采样新增 $($report.playback.sampleFrameDropCount + $report.playback.sampleDecoderFrameDropCount) / 全流程累计 $($frameDrops + $decoderDrops)"
    Write-Host "  控制：暂停=$pauseWorked 跳转=$seekWorked 音量=$volumeWorked 静音=$muteWorked 倍速=$speedControlWorked 全屏=$fullscreenWorked 字幕=$subtitleWorked"
    Write-Host "  界面：常规按钮=$controlsVisible 三段时间=$timeDisplayVisible 全屏控件隐藏=$fullscreenControlsHidden 光标隐藏=$fullscreenCursorHidden 画面铺满=$fullscreenVideoSurfaceWorked"
    Write-Host "  报告：$reportPath"
}
finally {
    if ($null -ne $player -and -not $player.HasExited) {
        $player.CloseMainWindow() | Out-Null
        if (-not $player.WaitForExit(2000)) { $player.Kill() }
    }
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $player) { $player.Dispose() }
    if ($null -ne $subtitlePath -and (Test-Path -LiteralPath $subtitlePath)) {
        Remove-Item -LiteralPath $subtitlePath -Force
    }
}
