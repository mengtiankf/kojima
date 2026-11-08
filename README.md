# Kojima

Windows 桌面灵动岛（Dynamic Island）。下载：[最新版本](https://github.com/mengtiankf/kojima/releases)

主进程只负责界面与调度，功能全部放在**按需启动的子进程**里；岛体材质、肩部曲线与主题文件沿用 [BrimDeck](https://github.com/MoFeng2223/BrimDeck) 的设计语言。

![首页](docs/doc-home.png)
![AI 用量](docs/doc-usage.png)
![系统监控](docs/doc-monitor.png)
![天气](docs/doc-weather.png)
![股票](docs/doc-stocks.png)
![控制](docs/doc-controls.png)

> 以上图片由 `KOJIMA_DEMO=1` 的演示数据渲染，不含任何真实账号或行情缓存。

## 功能

| 模块 | 说明 |
| --- | --- |
| 灵动岛 | 顶部居中 / 上靠左 / 上靠右三档位置；Notch（上凸缺口）、Capsule（胶囊）、Indicator（指示条）三种形态；悬停展开、离开自动收起，**不支持拖动**，位置只在设置里改 |
| 收起时显示 | 时钟和天气 / 仅时钟 / 仅天气 / 媒体信息 / 不显示，五档任选 |
| 首页 | 时钟、日期、天气、媒体卡与常驻快捷操作（静音 / 实时字幕 / 截图 / 设置） |
| AI 用量 | 13 个来源：Claude、Codex、Cursor、Antigravity、DeepSeek Harness、Kimi、OpenCode、ZCode、DeepSeek、OpenRouter、GLM、NewAPI、Sub2API；今日 / 本周 / 本月 Token 与额度 |
| 系统监控 | CPU（总/每核）、GPU（利用率/显存/温度/功耗）、内存、网络、磁盘、温度、进程 TOP 7；温度与功耗可接 LibreHardwareMonitor / OpenHardwareMonitor |
| 天气 | 当前天气 + 三日预报；图标为自绘矢量，不依赖字体码位 |
| 股票 | 上证 / 深证 / 茅台 / 宁德 / 比亚迪 / 腾讯，15 秒轮询，红涨绿跌 |
| 控制 | 音量、亮度、截图、实时字幕、音乐、锁屏 |
| 剪贴板 | 最近 40 条文本，点击写回剪贴板 |
| 翻译 | 中→日 / 中→英，走 DeepSeek API（密钥在设置里填） |
| 消息 | 应用内通知中心：CPU 占用过高、天气变化、持仓提醒等 |
| 主题 | 内置 11 套 BrimDeck 主题，支持 `%LOCALAPPDATA%\Kojima\themes` 放入自定义主题 |
| 设置 | 独立设置窗口：通用 / 外观 / 功能 / 用量 / 关于；主题即点即生效，滚动条为沉浸式细条 |

## 架构

```
Kojima.Shell (WPF, 主进程)             Kojima.Agents (子进程, 每个功能一个)
  ├─ 灵动岛窗口 / 页面 / 托盘 / 热键       ├─ monitor   采样 CPU/GPU/内存/网络/进程
  ├─ ConfigStore / ThemeCatalog          ├─ usage     读取本地会话日志
  └─ AgentSupervisor ─── NamedPipe ───▶  └─ …         其它功能占位
```

- 子进程只在功能被真正使用时由 `AgentSupervisor` 拉起，空闲 `IdleExitSeconds`（默认 90 秒）后自行退出；关闭功能开关会立即回收。
- IPC 是逐行 JSON 的命名管道：`kojima-{feature}-{shellPid}`，主进程发 `IpcEnvelope{Id,Method,Params}`，子进程回 `{Id,Result}` 或推 `{Event,Data}`。
- 共享模型、配置、主题与几何算法都在 `Kojima.Core`，两个进程引用同一份代码。

## 安装

从 [Releases](https://github.com/mengtiankf/kojima/releases) 下载，或本地构建后从 `dist/` 取用：

- `dist/KojimaSetup-1.1.0.exe`：Inno Setup 安装包（默认装到 `%LOCALAPPDATA%\Programs\Kojima`，可选开机启动、桌面快捷方式）
- `dist/Kojima-1.1.0-x64.msi`：WiX 安装包（per-user）
- `publish/`：绿色版，直接运行 `Kojima.exe`

配置与日志在 `%LOCALAPPDATA%\Kojima`：

```
config.json      # 设置（位置、形态、主题、收起显示、功能开关、用量来源与密钥、监控数据源）
logs/            # shell.log / agent-*.log
themes/          # 自定义主题（JSON，可带 // 注释）
```

## 快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Alt+K` | 展开 / 收起面板 |
| `Ctrl+Alt+S` | 打开设置 |
| `Ctrl+Alt+M` | 直接打开监控页（被占用时回退到 `Ctrl+Alt+O`） |

## 构建

需要 .NET 10 SDK：

```powershell
dotnet build Kojima.slnx
dotnet run --project src/Kojima.Shell
```

打安装包（publish → Inno Setup → WiX）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

需要 `ISCC.exe`（Inno Setup 6）与 `wix`（.NET 工具 5.x；v7 需接受 OSMF 协议）在 PATH 中。

## 开发钩子

| 环境变量 | 作用 |
| --- | --- |
| `KOJIMA_PREVIEW=<page>` | 启动后直接展开并切到指定页（home / usage / monitor / weather / stocks / ctl / clip / translate / msg） |
| `KOJIMA_SHOT=<path>` | 启动后把窗口渲染成 PNG（配合 `KOJIMA_SHOT_DELAY=<ms>`） |
| `KOJIMA_DEMO=1` | 用内置演示数据填充页面与通知，用于生成文档截图 |

## 致谢

- 岛体几何、主题 JSON 格式与设计语言来自 [BrimDeck](https://github.com/MoFeng2223/BrimDeck)（Apache-2.0），主题版权归各作者，详见 [THIRD_PARTY.md](THIRD_PARTY.md)。
- 图标使用 Segoe Fluent Icons；天气图标为本项目自绘。

## 许可

[MIT](LICENSE)
