# 第三方资源

## BrimDeck

- 来源：https://github.com/MoFeng2223/BrimDeck
- 许可：Apache License 2.0
- 使用范围：`assets/themes/*.json`（11 套主题文件原样复制）、岛体几何与配色方案的设计参考、`plugins/Kojima.Plugin.Monitor/Native/FrameRates.cs`（前台帧率 ETW 采样，移植自 `src/BrimDeck/Native/FrameRates.cs`，Apache-2.0）。
- 歌词实现参考并适配 BrimDeck 的在线歌词匹配、网易云日志解码和 Apple Music TTML 缓存算法，涉及 `plugins/Kojima.Plugin.Media/Features/LyricsProvider*.cs`、`NeteaseLyricsLog.cs`、`AppleLyricsCache.cs`，同样按 Apache-2.0 使用。

主题 JSON 的版权归 BrimDeck 作者所有，按 Apache-2.0 保留原文件中的 author / license 字段。`FrameRates.cs` 与上述衍生实现保留原版权与 Apache-2.0 声明。

## 图标字体

Segoe Fluent Icons / Segoe MDL2 Assets 为 Windows 系统自带字体，仅通过字体族名引用，未分发字体文件。
