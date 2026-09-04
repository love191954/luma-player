# Luma Player 0.4.1

面向 Windows 10/11 的高性能本地视频播放器。界面只保留打开、播放/暂停、前后跳转、静音、音量、倍速、音轨、字幕、格式关联和全屏；解码与 HDR 色彩处理由 mpv 0.41+、FFmpeg 和 libplacebo 完成。

## 直接使用

1. 解压整个发布包，不能只拖出一个 EXE。
2. 双击 `LumaPlayer.exe`。
3. 把视频拖进窗口，或点击“打开”。
4. 想要双击视频直接播放，点击“关联格式”，再在 Windows 默认应用页确认 Luma Player。

## 一键安装

解压发布包后，双击 `安装 Luma Player.bat`。安装程序会把完整播放器复制到当前用户的 `%LocalAppData%\Programs\LumaPlayer`，创建开始菜单快捷方式，并为 14 种视频格式注册 Luma Player 图标和打开命令。

Windows 10/11 会要求在“默认应用”页面完成最终确认。播放器不能绕过系统对默认应用的保护。资源管理器显示的是 Luma Player 的稳定文件图标，不是读取视频首帧生成的缩略图。

播放器会自动选择硬件解码，并根据当前显示器自动输出 HDR 或把 HDR/杜比视界正确映射到 SDR。普通用户不需要配置播放器。

## 操作

| 操作 | 快捷键 |
|---|---|
| 打开视频 | `Ctrl+O` 或拖入文件 |
| 播放 / 暂停 | `Space` |
| 点击前后跳 10 秒 | `−10 秒` / `+10 秒` 按钮 |
| 键盘前后跳 5 秒 | `←` / `→` |
| 音量 | `↑` / `↓` |
| 静音 | `M` |
| 全屏 / 退出全屏 | `F` / `Esc` |

控制栏会分别显示已播放、剩余和总时长。“倍速”菜单提供 1×、2×、3×、4×。全屏期间所有控件和鼠标光标均隐藏；使用快捷键控制，按 `Esc` 返回窗口后控制栏恢复。

Windows 10/11 不允许应用静默抢占默认文件关联。“关联格式”会完成 Luma Player 注册并打开系统确认页，最终选择必须由用户确认一次。

0.4.1 控制栏采用深色影院风格：石墨色面板、细边框、轻微高光和暖橙色播放主按钮。窗口在布局全部完成后才显示，并使用双缓冲绘制，避免启动时按钮或文字先残缺、随后跳变。所有原有功能按键和快捷键均保留。

## 开发者：构建

在 PowerShell 中运行：

```powershell
cd .\LumaPlayer
.\build.ps1
```

如果首次构建缺少图标资源，先运行：

```powershell
& "C:\Users\LEGION\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" .\tools\generate_luma_icon.py
```

脚本会编译原生 WinForms 外壳，并下载微软 WinGet 清单固定的 mpv 0.41.0 Windows 构建；SHA-256 不匹配时会拒绝使用。完成后双击 `dist\LumaPlayer.exe`。

如果 `dist` 已经有 `mpv.exe`，可离线重建：

```powershell
.\build.ps1 -SkipMpvDownload
```

## HDR 与杜比视界输出边界

- 渲染固定使用 `gpu-next` + libplacebo，Windows 输出使用 D3D11 flip model，并启用 `target-colorspace-hint=auto`。HDR/SDR 目标色彩空间由显示器、Windows 和驱动能力共同决定。
- Dolby Vision Profile 5 可进行 DV 重塑并转换成显示目标需要的 HDR/PQ 或 SDR；HDR10、HLG 也会按显示能力输出或色调映射。
- Dolby Vision Profile 7 的基础层和 RPU 可以处理，但 FEL 增强层是否完整使用取决于 FFmpeg/mpv 支持；本项目不虚假宣称完整 FEL 解码。
- Windows 上的通用第三方播放链路不会把完整 Dolby Vision 动态元数据原样传给电视。当前方案以色度正确的目标显示映射为目标；电视是否点亮“Dolby Vision”标志不在本项目可保证范围内。
- HDR 显示器请在 Windows“设置 → 系统 → 显示 → HDR”中打开 HDR，并使用显卡厂商最新驱动。未开启 HDR 时，播放器会把 HDR/DV 正确映射到 SDR，而不是显示灰雾或错误色彩。

## 架构

`LumaPlayer.exe` 只负责窗口与简单控制，通过本地命名管道控制同目录的 `mpv.exe`。视频帧不会经过 .NET UI 或 CPU 回拷：硬件解码、缩放、字幕合成、色彩管理和 D3D11 呈现均在 mpv/FFmpeg/libplacebo 内完成。

## 系统要求

- Windows 10 1607 或更高，推荐 Windows 11 24H2 或更新版本
- x64 CPU
- HDR 建议使用支持 10-bit HEVC/AV1 硬件解码的现代 Intel、AMD 或 NVIDIA GPU
- HDR 显示器/电视、正确的 10-bit 输出链路与最新显卡驱动

项目外壳基于 Windows 自带的 .NET Framework WinForms，不需要安装 .NET SDK。mpv 及其依赖遵循各自开源许可证。

## HDR 实机诊断

诊断脚本会按播放器相同的 D3D11/gpu-next 参数真实播放 12 秒，并记录杜比视界 Profile、输入/输出色彩空间、硬件解码器、掉帧和音画同步：

```powershell
.\diagnose-hdr.ps1 "D:\Videos\sample.mp4"
```

机器可读报告写入 `diagnostics\hdr-report-*.json`，mpv 原始日志写入同一目录。诊断会静音，但会显示真实 D3D11 视频窗口，以验证完整呈现路径。

验证最终播放器自身的嵌入式播放路径：

```powershell
.\verify-player.ps1 "D:\Videos\sample.mp4"
```

生成给普通用户解压即用的 ZIP：

```powershell
.\package.ps1
```

发布目录会同时包含 `安装 Luma Player.bat` 和 `Install-LumaPlayer.ps1`。便携 ZIP 与一键安装版本使用同一套播放器文件和关联规则。
