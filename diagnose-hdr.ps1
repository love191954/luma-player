param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$VideoFile,

    [int]$SampleSeconds = 12
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$mpvPath = Join-Path $projectRoot "dist\mpv.exe"
$videoPath = [System.IO.Path]::GetFullPath($VideoFile)
$reportDir = Join-Path $projectRoot "diagnostics"

if (-not (Test-Path -LiteralPath $mpvPath)) {
    throw "缺少播放核心：$mpvPath。请先运行 .\build.ps1。"
}
if (-not (Test-Path -LiteralPath $videoPath)) {
    throw "测试视频不存在或尚未下载完成：$videoPath"
}
if ($SampleSeconds -lt 5 -or $SampleSeconds -gt 60) {
    throw "SampleSeconds 必须在 5 到 60 之间。"
}

New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $reportDir ("mpv-" + $stamp + ".log")
$jsonPath = Join-Path $reportDir ("hdr-report-" + $stamp + ".json")
$pipeName = "LumaPlayer-Diagnostics-" + [Guid]::NewGuid().ToString("N")
$pipePath = "\\.\pipe\" + $pipeName

$processInfo = [System.Diagnostics.ProcessStartInfo]::new($mpvPath)
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $true
$arguments = @(
    "--config=no",
    "--load-scripts=no",
    "--input-default-bindings=no",
    "--input-vo-keyboard=no",
    "--input-ipc-server=$pipePath",
    "--vo=gpu-next",
    "--gpu-api=d3d11",
    "--gpu-context=d3d11",
    "--hwdec=auto-safe",
    "--target-colorspace-hint=auto",
    "--target-colorspace-hint-mode=target",
    "--video-sync=display-resample",
    "--audio-pitch-correction=yes",
    "--mute=yes",
    "--geometry=1280x720",
    "--title=Luma Player HDR Diagnostics",
    "--log-file=$logPath",
    $videoPath
)
foreach ($argument in $arguments) {
    $processInfo.ArgumentList.Add($argument)
}

$process = $null
$pipe = $null
$reader = $null
$writer = $null
$requestId = 0

function Get-MpvProperty {
    param([Parameter(Mandatory = $true)][string]$Name)

    $script:requestId++
    $payload = @{
        command = @("get_property", $Name)
        request_id = $script:requestId
    } | ConvertTo-Json -Compress -Depth 6
    $script:writer.WriteLine($payload)
    $script:writer.Flush()

    while ($true) {
        $readTask = $script:reader.ReadLineAsync()
        if (-not $readTask.Wait(5000)) {
            throw "读取 mpv 属性超时：$Name"
        }
        $line = $readTask.Result
        if ($null -eq $line) {
            throw "mpv 控制管道已关闭。"
        }
        $message = $line | ConvertFrom-Json
        if ($message.request_id -eq $script:requestId) {
            if ($message.error -ne "success") {
                return $null
            }
            return $message.data
        }
    }
}

try {
    $process = [System.Diagnostics.Process]::Start($processInfo)
    if ($null -eq $process) {
        throw "无法启动 mpv 诊断进程。"
    }

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $pipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous
    )
    $pipe.Connect(10000)
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.UTF8Encoding]::new($false), $false, 8192, $true)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 8192, $true)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $duration = $null
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $duration) {
        $duration = Get-MpvProperty -Name "duration"
        if ($null -eq $duration) {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $duration) {
        throw "视频在 20 秒内没有完成载入。"
    }

    $before = [ordered]@{
        videoFormat = Get-MpvProperty -Name "video-format"
        videoCodec = Get-MpvProperty -Name "video-codec"
        videoParams = Get-MpvProperty -Name "video-params"
        videoOutParams = Get-MpvProperty -Name "video-out-params"
        videoTargetParams = Get-MpvProperty -Name "video-target-params"
        hardwareDecoder = Get-MpvProperty -Name "hwdec-current"
        currentVideoOutput = Get-MpvProperty -Name "current-vo"
        width = Get-MpvProperty -Name "width"
        height = Get-MpvProperty -Name "height"
        containerFps = Get-MpvProperty -Name "container-fps"
        estimatedFps = Get-MpvProperty -Name "estimated-vf-fps"
        displayFps = Get-MpvProperty -Name "display-fps"
        audioCodec = Get-MpvProperty -Name "audio-codec-name"
        durationSeconds = $duration
    }

    Write-Host "正在真实播放 $SampleSeconds 秒并采集掉帧/音画同步数据……"
    Start-Sleep -Seconds $SampleSeconds

    $after = [ordered]@{
        playbackPosition = Get-MpvProperty -Name "time-pos"
        frameDropCount = Get-MpvProperty -Name "frame-drop-count"
        decoderFrameDropCount = Get-MpvProperty -Name "decoder-frame-drop-count"
        mistimedFrameCount = Get-MpvProperty -Name "mistimed-frame-count"
        delayedFrameCount = Get-MpvProperty -Name "vo-delayed-frame-count"
        avSyncSeconds = Get-MpvProperty -Name "avsync"
    }

    $logText = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
    $dolbyMatch = [regex]::Match($logText, 'Found Dolby Vision config record: profile (\d+) level (\d+)')
    $displayMatch = [regex]::Match($logText, 'Queried output: ([^,]+), (\d+)x(\d+) @ (\d+) bits, colorspace: ([A-Z0-9_]+)')
    $swapchainMatch = [regex]::Match($logText, 'Swapchain successfully configured to color space ([A-Z0-9_]+)')

    $sourceTransfer = $before.videoParams.gamma
    $sourcePrimaries = $before.videoParams.primaries
    $targetTransfer = $before.videoTargetParams.gamma
    $targetPrimaries = $before.videoTargetParams.primaries
    $isDolbyVision = $before.videoParams.colormatrix -eq 'dolbyvision' -or $dolbyMatch.Success
    $dolbyVisionProfile = if ($dolbyMatch.Success) { [int]$dolbyMatch.Groups[1].Value } else { $null }
    $dolbyVisionLevel = if ($dolbyMatch.Success) { [int]$dolbyMatch.Groups[2].Value } else { $null }
    $isHdrSource = $isDolbyVision -or $sourceTransfer -in @("pq", "hlg")
    $swapchainColorSpace = if ($swapchainMatch.Success) { $swapchainMatch.Groups[1].Value } else { $null }
    $isHdrSwapchain = $swapchainColorSpace -match 'G2084|G10_NONE_P709|P2020'
    $isHdrOutput = $targetTransfer -in @("pq", "hlg") -and $isHdrSwapchain
    $hardwareDecoded = $before.hardwareDecoder -and $before.hardwareDecoder -ne "no"
    $totalDrops = [int]($after.frameDropCount ?? 0) + [int]($after.decoderFrameDropCount ?? 0)

    $report = [ordered]@{
        generatedAt = (Get-Date).ToString("o")
        player = "Luma Player"
        videoFile = $videoPath
        sampleSeconds = $SampleSeconds
        verdict = [ordered]@{
            hardwareDecoded = [bool]$hardwareDecoded
            dolbyVisionDetected = [bool]$isDolbyVision
            dolbyVisionProfile = $dolbyVisionProfile
            dolbyVisionLevel = $dolbyVisionLevel
            hdrSource = [bool]$isHdrSource
            hdrOutput = [bool]$isHdrOutput
            hdrMappedToSdr = [bool]($isHdrSource -and -not $isHdrOutput)
            droppedFrames = $totalDrops
            smoothPlayback = [bool]($totalDrops -le 2)
        }
        source = $before
        playback = $after
        display = [ordered]@{
            name = if ($displayMatch.Success) { $displayMatch.Groups[1].Value } else { $null }
            width = if ($displayMatch.Success) { [int]$displayMatch.Groups[2].Value } else { $null }
            height = if ($displayMatch.Success) { [int]$displayMatch.Groups[3].Value } else { $null }
            bitsPerColor = if ($displayMatch.Success) { [int]$displayMatch.Groups[4].Value } else { $null }
            reportedColorSpace = if ($displayMatch.Success) { $displayMatch.Groups[5].Value } else { $null }
            swapchainColorSpace = $swapchainColorSpace
            targetPrimaries = $targetPrimaries
            targetTransfer = $targetTransfer
        }
    }

    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    Write-Host ""
    Write-Host "诊断完成"
    Write-Host "  视频：$($before.width)x$($before.height) @ $($before.containerFps) fps"
    Write-Host "  解码：$($before.videoCodec) / $($before.hardwareDecoder)"
    if ($isDolbyVision) {
        Write-Host "  输入：Dolby Vision Profile $dolbyVisionProfile，$sourcePrimaries / $sourceTransfer"
    }
    else {
        Write-Host "  输入：$sourcePrimaries / $sourceTransfer"
    }
    if ($isHdrOutput) {
        Write-Host "  输出：HDR，$targetPrimaries / $targetTransfer，交换链 $swapchainColorSpace"
    }
    else {
        Write-Host "  输出：SDR 色调映射，$targetPrimaries / $targetTransfer，交换链 $swapchainColorSpace"
    }
    Write-Host "  掉帧：$totalDrops"
    Write-Host "  音画同步偏差：$($after.avSyncSeconds) 秒"
    Write-Host "  报告：$jsonPath"
}
finally {
    if ($null -ne $writer -and $null -ne $pipe -and $pipe.IsConnected) {
        try {
            $quit = @{ command = @("quit") } | ConvertTo-Json -Compress
            $writer.WriteLine($quit)
            $writer.Flush()
        }
        catch { }
    }
    if ($null -ne $process -and -not $process.HasExited) {
        if (-not $process.WaitForExit(1500)) {
            $process.Kill()
        }
    }
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $process) { $process.Dispose() }
}
