param(
    [Parameter(Mandatory = $false)]
    [string]$Destination = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "dist"),

    [switch]$Force
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$cacheRoot = Join-Path $projectRoot ".cache"
$mpvCache = Join-Path $cacheRoot "mpv_installer_x64.exe"
$innounpZip = Join-Path $cacheRoot "innounp-270.zip"
$innounpRoot = Join-Path $cacheRoot "innounp"
$innounpExe = Join-Path $innounpRoot "innounp.exe"
$mpvExe = Join-Path $destinationPath "mpv.exe"

# Reproducible versions and hashes from Microsoft's signed WinGet source index.
$mpvUrl = "https://github.com/0GMou/mpv2winget/releases/download/v0.41.0/mpv_installer_x64.exe"
$mpvHash = "159441C52FD74755CB74C830F661FCD9517C77056887C27AC0569876B600F0C9"
$innounpUrl = "https://raw.githubusercontent.com/jrathlev/InnoUnpacker-Windows-GUI/07ee3b1a05a26fa27a0efe110fe78dfa72c06c71/innounp-2/bin/innounp-270.zip"
$innounpHash = "D5514CAC741F12CA94F642EACD30D514D820DCE016E4D016325413DF472C34EA"

function Test-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $ExpectedHash
}

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$DestinationFile,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    if (Test-FileHash -Path $DestinationFile -ExpectedHash $ExpectedHash) {
        Write-Host "使用已校验缓存：$DestinationFile"
        return
    }

    $partialFile = $DestinationFile + ".partial"
    $downloadUrls = @(
        ("https://gh-proxy.com/" + $Url),
        $Url
    )

    foreach ($downloadUrl in $downloadUrls) {
        Write-Host "下载：$downloadUrl"
        & curl.exe `
            --fail `
            --location `
            --continue-at - `
            --retry 3 `
            --retry-delay 2 `
            --connect-timeout 20 `
            --max-time 300 `
            --output $partialFile `
            $downloadUrl

        if ($LASTEXITCODE -eq 0) {
            Move-Item -LiteralPath $partialFile -Destination $DestinationFile -Force
            if (Test-FileHash -Path $DestinationFile -ExpectedHash $ExpectedHash) {
                Write-Host "SHA-256 校验通过：$ExpectedHash"
                return
            }
            throw "SHA-256 不匹配，拒绝使用下载文件：$DestinationFile"
        }
    }

    throw "下载失败。已保留部分文件，下次运行会断点续传：$partialFile"
}

if ((Test-Path -LiteralPath $mpvExe) -and -not $Force) {
    Write-Host "mpv 已存在，跳过下载：$mpvExe"
    return
}

New-Item -ItemType Directory -Force -Path $destinationPath, $cacheRoot | Out-Null
Get-VerifiedDownload -Url $mpvUrl -DestinationFile $mpvCache -ExpectedHash $mpvHash
Get-VerifiedDownload -Url $innounpUrl -DestinationFile $innounpZip -ExpectedHash $innounpHash

if (-not (Test-Path -LiteralPath $innounpExe)) {
    New-Item -ItemType Directory -Force -Path $innounpRoot | Out-Null
    Expand-Archive -LiteralPath $innounpZip -DestinationPath $innounpRoot -Force
}
if (-not (Test-Path -LiteralPath $innounpExe)) {
    throw "innounp 解包工具缺失：$innounpExe"
}

$extractRoot = Join-Path $cacheRoot ("mpv-extract-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    & $innounpExe -x -b -q -y -o "-d$extractRoot" $mpvCache
    if ($LASTEXITCODE -ne 0) {
        throw "mpv 安装器解包失败，退出代码：$LASTEXITCODE"
    }

    $runtimeRoot = Join-Path $extractRoot "{app}"
    $runtimeFiles = @("mpv.exe", "mpv.com", "d3dcompiler_43.dll")
    foreach ($runtimeFile in $runtimeFiles) {
        $sourceFile = Join-Path $runtimeRoot $runtimeFile
        if (-not (Test-Path -LiteralPath $sourceFile)) {
            throw "mpv 安装器中缺少运行文件：$runtimeFile"
        }
        Copy-Item -LiteralPath $sourceFile -Destination $destinationPath -Force
    }
}
finally {
    $resolvedCache = [System.IO.Path]::GetFullPath($cacheRoot).TrimEnd('\') + '\'
    $resolvedExtract = [System.IO.Path]::GetFullPath($extractRoot)
    if ($resolvedExtract.StartsWith($resolvedCache, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedExtract)) {
        Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
    }
}

& (Join-Path $destinationPath "mpv.com") --version | Select-Object -First 4
Write-Host "mpv 已就绪：$mpvExe"
