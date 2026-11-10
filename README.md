# Kojima

Windows 桌面灵动岛（Dynamic Island）。下载：[最新版本](https://github.com/mengtiankf/kojima/releases)

界面与调度在主进程，监控、用量、天气、股票、媒体、剪贴板、翻译、通知与频谱这些采样、解析和联网工作都放在**按需启动的子进程**里（空闲自动退出）；岛体材质、肩部曲线与主题文件沿用 [BrimDeck](https://github.com/MoFeng2223/BrimDeck) 的设计语言。

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
| 灵动岛 | 顶部居中 / 上靠左 / 上靠右三档吸附位置，也可直接拖动并吸附屏幕边缘；刘海 / 胶囊 / 指示条三种形态；悬停展开、离开自动收起，动画时长可调；展开宽度可拖动右下角或设置滑杆调整；支持多显示器选择，位置按显示器记忆，并可让其他显示器顶部同时显示副岛 |
| 收起时显示 | 时钟和天气 / 仅时钟 / 仅天气 / 媒体信息 / 不显示，五档任选 |
| 首页 | 时钟、日期、天气、媒体卡（封面 / 实时频谱 / 当前歌词 / 上一首 / 播放暂停 / 下一首）与常驻快捷操作（静音 / 截图 / 设置）；歌词来自 LRCLIB 与网易云，失败时静默降级 |
| AI 用量 | 13 个来源：Claude、Codex、Cursor、Antigravity（读取会话数据库）、DeepSeek Harness、Kimi、OpenCode、ZCode、DeepSeek、OpenRouter、GLM、NewAPI、Sub2API；今日 / 本周 / 本月 Token、额度与估算成本（额度依赖服务端公开接口，结果缓存 5 分钟并自动退避限流；没有额度接口时给出说明，成本按公开价格表估算）；每个来源可分别关闭“本机记录”与“账户额度” |
| 系统监控 | CPU（总/每核）、GPU（NVIDIA 走 nvidia-smi，其它显卡走系统 GPU 引擎计数器；显存 / 温度 / 功耗）、内存、网络、磁盘、温度、电池健康与剩余时间、前台程序帧率（ETW，读取失败时自动隐藏）、进程 TOP 7；温度与功耗可接 LibreHardwareMonitor / OpenHardwareMonitor |
| 天气 | 当前天气 + 三日预报；可在设置里填写城市（如 上海、Tokyo），留空按 IP 定位；图标为自绘矢量，不依赖字体码位 |
| 股票 | 自选股可搜索、自选、增删，内置上证 / 深证 / 茅台 / 宁德 / 比亚迪 / 腾讯等常用标的；行情源可选腾讯 / 新浪 / 东方财富（自动依次回退），刷新间隔可调，红涨绿跌 |
| 控制 | 音量、亮度、截图、音乐（可在设置里指定任意 exe）、锁屏 |
| 剪贴板 | 400 ms 序列号轮询，最近 40 条文本，点击写回剪贴板 |
| 翻译 | 中→日 / 中→英；引擎可选 腾讯翻译君（大陆直连，免费）/ MyMemory / Google / 百度 / 微软 / DeepSeek / 自定义 OpenAI 兼容大模型（自动模式优先 DeepSeek，其次腾讯翻译君，再退 MyMemory） |
| 消息 | 通知中心独立子进程读取 Windows 通知库，并在设置里提供读取频率与清空操作；CPU 占用过高、天气变化、持仓提醒也会进入同一个中心 |
| 主题 | 内置 11 套 BrimDeck 主题，支持 `%LOCALAPPDATA%\Kojima\themes` 放入自定义主题；主题编辑器用底色 / 文字色 / 强调色派生整套配色；可跟随系统深浅色或按时间段自动切换，系统高对比度开启时自动使用高对比色板 |
| 设置 | 独立设置窗口：通用 / 外观 / 功能 / 用量 / 关于；每个功能模块的详细配置在二级菜单里；主题即点即生效，滚动条为沉浸式细条；展开态右键也可打开 |
| 更新 | 启动后每天检查一次 GitHub Release；安装版静默调用安装包，便携版解压替换后自动重启；构建产物附带 SHA-256 校验文件 |
| 导航 | 悬停标签即切换页面，不需要点击；标签顺序可在「设置 → 功能」里用 ↑ ↓ 调整 |

## 架构

```
Kojima.exe (Shell：单实例/托盘/监督)      Kojima.Agents (子进程, 按需拉起)
  └─ 拉起并守护 Kojima.Island.exe          ├─ monitor    CPU/GPU/内存/网络/进程采样 + 占用提醒
        │ 命名管道 kojima-shell            ├─ usage      本地会话日志 + 账户额度 + 缓存/限流
        ▼                                  ├─ weather   wttr.in 请求、解析与雨雪提醒
Kojima.Island.exe (WPF, UI 进程)           ├─ stocks    腾讯行情、GBK 解码与 ±5% 提醒
  ├─ 灵动岛窗口 / 页面 / 热键               ├─ media     SMTC 会话、封面与播放控制
  ├─ ConfigStore / ThemeCatalog            ├─ clipboard 剪贴板轮询与历史
  ├─ 薄代理（媒体/剪贴板/翻译/消息/频谱）    ├─ translate 翻译请求
  └─ AgentSupervisor ─── NamedPipe ───▶    ├─ messages  Windows 通知库读取
                                           ├─ spectrum  WASAPI 环回频谱
                                           ├─ controls  音量与亮度
                                           └─ updates   GitHub Release 检查与安装包下载
```

- `Kojima.exe` 只负责单实例、托盘、退出与守护 `Kojima.Island.exe`；灵动岛、设置页和所有功能调度都在 UI 子进程里，UI 崩溃时 Shell 会自动把它拉起来。
- 子进程只在功能被真正使用时由 `AgentSupervisor` 拉起，空闲 `IdleExitSeconds`（默认 90 秒）后自行退出；关闭功能开关会立即回收。
- 子进程意外退出会被 `Process.Exited` 捕获：记录退出码、回收句柄，并按 2/4/8/16/30 秒退避后再拉起；连续稳定运行 30 秒后才清除失败计数，避免崩溃循环。
- 提醒规则（CPU ≥ 90%、雨雪、持仓 ±5%）也在子进程内判定，通过 `{Event="notice", Data={title,body,kind}}` 推给 UI 进程进入消息中心。
- IPC 是逐行 JSON 的命名管道：`kojima-{feature}-{uiPid}`，UI 进程发 `IpcEnvelope{Id,Method,Params}`，子进程回 `{Id,Result}` 或推 `{Event,Data}`。
- 共享模型、配置、主题与几何算法都在 `Kojima.Core`，三个进程引用同一份代码。

## 安装

从 [Releases](https://github.com/mengtiankf/kojima/releases) 下载，或本地构建后从 `dist/` 取用：

- `dist/KojimaSetup-1.3.1.exe`：Inno Setup 安装包（默认装到 `%LOCALAPPDATA%\Programs\Kojima`，可选开机启动、桌面快捷方式）
- `publish/`：绿色版，运行 `Kojima.exe`（Shell 会自动拉起 `Kojima.Island.exe`）

配置与日志在 `%LOCALAPPDATA%\Kojima`：

```
config.json      # 设置（位置、形态、主题、收起显示、功能开关、用量来源、监控数据源；不含任何密钥）
secrets.dat      # 密钥（DPAPI 当前用户加密，仅本机可解；首次启动会把旧版 config.json 里的明文密钥迁走并抹除）
logs/            # shell.log / island.log / agent-*.log
themes/          # 自定义主题（JSON，可带 // 注释）
```

密钥只写入 `secrets.dat`，`config.json` 与日志里都不会出现明文；用量读取只使用本地日志或各服务的官方接口，不写回、不续期任何登录状态。

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
dotnet test tests/Kojima.Tests/Kojima.Tests.csproj
dotnet run --project src/Kojima.Shell
```

打安装包与便携版（publish → Inno Setup → zip + SHA-256）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

需要 Inno Setup 6（脚本会自动查找 `%LOCALAPPDATA%` 与 `Program Files` 下的 `ISCC.exe`）。产物在 `dist/`：`KojimaSetup-<版本>.exe` 安装版和 `Kojima-<版本>-portable.zip` 便携版，各带 `.sha256` 校验文件；不再构建 MSI。

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
