# 微型短视频播放器

Windows 微型悬浮播放器：启动后连接红果，默认静音，支持右键菜单及鼠标上侧键隐藏或显示。字幕识别暂不包含在此版本中。

右键菜单可调整视频亮度（默认 85%）。播放器隐藏时会尝试暂停红果，重新显示时仅恢复由播放器暂停的视频。
播放器重启后，会根据进程身份恢复自己留下的自动暂停状态，避免把原本正在播放的视频一直停住。

右键菜单提供暂停、下一条、静音和音量；对应全局快捷键为 Ctrl+Alt+空格、Ctrl+Alt+右箭头、Ctrl+Alt+上箭头和 Ctrl+Alt+下箭头。“下一条”调用红果默认的下箭头快捷键，需在红果中保留该默认设置。

从 Releases 下载 `MiniVideoFloat-Setup.exe` 安装。安装版启动时和运行期间会检查本仓库的最新正式版；下载完成并通过 GitHub 提供的 SHA-256 校验后自动安装更新。红果本身的更新由红果处理。

项目采用 C#、WPF 和 .NET 8。构建安装包需要 Windows、.NET 8 SDK、Python 3、.NET Framework 4 编译器，以及另行提供的红果发行目录：

```powershell
.\build.ps1 -RedFruitDir 'D:\path\to\hongguo'
```

发布新版本前同步修改 `player/AppUpdater.cs` 中的 `CurrentVersion` 和 `installer/Setup.cs` 中的 `DisplayVersion`，然后创建对应的 `v` 开头的 GitHub Release，并上传名为 `MiniVideoFloat-Setup.exe` 的安装包。
