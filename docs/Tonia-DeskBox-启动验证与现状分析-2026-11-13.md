# Tonia-DeskBox 启动验证与现状分析报告

| 项    | 内容                                                                                           |
| ---- | -------------------------------------------------------------------------------------------- |
| 验证时间 | 2026-11-13 09:57–10:07 (GMT+8)                                                               |
| 仓库   | `C:\Users\Tonia\WorkBuddy\Worktrees\Tonia-DeskBox\main-0d5d19e0`（worktree，HEAD `302622b`）    |
| 结论   | **可以正常启动并稳定运行，无阻塞点**；Debug/Release 构建与 publish 均 0 报错 0 警告，运行期无异常日志。但存在 3 个功能级缺陷和若干可靠性/工程化问题 |
| 代码改动 | 验证阶段无改动；**2026-11-13 后续已按 §8 修复**（9 个源码文件 + README / global.json / 文档修订，未提交） |

---

## 1. 环境、依赖与配置

| 项     | 实测值                                                                                             |
| ----- | ----------------------------------------------------------------------------------------------- |
| OS    | Windows，build `26200`（`10.0.26200`，Win11 24H2+）                                                 |
| SDK   | .NET SDK `8.0.422`（`D:\Devlopment\sdk\dotnet\8.0\sdk`）                                          |
| 运行时   | `Microsoft.WindowsDesktop.App 8.0.28`、`Microsoft.NETCore.App 8.0.28`                            |
| 目标框架  | `net8.0-windows`，`UseWPF=true` + `UseWindowsForms=true`，`Nullable=enable`                       |
| 第三方依赖 | **0 个 `PackageReference`**，纯 BCL + Win32 P/Invoke（user32 / dwmapi / shell32 / gdi32 / WinForms） |
| 清单    | `app.manifest`：`asInvoker`（不提权）、`PerMonitorV2` DPI、supportedOS = Win10/11                       |
| 入口    | `App.xaml` `ShutdownMode=OnExplicitShutdown`（托盘常驻应用，无主窗口概念）                                     |
| 图标资源  | `app.ico` 131420 B，合法 ICO 头 `00 00 01 00 07 00`（7 个图像尺寸）                                        |
| 代码规模  | 17 个 `.cs` + 5 个 `.xaml`，合计约 4256 行                                                             |
| 运行时数据 | `src/ToniaDeskBox/bin/Debug/net8.0-windows/boxes/`（`config.json` + `config.json.bak`）           |

---

## 2. 启动验证记录

### 2.1 构建（关键日志原文）

```
$ dotnet build ToniaDeskBox.sln -c Debug --nologo
  正在确定要还原的项目…
  已还原 ...\src\ToniaDeskBox\ToniaDeskBox.csproj (用时 65 毫秒)。
  ToniaDeskBox -> ...\src\ToniaDeskBox\bin\Debug\net8.0-windows\ToniaDeskBox.dll
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:19.31
```

```
$ dotnet build ToniaDeskBox.sln -c Release --nologo
  已还原 ... (用时 1.83 秒)。
  ToniaDeskBox -> ...\bin\Release\net8.0-windows\win-x64\ToniaDeskBox.dll
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:05.79
```

```
$ dotnet publish src/ToniaDeskBox/ToniaDeskBox.csproj -c Release -o <temp>
  ToniaDeskBox -> ...\bin\Release\net8.0-windows\win-x64\ToniaDeskBox.dll
  ToniaDeskBox -> <temp>\
产物：ToniaDeskBox.exe 664178 B（≈649 KB，单文件）、ToniaDeskBox.pdb 59464 B
```

> 需求文档 §6 要求主程序 ≤ 5MB —— **达成**（framework-dependent 单文件 649 KB）。

### 2.2 运行验证（进程级 + 窗口级证据）

| 场景                       | PID   | 结果                         | 可见窗口                                                                        | 配置结果                                    |
| ------------------------ | ----- | -------------------------- | --------------------------------------------------------------------------- | --------------------------------------- |
| 首次启动（PowerShell 拉起）      | 36180 | 存活、`Responding=True`、22 线程 | `MainWindowHandle=0`（原因见下）                                                  | 生成 `config.json`                        |
| A. 干净首启（隔离副本，无 `boxes/`） | 49700 | 稳定存活 8s、无退出码               | `Box` 窗口 `VISIBLE=True`，`OWNER=65862`（= Progman 句柄），`RECT=(120,120,135,42)` | `boxes=1 names=['新建盒子'] xy=[(120,120)]` |
| B. 二次启动（仓库 bin，已有配置）     | 47480 | 稳定存活 8s、无退出码               | `Box` 窗口 `VISIBLE=True`，`OWNER=65862`，`RECT=(461,383,111,42)`               | 起始 `xy=(460.57,383.43)`                 |
| C. Acrylic 对照（隔离副本）      | —     | 两组均存活、窗口均可见                | 见 §2.4                                                                      | —                                       |
| D. 指标采样                  | —     | 存活、`Responding=True`       | —                                                                           | —                                       |

**关键判读**：`Process.MainWindowHandle=0` **不是启动失败**。`BoxWindow.PinToDesktop()` 通过 `SetWindowLongPtr(GWL_HWNDPARENT, Progman)` 把盒子设为 Progman 的 owned window，因此不进任务栏 / Alt+Tab / `MainWindowHandle`。Win32 `EnumWindows` 实测可见窗口 `HwndWrapper[ToniaDeskBox;;...]`，标题 `Box`，owner 正是 `65862`，同时存在 `WindowsForms10.Window.0.app`、`GDI+ Hook Window`、`.NET-BroadcastEventWindow` —— 说明 `NotifyIcon` 托盘与 WinForms 消息泵均已初始化。**桌面挂载逻辑按设计生效。**

> 方法论说明：跨工具调用观察 GUI 进程时，工具会话结束会连带回收子进程，导致"0 个窗口"的假象。最终采用「启动 → 采样 → 终止」在**同一进程内**完成的方式取证，上表结果均来自该方法。

### 2.3 运行指标

| 指标            | 实测                        | 需求目标（§6）   | 判定                       |
| ------------- | ------------------------- | ---------- | ------------------------ |
| WorkingSet    | 133.4 MB（启动初期 149.2 MB）   | —          | —                        |
| 私有提交          | 60.5 MB                   | 内存 < 50 MB | 按私有提交口径**略超**；按 WS 口径明显超 |
| 空闲 CPU（3s 窗口） | 0.094 s ≈ 3.12% 单核        | ≈ 0%       | **未达**，但采样区间含启动后图标预热     |
| 线程 / 句柄       | 23 / 1535                 | —          | WPF + WinForms 双栈，句柄偏高   |
| 虚拟地址空间        | ~2.36 TB（.NET 保留地址，非实际占用） | —          | 不作为内存口径                  |

### 2.4 Acrylic 专项实测（对照实验）

| config `Acrylic` | `EXSTYLE`    | `WS_EX_LAYERED` | DWM backdrop (38)             |
| ---------------- | ------------ | --------------- | ----------------------------- |
| `false`          | `0x00080000` | **True**        | `0`（未设置）                      |
| `true`           | `0x00080000` | **True**        | `3`（`DWMSBT_TRANSIENTWINDOW`） |

窗口扩展样式在两种模式下**完全相同**，即 `AllowsTransparency` 恒为 True。分层窗口由 WPF 自行合成，DWM system backdrop 属性虽被写入，但按 Win32 语义不会在其上渲染 Acrylic —— **FR-3.4 毛玻璃实质失效**（详见问题 B1）。

### 2.5 报错、警告与异常清单

| 来源                                                                 | 结果                                                                                            |
| ------------------------------------------------------------------ | --------------------------------------------------------------------------------------------- |
| Debug 构建                                                           | 0 警告 / 0 错误                                                                                   |
| Release 构建                                                         | 0 警告 / 0 错误                                                                                   |
| `dotnet publish`                                                   | 成功，无警告                                                                                        |
| 应用 `boxes/error.log`                                               | **不存在**（`DispatcherUnhandledException` 未触发）                                                   |
| Windows 事件日志（近 3 min，`.NET Runtime` / `Application Error` / `WER`） | **无记录**                                                                                       |
| 进程异常退出                                                             | 无（所有采样点 `alive=True`、`Responding=True`）                                                       |
| 配置编码                                                               | `config.json` 为无 BOM UTF-8，`json.loads` 校验通过；PowerShell 5.1 默认按 GBK 读取显示的"乱码"是**显示层问题，非数据损坏** |

**唯一需留意的观察项**：场景 B 在 t=6.5–8.0s 期间，盒子窗口位置连续变化 `(461,383) → (439,451) → (443,500) → (437,573) → (410,491)`，且 `config.json` 的 `X/Y` 被写成 `(410.29,491.43)`；同期系统出现 `CiceroUIWndFrame` / `MSCTFIME UI` 输入法窗口，符合真实桌面会话中存在人工交互的特征。场景 A（隔离副本、8s）位置始终稳定在 `(120,120)`。**无法在无交互条件下复现自动漂移，列为待确认项**，不据此判定为缺陷。

---

## 3. 目录结构与构建/运行表现速览

```
ToniaDeskBox.sln
docs/  Tonia-DeskBox-需求文档-v1.0.md · 参考/LightBox-桌面整理工具-参考文章.pdf
src/ToniaDeskBox/
  App.xaml(.cs)                    应用入口、单实例、托盘事件、盒子生命周期、3s heal 定时器
  Models/AppConfig.cs              配置模型（AppSettings / GlobalBoxSettings / BoxConfig）
  Services/  ConfigStore · AutoStartService · BackupService · DarkContextRenderer
             DwmHelper · FileMover · IconService · ShortcutService · SnapService
             TrayService · WallpaperColorService
  Windows/   BoxWindow · PanelWindow · BoxSettingsWindow · GlobalSettingsWindow
  app.ico · app.manifest · ToniaDeskBox.csproj
vibe_images/  deskbox-icon_*.png（设计稿，未被代码引用）
```

- 单工程解决方案，`src/` 为解决方案文件夹；无测试项目、无 CI、无 `global.json`、无 README。
- `csproj` 的 Release 条件块加了 `RuntimeIdentifier=win-x64`，导致 Release build 输出落到 `bin/Release/net8.0-windows/win-x64/`（与常规路径不同）；`PublishSingleFile` 仅在 `dotnet publish` 时生效。
- `.gitignore` 忽略 `bin/`、`obj/`、`boxes/`；运行时 `boxes/` 位于 `bin/` **内部**。
- 需求文档 §8.3 写"版本管理 SVN"，实际为 Git（3 次提交：`2d69b7a` M1 → `9085228` M2/M3 → `302622b` 外观打磨）；文档状态仍标"需求定义阶段（未开发）"。

---

## 4. 问题清单（按严重程度 / 影响范围分类）

### A. 阻塞级 —— 无

构建、发布、启动、窗口渲染、托盘初始化全部通过，不存在启动阻塞点。

---

### B. 高：功能失效或数据安全（建议优先处理）

**B1 · Acrylic 毛玻璃实质失效（FR-3.4）**

- **问题表现**：勾选"毛玻璃"后窗口仍是分层窗口，DWM Acrylic 不渲染；切换模式后外观与普通模式无区别。
- **涉及位置**：`Windows/BoxWindow.xaml:6`（`AllowsTransparency="True"` 硬编码）、`Windows/BoxWindow.xaml.cs:57-61`（`AllowsTransparency = !_acrylic` 写在 `InitializeComponent()` **之前**）、`Services/DwmHelper.cs:38-48`、`App.xaml.cs:224-232`（依赖 `AcrylicActive` 判断是否重建窗口）。
- **可能原因**：XAML 中声明的属性在 `InitializeComponent()` 内赋值，覆盖了构造函数里的预设值；`AllowsTransparency` 恒为 True → 窗口恒带 `WS_EX_LAYERED`。实测 Acrylic=true/false 两组 `EXSTYLE` 均为 `0x00080000`。
- **影响范围 / 对功能的影响**：P1 外观能力整体不可用，且"模式切换必须整窗重建"的注释与实现不符；不影响启动。
- **优先处理建议**：把 `AllowsTransparency = !_acrylic` 移到 `InitializeComponent()` **之后**（仍在构造函数内、HWND 创建之前即可），或从 XAML 移除该属性仅由代码设置；随后用视觉截图复核 Acrylic 是否真正呈现。这是最小改动，不触碰其它逻辑。

**B2 · 首次运行逻辑为死代码，示例盒子与自动整理不生效（FR-1 / §4 场景 1 / §12）**

- **问题表现**：全新环境启动只得到 1 个"新建盒子"，停在默认 `(120,120)`；预期是"系统工具 / 办公工具"两个示例盒并在屏幕底部居中排列。
- **涉及位置**：`Services/ConfigStore.cs:56-64`（无配置时 `cfg.Boxes.Add(new BoxConfig())` 并 `Save`）、`App.xaml.cs:53-73`（`firstRun = Config.Boxes.Count == 0`）。
- **可能原因**：`Load()` 已预置并落盘 1 个盒子，`OnStartup` 的 `firstRun` 永远为 `false` → 创建示例盒、`w0.ContentRendered += AutoSortBoxes()` 两段代码永不执行。
- **影响范围 / 对功能的影响**：首启体验与需求文档、验收清单不一致；实测隔离副本首启 `boxes=1 names=['新建盒子'] xy=[(120,120)]`。不影响启动，但影响首次可用性。
- **优先处理建议**：统一"是否首次运行"的判定口径（如由 `ConfigStore` 暴露 `IsNew`，或让 `Load()` 不再预置盒子、交给 `OnStartup` 决定），注意避免两边都创建造成重复盒子。

**B3 · `BoxDir` 直接用盒子名拼路径，存在越界读写风险（FR-6.2 数据安全）**

- **问题表现**：盒子名若为 `..` / `.` 或含路径分隔符，`Path.Combine(DataDir, name)` 会指向数据目录之外；收纳、"解散盒子"、"删除盒子"可能作用于错误目录。
- **涉及位置**：`Services/ConfigStore.cs:105`；`Windows/BoxSettingsWindow.xaml.cs:222`（仅校验 `Path.GetInvalidFileNameChars()`）；调用方 `Windows/BoxWindow.xaml.cs:627-663`、`Windows/PanelWindow.xaml.cs:291-317`。
- **可能原因**：`.` 与 `..` 不在 `GetInvalidFileNameChars()` 中；且 `config.json` 可手工编辑或由损坏恢复路径生成，绕过设置窗口校验。
- **影响范围 / 对功能的影响**：**数据风险最高项**，可能误移动/误删除桌面或上层目录文件，违背"删除盒子/退出程序不丢文件"。
- **优先处理建议**：对 `Name` 增加规范化校验（拒绝 `.`/`..`/分隔符/绝对路径/保留名）；在 `BoxDir` 内用 `Path.GetFullPath` 断言结果仍在 `DataDir` 之下，越界则回退到安全目录或抛明确异常。

**B4 · 运行时数据目录与构建输出混放（FR-6.2 / FR-6.4）**

- **问题表现**：开发期 `boxes/` 位于 `src/ToniaDeskBox/bin/Debug/net8.0-windows/` 内，`dotnet clean`、删除 `bin/`、`git clean -xdf` 或 IDE"清理解决方案"会连同收纳的桌面文件一起清除。
- **涉及位置**：`Services/ConfigStore.cs:27-39`（`AppContext.BaseDirectory` 优先）、`.gitignore:2`（`bin/`）。
- **可能原因**：需求按"绿色发布、exe 同级 boxes"设计，未区分开发构建目录与发布目录。
- **影响范围 / 对功能的影响**：开发/调试阶段存在**不可逆数据丢失**风险；发布形态下（exe 独立目录）不受影响。
- **优先处理建议**：开发期把数据目录改到固定独立位置（如 `%LOCALAPPDATA%\Tonia-DeskBox\boxes` 或仓库根 `boxes/`），或至少在 README 中明确警告；保留发布时的"exe 同级"策略以符合免安装迁移需求。

---

### C. 中：可靠性、持久化与需求偏差

**C1 · 配置读写静默吞异常，损坏时可能覆盖唯一备份（§6 数据可靠性）**

- **问题表现**：`config.json` 解析失败、写入失败均无日志、无提示；若主文件与 `bak` 同时不可读，`Load()` 会新建默认配置并 `Save`，而 `Save` 先把损坏的主文件复制成 `bak`，抹掉可人工恢复的副本。
- **涉及位置**：`ConfigStore.cs:77-88`（`TryRead` 的 `catch { return null; }`）、`90-103`（`Save` 的 `catch {}`）、`56-75`（`Load`）。
- **可能原因**：全部异常被吞，无法区分"文件不存在 / JSON 损坏 / 权限不足 / 磁盘满"。
- **影响范围 / 对功能的影响**：盒子定义、位置、排序可能在用户无感知下丢失；需求 §6 的"自动从 bak 恢复"只覆盖一半场景。
- **优先处理建议**：分类处理异常并写 `error.log`；损坏文件先另存 `config.corrupt-<ts>.json` 再回退 `bak`；`Save` 失败至少提示一次且不要覆盖 `bak`。

**C2 · 启动期异常会退化成"无界面僵尸进程"**

- **问题表现**：`OnStartup` 任一环节（托盘创建、配置加载、DWM 调用）抛异常时，仅弹 MessageBox 就被标记已处理，进程不退出；因 `ShutdownMode=OnExplicitShutdown` 且 Mutex 仍被持有，用户再次双击只会看到"已在运行"，必须手动结束进程。
- **涉及位置**：`App.xaml.cs:25-80`（无 try/catch）、`141-156`（`args.Handled = true`）、`App.xaml:4`（`OnExplicitShutdown`）、`App.xaml.cs:31`（Mutex）。
- **可能原因**：异常处理器不区分"启动阶段"与"运行阶段"。
- **影响范围 / 对功能的影响**：本次启动**未触发**；但属于启动失败时无法自恢复的隐患。
- **优先处理建议**：启动期异常单独捕获并 `Shutdown(1)`，日志写到固定路径；`Handled=true` 仅用于运行期非致命异常。

**C3 · 窗口位置与配置不同步，重启后可能回弹（FR-1.5）**

- **问题表现**：每 3 秒 `HealBoxes` 调用 `ClampIntoScreen(w)` 只修改 `Window.Left/Top`，`Box.X/Y` 仅在用户拖动结束或 `AutoSortBoxes` 时写回并保存；DPI 变化、任务栏调整、Explorer 重启导致窗口被拉回后，配置仍是旧值。
- **涉及位置**：`App.xaml.cs:98-114`、`Services/SnapService.cs:54-61`、`Windows/BoxWindow.xaml.cs:78-80`（`Loaded`/`ContentRendered` 各钳制一次）。
- **可能原因**：缺少"窗口实际位置 → 配置"的反向同步与节流保存。
- **影响范围 / 对功能的影响**：多屏/缩放变化场景下"重启还原位置"不准；另需复核 §2.5 观察到的位置漂移。
- **优先处理建议**：`HealBoxes` 检测到位置被修正时同步写回 `Box.X/Y` 并节流 `Save`；或把 heal 改为事件驱动。

**C4 · 内存 / 空闲 CPU 与非功能目标有差距（§6）**

- **问题表现**：实测 WorkingSet 133.4 MB、私有提交 60.5 MB、23 线程 / 1535 句柄；启动后 3–6s 区间 CPU ≈ 3.12% 单核。目标为内存 < 50 MB、空闲 CPU ≈ 0%。
- **涉及位置**：`App.xaml.cs:75-77`（3 秒 `DispatcherTimer` 常驻轮询）、`83-96`（启动后 1s 图标预热）、`csproj:6-7`（WPF + WinForms 双 UI 栈）、`Services/IconService.cs`（COM 图标提取）。
- **可能原因**：双 UI 栈基础开销 + 定时轮询 + 预热任务；采样区间包含预热，稳态值需更长采样确认。
- **影响范围 / 对功能的影响**：不影响启动与功能，但违反需求中的资源约束。
- **优先处理建议**：先做 60s 稳态采样明确口径（WS vs 私有提交）；再把 heal 由轮询改为 DPI/显示器/Explorer 重启事件驱动，图标预热改为按需。


**C5 · Acrylic 支持性判断与赋值不一致**

- **问题表现**：新建盒子时 `box.Acrylic = g.Acrylic && DwmHelper.IsAcrylicSupported()`，全局应用时却直接 `box.Acrylic = g.Acrylic`；`IsAcrylicSupported()` 仅判断 OS build ≥ 22621，未验证 DWM 实际可用性。
- **涉及位置**：`App.xaml.cs:188-196` 与 `211-233`、`Services/DwmHelper.cs:31-36`。
- **影响范围 / 对功能的影响**：低版本系统/禁用 DWM 时可能进入不一致状态；与 B1 叠加会放大外观异常。
- **优先处理建议**：统一走一个 `ResolveAcrylic()` 判定，并检查 `DwmSetWindowAttribute` 的 HRESULT。

**C6 · 多屏 DPI 单位混用（FR-7 / §7）**

- **问题表现**：`AutoSortBoxes` 用 `WF.Screen.PrimaryScreen.WorkingArea` 除以 `wins[0]` 的 DPI；`SnapService` 用 `Screen.FromHandle(hwnd)` + 该窗口 DPI。不同缩放率的多屏环境下，主屏工作区与从屏 DPI 不匹配。
- **涉及位置**：`App.xaml.cs:116-139`、`Services/SnapService.cs:17-26,63-78`、`Windows/PanelWindow.xaml.cs:123-151`。
- **影响范围 / 对功能的影响**：单机单缩放本次未复现；多屏 125%/175% 混用时盒子位置、面板定位可能偏移。
- **优先处理建议**：统一以目标窗口所在屏的 `Screen.FromHandle` + 该窗口 DPI 换算；多屏环境实测。

---

### D. 低：工程化、一致性与需求缺口

**D1 · 文档与仓库现状脱节 / 缺少上手说明**

- 表现：需求文档头部仍标"需求定义阶段（未开发）"，§8.3 写 SVN，实际已是 Git 且完成 M3；仓库无 README、无 .NET 8 Desktop Runtime 依赖与下载地址说明（§6 明确要求写明）、无 `global.json` 固定 SDK、无构建/发布命令说明。
- 涉及位置：`docs/Tonia-DeskBox-需求文档-v1.0.md:8,174`、仓库根目录、`ToniaDeskBox.csproj`。
- 影响：新人/换机时无法从仓库直接得知如何构建与运行；SDK 版本漂移风险。
- 建议：补 README（构建/发布/运行/数据目录/运行时依赖）、更新文档状态与版本管理描述、加 `global.json`。

**D2 · Release 输出路径与"单文件"语义易踩坑**

- 表现：`dotnet build -c Release` 输出为多文件且落在 `bin/Release/net8.0-windows/win-x64/`；`PublishSingleFile` 只在 `dotnet publish` 生效。
- 涉及位置：`ToniaDeskBox.csproj:30-34`。
- 影响：脚本/文档写错产物路径；误以为 build 即得到单 exe。
- 建议：README 写清两条命令；如需纯净发布可加 `<DebugType>none</DebugType>` 去掉 pdb。

**D3 · 需求清单中尚未实现的功能项**

- 表现：FR-4.3「Alt 临时禁用吸附」`SnapService` 无修饰键判断；FR-5.4 快捷键隐藏/呼出 —— `AppSettings.Hotkey` 字段存在但零实现；FR-5.3「数据目录位置」全局设置窗口无此项；FR-3.6 盒子图标主题样式无实现。
- 涉及位置：`Services/SnapService.cs`、`Models/AppConfig.cs:12`、`Windows/GlobalSettingsWindow.xaml.cs`、`Services/ConfigStore.cs:21-39`。
- 影响：均为 P1/P2 需求缺口，不影响已交付的 M1–M3 主体功能与启动。
- 建议：按 M4 里程碑排期，不纳入本次启动验证范围。

**D4 · 单实例与 COM/资源小隐患**

- 表现：`Mutex` 名 `ToniaDeskBox_SingleInstance` 未显式加 `Global\`/`Local\` 前缀，多会话/多用户下可起多个实例（`App.xaml.cs:31`）；`ShortcutService.ResolveLnk` 中 `lnk` COM 对象未释放，每次点击泄漏一个引用（`:109-118`）；`SHFILEINFOW` 结构缺 `szTypeName[80]`，兜底路径结构体不完整（`IconService.cs:154-162`）；`BackupService.ExportZip` 会把 `error.log` 一并打包，且导出目标若选在 dataDir 内，惰性枚举可能纳入 zip 自身（`BackupService.cs:12`）；`App.PrefetchIconsAsync` 用 `EnumerateFiles`（不含子目录），而角标/面板用 `EnumerateFileSystemEntries`（含），子目录图标不预热。
- 影响：均为低频/边缘场景，不影响启动。
- 建议：随对应模块迭代时一并处理，优先级最低。

**D5 · `Acrylic` 之外的一处功能口径偏差**

- 表现：`BoxWindow` 右键"删除盒子"在盒内有文件时直接 `IsEnabled=false`，提示"请先在面板中移出后再删除"；而需求 FR-1.1 要求"删除盒子时若盒内有文件，必须弹窗确认并提示文件去向（放回桌面 / 仅删除盒子保留文件）"。
- 涉及位置：`Windows/BoxWindow.xaml.cs:509-517`。
- 影响：与需求文档不一致，但不丢文件（属于保守实现）。
- 建议：确认产品口径后决定是否补齐弹窗选项。

---

## 5. 阻塞点与已排查范围

**阻塞点：无。** 项目在本机可直接 `dotnet build` → `dotnet run`/双击 exe 启动，无需任何最小调整即可运行。

**已排查范围**

- 本机 SDK / Desktop Runtime / OS build / DWM 支持性
- `.sln`、`.csproj`、`app.manifest`、`.gitignore`、需求文档 v1.0
- 17 个 `.cs` 与 5 个 `.xaml` 全量走查
- Debug / Release 构建 + `dotnet publish` 单文件发布
- 4 轮实际启动（含隔离干净副本首启、Acrylic true/false 对照），进程存活 / 响应性 / 退出码
- Win32 `EnumWindows` 窗口枚举（类名、标题、可见性、owner、矩形）、`WS_EX_LAYERED` / DWM backdrop 属性
- `config.json` / `config.json.bak` 内容与 UTF-8 编码、`error.log`、Windows 事件日志（.NET Runtime / Application Error / WER）
- 内存、CPU、线程、句柄采样
- 仓库改动核对：`git status --short` 为空（已跟踪文件零改动），仅 `bin/`、`obj/` 为被忽略的构建产物

**未覆盖（需要人工或特定环境）**

- 真实交互：拖入收纳、拖出放回桌面、右键菜单、设置弹窗、托盘菜单点击、图标启动
- 多显示器 / 混合 DPI 缩放、Explorer 重启、锁屏与休眠恢复
- 长时间稳态（>1 min）内存与 CPU
- Acrylic 的视觉截图确认
- Win10 兼容性（文档仅"尽力而为"）

---

## 6. 需要你补充确认的信息

> 2026-11-13 已收到用户对以下 6 项的答复，逐条处理结论与修复验证见 §8。

1. **首次运行的预期口径**：需求文档 §4/§12 与 `App.OnStartup` 都指向"2 个示例盒 + 底部居中"，但 `ConfigStore` 实现产出"1 个空盒"。请确认以哪个为准，再决定 B2 的修法。
2. **场景 B 的位置漂移**：t=6.5–8.0s 内盒子被移动 4 次且写入配置，同期出现输入法 UI 窗口。这是你当时在桌面上手动拖动了吗？若是人工操作则 C3 可降级；若确认无交互，需要继续排查 `HealBoxes` / 外部窗口管理器的干预。
3. **Acrylic 视觉表现**：本次只验证了窗口样式与 DWM 属性（已足以证明分层窗口未被消除），未截图。请确认在 Win11 上勾选毛玻璃后是否确实看不到亚克力效果，以便确认 B1 的修复是否必须与截图复核一起做。
4. **内存口径**：需求 "< 50MB" 指任务管理器 WorkingSet 还是私有提交？实测 133.4 MB / 60.5 MB，是否要按 C4 立项优化。
5. **数据目录位置**：开发期 `boxes/` 落在 `bin/` 内是否可接受，还是希望改为独立目录（B4）。
6. **是否需要我按 B → C → D 顺序给出修复补丁**：本次严格未改代码，等你确认后再动手。

---

## 7. 本次产生的文件（非源码改动）

| 路径                                                                   | 说明                                                         |
| -------------------------------------------------------------------- | ---------------------------------------------------------- |
| `src/ToniaDeskBox/bin/`、`obj/`                                       | Debug/Release 构建产物，被 `.gitignore` 忽略                       |
| `src/ToniaDeskBox/bin/Debug/net8.0-windows/boxes/config.json{,.bak}` | 应用启动时自动生成的运行时配置（仅 1 个默认盒子、无收纳文件）                           |
| `docs/Tonia-DeskBox-启动验证与现状分析-2026-11-13.md`                         | 本报告（新增，未提交）                                                |
| 系统临时目录                                                               | 隔离副本、`dotnet publish` 产物、诊断脚本与构建日志，`/tmp/tdbx-build-*.log` |

所有探测进程已结束，无 `ToniaDeskBox.exe` 残留进程。

---

## 8. 修复记录（2026-11-13 10:26 起，同日）

> 本节为验证报告的后续追加：用户确认五项待定信息后，按第 4 节问题清单实施修复并重新验证。
> 源码改动范围：9 个 `.cs` / `.xaml`；新增 `README.md`、`global.json`；修订需求文档与本报告。
> **未执行 commit / push。**

### 8.1 五项答复的落点

| # | 用户答复 | 处理决定 |
|---|---|---|
| 1 | 首次启动是一个空盒 | **B2 改为删除死代码**，不实现「系统工具 / 办公工具」两个示例盒。需求 §12 验收清单原文即「桌面出现 1 个盒子」，`OnStartup` 的 `firstRun` 分支既永不执行又与口径冲突 |
| 2 | 启动后确实手动拖动过 | §2.5 位置漂移**非缺陷**，不再追查；C3 收敛为「钳制结果需回写配置」的持久化同步问题 |
| 3 | Acrylic 确实看不到效果 | B1 实锤，按「移除分层窗口」方向修复并做窗口级实测 |
| 4 | 内存口径不理解 | 代为拍板：**不立项做内存优化**。稳态 CPU 已达标；58.7 MB 私有提交是 .NET 8 WPF + WinForms 双栈基线，「<50MB」属 Win32 原生量级，记为已知偏差写入需求 §6 与 README |
| 5 | 数据目录不理解 | 代为拍板：**发布形态保持 exe 同级 `boxes`（符合免安装 / 整体拷贝迁移），仅开发构建输出目录改到 `%LOCALAPPDATA%\Tonia-DeskBox\boxes-dev`**，并做一次性复制式迁移（原目录保留） |

### 8.2 已修复项

| 编号 | 改动 | 位置 |
|---|---|---|
| B1 | 从 XAML 移除硬编码 `AllowsTransparency="True"`；把 `AllowsTransparency = !_acrylic` 移到 `InitializeComponent()` 之后（HWND 创建前） | `BoxWindow.xaml`、`BoxWindow.xaml.cs` |
| B2 | 删除 `firstRun` 整段（2 个示例盒 + 首启自动排列），首启统一由 `ConfigStore.Load()` 产出 1 个空盒 | `App.xaml.cs` |
| B3 | 新增 `IsSafeSegment()` 与 `BoxDir()` 越界断言；`Load()` 规范化 Id / 名称、重名消歧并落盘；设置窗口名称校验改走统一判定 | `ConfigStore.cs`、`BoxSettingsWindow.xaml.cs` |
| B4 | 开发构建目录数据改到 `%LOCALAPPDATA%\Tonia-DeskBox\boxes-dev`；一次性复制式迁移；发布形态不变 | `ConfigStore.cs` |
| C1 | 损坏配置隔离为 `config.corrupt-<ts>.json`、bak 恢复后重建主文件、`Save` 仅在旧主文件合法时更新 bak、异常写 `error.log`、null 字段兜底 | `ConfigStore.cs` |
| C2 | 启动期异常释放 Mutex 并 `Shutdown(1)`；运行期异常仍 `Handled=true`；日志路径双重回退 | `App.xaml.cs` |
| C3 | `ClampIntoScreen` 返回「是否修正」；`HealBoxes` 修正时写回 `Box.X/Y` 并一次性保存；无变化时不再写窗口位置 | `SnapService.cs`、`App.xaml.cs` |
| C5 | 全局应用 Acrylic 同样走 `IsAcrylicSupported()`；系统不支持时不把 `Acrylic=true` 写回配置 | `App.xaml.cs`、`BoxSettingsWindow.xaml.cs` |
| D1 / D2 | 补 README（构建 / 发布 / 运行 / 数据目录 / 运行时依赖 / 已知偏差）、`global.json`；需求文档状态改为开发中、SVN→Git、明确首启与内存口径 | `README.md`、`global.json`、需求文档 |
| D4 | Mutex 显式 `Local\` 前缀；释放 `lnk` COM 对象；补全 `SHFILEINFOW.szTypeName`；备份排除 error.log / 损坏副本 / zip 自身；图标预热覆盖子目录 | 5 个服务类 + `App.xaml.cs` |

### 8.3 验证结果（修复后重跑）

**构建**：Debug / Release `dotnet build`、`dotnet publish` 全部 **0 警告 0 错误**；单文件
`ToniaDeskBox.exe` **667250 B（≈652 KB）**，仍满足 ≤5MB。中途出现过一次
`MC3000: XML 无效`（XAML 注释误放在开始标签的属性列表中间），已把注释移到 `<Window>` 标签之前并重新构建通过。

**Acrylic 对照（隔离发布副本，Win32 窗口级实测）**

| config `Acrylic` | `EXSTYLE` | `WS_EX_LAYERED` | DWM backdrop(38) | CLOAKED | PrintWindow 盒面主色 |
|---|---|---|---|---|---|
| `false` | `0x00080008` | **True** | 0 | 0（未隐藏） | `RGB(26,36,41)` 深色，4462 px |
| `true` | `0x00000008` | **False** | **3** | 0（未隐藏） | `RGB(127,133,135)` 中灰，4462 px |

> `0x8` 为验证时临时开启的 `WS_EX_TOPMOST`，与分层样式无关。两组
> `PrintWindow(PW_RENDERFULLCONTENT)` 均抓到完整内容（非空白、非全黑），文字像素仍在，
> 且盒面主色明显不同 —— DWM 已接管背景合成、窗口内容渲染正常，**B1 修复生效**。

**其它场景**

| 场景 | 结果 |
|---|---|
| 干净首启（无 boxes 目录） | `boxes=1 names=['新建盒子'] xy=(120,120)`，符合口径；error.log 仅记录「已重建默认配置」 |
| 主配置损坏 + 有效 bak | 生成 `config.corrupt-20261113-104050.json`；`config.json` 从 bak 恢复为 `FromBak`；`config.json.bak` **内容未被坏文件覆盖**；error.log 记录完整 JsonException |
| 配置含 `..` / `..\..\escape` / `CON` / 重名 | 规范化为 `盒子-evil0001/0002/0003`、`正常盒子`、`正常盒子-2`；无路径穿越、名称唯一、boxes 下无越界子目录、无异常日志 |
| 发布形态数据目录 | 隔离副本的 `boxes/config.json` 正常生成在 exe 同级，绿色迁移策略未变 |
| 开发期数据目录 | `boxes-dev` 由无到有，`config.json`(496 B) + `config.json.bak`(497 B) 完整复制，用户手动拖动后的 `X=410.2857, Y=491.4286` 保留；旧 `bin/.../boxes` 仍在 |
| 进程 / 日志 | 全部场景进程存活、`Responding=True`、无崩溃事件；正常场景无 error.log；探测进程全部终止，残留 0 |

**运行指标（30 秒稳态，bin/Debug 启动）**

| 指标 | 修复前（启动后 3s 区间） | 修复后（8s 后 30s 稳态） |
|---|---|---|
| CPU | 0.094 s / 3 s ≈ 3.12% 单核 | **0.109 s / 30 s ≈ 0.365% 单核**（达标） |
| WorkingSet | 133.4 MB | 125.7 MB |
| 私有提交 | 60.5 MB | 58.7 MB |
| 线程 / 句柄 | 23 / 1535 | 16 / 1491 |

> 之前 3.12% 是启动图标预热区间的采样，不是稳态；稳态接近 0%。
> 内存仍高于 50MB，属框架基线，见 8.1 第 4 条决策。

**方法论补充**：本环境屏幕截图 / `GetDC` 抓屏拿不到桌面合成画面（盒子是 Progman owned window，
位于普通窗口之下，且工具会话前台窗口会遮挡），三种配置抓到的位图完全一致，**不能用作视觉判据**。
最终改用 `PrintWindow(PW_RENDERFULLCONTENT)` 抓窗口自身 + Win32 扩展样式 / DWM 属性读回作为证据。

### 8.4 明确未修项及理由

| 项 | 理由 |
|---|---|
| D3：FR-4.3 / 5.3 / 5.4 / 3.6 | 属于 M4 需求缺口，是「新增功能」而非「修复现有问题」；本次不做新功能，避免过度设计 |
| D5：删除非空盒子的文件去向弹窗 | 需要产品口径决策；当前实现禁用删除，保守且不丢文件 |
| C6：多屏混合 DPI 单位混用 | 改动涉及吸附 / 自动排列算法，本机无多屏混合缩放环境**无法验证**，改了不负责；已在 README 标注为已知限制 |
| C4 内存硬压到 <50MB | 需换 Win32 原生栈或 NativeAOT，属架构级改造；记为已知偏差 |

### 8.5 仍需你确认

1. 请在本机勾选「毛玻璃」肉眼确认：窗口级证据已坐实 DWM backdrop 接管，但亚克力模糊的是桌面背景，纯色壁纸区域只会呈现带色调的半透明，视觉强度需你判断。
2. 开发期数据目录已迁到 `%LOCALAPPDATA%\Tonia-DeskBox\boxes-dev`，旧 `bin/.../boxes` 保留未删；确认后可自行清理旧目录。
3. 以上改动**均未提交**，等你看过 diff 后再决定是否 commit / push。

---

## 9. 统一外观样式 + 舍弃毛玻璃（2026-11-13 15:00 起）

### 9.1 需求与结论

| 你的要求 | 落点 | 结论 |
|---|---|---|
| 统一设置窗口的样式风格为同一套样式 | 新增 `Services/AppearanceTheme.cs`，`Theme.*` 成为唯一真源；两个设置窗口改为与盒子/面板相同的 `ChromeBorder + ShadowCaster` 结构、同一套笔刷与分类色 | 四处窗口 + 右键菜单共用一套外观 |
| 毛玻璃没有任何效果，改为透明底色、舍弃毛玻璃 | 删除 `DwmHelper`、`BoxConfig.Acrylic` / `GlobalBoxSettings.Acrylic`、设置项与窗口重建逻辑；`AllowsTransparency=True` 恒开，窗口 `Opacity=1`，透明度改由底色笔刷 alpha 表达 | 不再有「假亚克力」，底色真正透出壁纸 |
| 底色切白色后字体仍是白色，整个面板/盒子全白 | 按底色 sRGB 相对亮度自动切换墨色（阈值 0.62）；XAML 中所有硬编码 `#xxFFFFFF` 前景改为 `DynamicResource Theme.*` | 白底自动深字，深底自动白字 |

### 9.2 关键实现细节

1. **单一真源**：`AppearanceTheme.Update(backColor, opacity, name, minAlpha)` 一次算出
   `Surface / SurfaceHover / SurfaceDrop / Border / Text / TextSecondary / TextMuted / Icon /
   Fill / FillHover / FillStrong / Divider / Accent / Action / AccentBadge / Ring …`。
2. **不能放进 `MergedDictionaries`**：合并字典会被 WPF Seal，连带 Freezable 笔刷一起冻结，
   后续 `Update()` 抛 `InvalidOperationException`。改为直接写入目标**根**资源字典
   （`Window.Resources` / `ContextMenu.Resources` / `Application.Resources`），每次替换为新笔刷对象。
3. **App.xaml 共享样式的 `DynamicResource` 解析到窗口级主题**（实测 `STYLE_DYNAMIC_RESOLVES_TO_WINDOW=True`），
   因此 `ModernTextBox` / `ModernCheck` / `AccentButton` / `GhostButton` / `ContextMenu` / `MenuItem` /
   `ScrollBar` 全部随所在窗口换肤，不需要为每个窗口复制样式。
4. **两级分类色**：`Theme.Accent`（柔和）用于色条/色点/框选/滑杆；`Theme.Action`（压暗 32%）
   用于按钮、勾选框等承载白字的元素，避免浅色分类色 + 白字对比不足。
5. **面板投影层**：`ShadowCaster` 背景改为低 alpha 的 `Theme.ShadowFill`（约底色 alpha 的 55%），
   既保留 `DropShadowEffect` 轮廓，又不会变成不透明背板。设置窗口同样拆成 `ShadowCaster + ChromeBorder`。
6. **面板跟随**：`BoxWindow.ApplySettings()` 增加 `_panel?.RefreshTheme()`，已打开的面板即时换肤。
7. **托盘右键菜单也统一**：`TrayService` 弃用 WinForms `ContextMenuStrip` 与 `DarkContextRenderer`，
   改为 WPF `ContextMenu` + `AppearanceTheme`，跟随全局默认外观（无全局设置时取第一个盒子）。
   托盘菜单没有父窗口，用 1×1 全透明宿主窗口作 `PlacementTarget`；**先激活宿主再 `IsOpen=true`**，
   不能在弹出后再 `SetForegroundWindow`/`Focus()`（会让 Popup 失去捕获、菜单立刻消失）。
   菜单收起时把前台还给原窗口。
8. **样式字典独立**：共享控件样式从 `App.xaml` 抽到 `Themes/SharedStyles.xaml`，`App.xaml` 只做合并。
   这同时解决了验证宿主的两个坑：不能 `new ToniaDeskBox.App()`（会触发真实 Mutex/托盘/配置启动），
   而子类化又会让 `InitializeComponent()` 找不到 `app.xaml` 资源。现在宿主直接
   `Application.LoadComponent("/ToniaDeskBox;component/Themes/SharedStyles.xaml")` 即可。
   注意字典在子目录下，`AppIcon` 必须用程序集绝对 pack URI，否则相对路径会解析成 `themes/assets/...`。
9. **面板关闭幂等**：真机日志出现 `InvalidOperationException: 在窗口关闭期间…Close`（失焦与 ×/Esc 撞在同一帧）。
   新增 `_closing` 标志与 `ClosePanel()`，所有关闭入口统一走它；这是本轮唯一一处实际崩溃修复。

### 9.3 验证数据（临时宿主 `tdbx_theme_verify`，白底 `#FFFFFF` / opacity 0.85）

```
BOX      WindowOpacity=1.00
         Surface=gradient[#D9FFFFFF -> #D9F7F7F7]   ← #D9 = 217 ≈ 0.85×255，半透明
         Border =gradient[#4018202C -> #1418202C]   ← 浅底用深墨描边
         NameText=#FF18202C  CountText=#FFFFFFFF  Hint=#9618202C
PANEL    Surface=gradient[#D9FFFFFF -> #D9F7F7F7]  Title=#FF18202C  Badge=#F04E816B
BOXSET   Surface=gradient[#E6FFFFFF -> #E6F7F7F7]  NameBox.Foreground=#FF18202C
GLOBALSET OpacityBox.Foreground=#FF18202C
APP      DefaultText=#F7FFFFFF（应用级默认仍是深色）
STYLE_DYNAMIC_RESOLVES_TO_WINDOW=True
切回深底：BOX NameText=#F7FFFFFF / PANEL Title=#F7FFFFFF（同一批资源键原地换色）
CONFIG_HAS_ACRYLIC=False
ERROR_LOG=(none)
```

托盘菜单（同一宿主内反射调用 `TrayService.ShowMenu()`）：
```
TRAY IsOpen=True Items=12 Font=13
TRAY Text=#FF18202C Surface=#F5FFFFFF W=138 H=300
popup(TrayMenu) rect=242x524 print=True
TRAY_AFTER_CLICK: menu=False isOpen=      ← 模拟点击别处后确实收起
```
白 / 深两种主题均验证通过；深色下文字为白色、悬停项使用 `Theme.FillHover`。

视觉证据（PrintWindow + DWM 扩展边框，2×/3× 放大合成到绿色壁纸上）：
`%TEMP%\tdbx_appearance_out_white\`、`..._out_dark\`、`tdbx_real_out\screen_Box_3x.png`。
真实运行实例（配置为 `#FFFFFF` 的「娱乐」盒子）已确认：盒子名深色、计数徽标白字、
「共 1 项」深灰、底色从屏幕像素可见壁纸绿色透出；最终版本 PID 23592 启动后 `error.log` 无新增条目。

构建：Debug / Release / `dotnet publish` 均 **0 警告 0 错误**；单文件产物约 0.99 MB（≤5 MB）。

> 说明：`boxes-dev\error.log` 中 15:02 / 15:03 两条异常来自中间版本（分类色笔刷被冻结、面板重复 Close），
> 对应代码已修，最终版本启动后未再出现。旧日志未删除，留作排查记录。

### 9.4 仍需你肉眼确认

1. 悬停提亮、拖入高亮、右键菜单在浅色盒子上的观感（自动化探针无法触发桌面 owned window 的 hover）。
2. 托盘图标真实右键：菜单已用宿主验证弹出 / 点别处收起，但「鼠标真正点托盘图标」这一入口需要你试一次；
   Esc 收起也未在真实托盘场景验证。
3. `ToolTip` 在浅色盒子上仍是应用级深色皮肤（有意保留，见 README 已知偏差）。
4. 本轮改动**仍未提交**，等你看过 diff 后再决定 commit / push。

## 10. 盒子存储位置可配置 + 自动迁移（2026-11-13 16:00 起）

### 10.1 要解决的问题

盒子收纳的是**真实文件**（拖入＝把桌面文件移动进来）。用户实测一个「编程工具」盒子装到
887.8 MB（38 个快捷方式 + 一个 889 MB 的解包目录），而开发态数据根默认在
`%LOCALAPPDATA%\Tonia-DeskBox\boxes-dev`（C 盘），长期使用会撑爆系统盘。

### 10.2 先拆开「配置」和「数据」

原实现 `ConfigStore.ConfigPath => DataDir/config.json`：配置和数据根绑在一起。
数据根一旦可改，配置就跟着漂，下次启动会找不到自己。所以：

| 内容 | 位置 | 可改 |
|---|---|---|
| config / bak / error.log | `ConfigDir`（开发 `%LOCALAPPDATA%\Tonia-DeskBox`，绿色版 exe 同级） | 否 |
| 盒子文件 | `DataRoot`，`AppSettings.DataRoot` 记录 | 是 |

- 新增 `AppSettings.DataRoot`（null = 默认位置）、`ConfigStore.ConfigDir` / `DefaultDataRoot` /
  `IsUsingDefaultRoot` / `TrySetDataRoot()`。
- 数据根不可用（外置盘拔掉）→ `UsedFallbackRoot` 置位，本次回退默认并写日志，**配置原值保留**，
  盘插回来自动恢复。
- 旧版兼容：`MigrateLegacyConfig()` 在固定目录无配置时，把 `boxes-dev\config.json` 复制过来
  （只复制不删除），并把数据根指回原目录。
- `TrayService` 的「打开数据目录」改成 `Func<string>` 实时取 `DataRoot`；导出备份额外把
  `config.json` 以 `_config/config.json` 打进 zip（否则换机恢复只有文件没有盒子结构）。

### 10.3 迁移：复制 → 校验 → 切配置 → 删源

`Services/StorageMigration.cs`，四步顺序刻意如此，**任何一步失败源都完整**：

1. `Scan` 统计文件数与字节（跳过根目录顶层的 `config*` 与 `*.log`）；
2. 目标盘剩余空间 ≥ 需迁移量 + 16 MB，否则直接拒绝；
3. 逐文件 1 MB 分块复制（可取消），同名同大小跳过 → 中断后重跑可续传；
4. 重新 `Scan` 目标，文件数与字节数都要 ≥ 源，才算成功；
5. 调用方 `TrySetDataRoot` + `Save` 成功之后，才 `PurgeSource` 删原位置。

`ValidateTarget` 拒绝：当前根本身、当前根的子目录、当前根的上级、磁盘根（`D:/`）、
配置目录及其上级。目标不可写同样拒绝。

UI：全局盒子设置 → 「存储」行（路径 + 更改… + 默认），下方实时显示「N 个盒子 · 占用 X · 该盘可用 Y」。
迁移期间盒子先隐藏、遮罩显示进度（百分比 + 文件数 + 字节）、可取消；`App.StorageBusy`
让 `HealBoxes` 让路，避免自愈逻辑在迁移中重建窗口。迁移前把设置窗口的 `Owner` 置空，
否则重建盒子会连带关掉设置窗口。

### 10.4 验证

- 迁移逻辑测试（`tdbx_migration_test`，28 项全过）：多级目录 / 中文名 / 20 MB 大文件、
  扫描跳过 config、字节与文件数一致、进度到 100%、默认不删源、清理后源只剩 config、
  重复执行、路径校验 6 例、取消后源完整、空源、体积格式化。
- 真机迁移：`boxes-dev` → `D:/ToniaData/boxes`，**704 个文件 / 887.8 MB**，
  校验通过、配置切换、原位置清空；C 盘该目录从 890 MB 降到 64 KB（仅配置与日志）。
- 重启后 2 个盒子（编程工具 40 项、娱乐 1 项）正常显示，`error.log` 无新增。
- Debug / Release / publish 均 0 警告 0 错误，单文件 1,010,802 B。

### 10.5 遗留

- 盒子窗口坐标每次启动会偏移约 32 DIP（配置值与渲染位置不是严格 `×1.75`），表现为盒子逐渐
  贴到任务栏底边。与本次改动无关，是既有的位置回写问题，尚未定位到具体写回点。
- 未验证多屏切换后 `DataRoot` 所在盘符变化的表现。

## 11. 深浅色主题切换（2026-11-13 16:20 起）

### 11.1 需求与关键约束

要求：新增浅色/深色主题切换；开启后不能再自己挑底色；要支持跟随系统。
追加要求：浅/深色的底色**取 Win11 任务栏的实际表面色**。

难点在于「不能挑底色」和「现有每盒独立 BackColor」的兼容：如果切换主题时直接改写
`BoxConfig.BackColor`，切回自定义就找不回原配色了。所以主题只接管**渲染**，不动数据。

### 11.2 实现

- `Models/AppConfig.cs`：`enum ThemeMode { Custom, Light, Dark, System }`，
  `AppSettings.ThemeMode` 用 `JsonStringEnumConverter` 写成字符串（手工编辑可读）；
  旧配置没有该字段 → 默认 `Custom`，数字写法也能读。
- `Services/ThemeService.cs`（新）：唯一决策点。
  - `ResolveColor(customHex)`：Custom 原样返回、其余返回主题色
  - `IsColorLocked`：非 Custom 时锁色板
  - `LightForIcons`：显式浅/深优先，其余跟随系统
  - 订阅**一次** `SystemEvents.UserPreferenceChanged`（原先 `AppIconService` 自己订阅），
    变化后广播 `Changed`（已切回 UI 线程）
- `AppearanceTheme.Update(BoxConfig)` 内部走 `ResolveColor` —— 盒子、面板、右键菜单、托盘菜单、
  应用级默认笔刷全部自动跟随，不用逐窗口改。
- `AppIconService` 去掉自己的系统监听，改为 `SetLight(bool)` 由 ThemeService 驱动。
- `App`：`Config` 加载后再 `ThemeService.Initialize`（顺序重要，主题值来自配置）；
  `Changed` 时保存模式、重刷应用级笔刷与图标、遍历所有盒子 `ApplySettings()`。
- UI：全局设置顶部「主题」四个分段芯片（`ThemeChip` 样式：选中态压暗分类色 + 白字）；
  非自定义时色板与「取壁纸近似色」灰掉（`SwatchButton` 补了 `IsEnabled` 触发器，opacity 0.28），
  并显示「切回自定义即可恢复」。单盒设置同样禁用并提示。
  芯片是**点一下就地生效**便于看真实效果，点「取消」回滚到打开时的模式。

### 11.3 颜色取值

| 模式 | 底色 | 说明 |
|---|---|---|
| 浅色 | `#F3F3F3` | Win11 浅色任务栏表面色 |
| 深色 | `#202020` | Win11 深色任务栏表面色 |
| 跟随系统 | 按 `AppsUseLightTheme` 取上两者 | 实时响应 |
| 自定义 | 盒子自身 `BackColor` | 保持原行为 |

同时把底色微渐变从「深色 +18/−10、浅色 +8/−8」收窄到「深色 +10/−6、浅色 +6/−6」，
否则盒子顶部会比任务栏明显亮、看起来"浮"着。

### 11.4 验证

- 逻辑测试（`tdbx_theme_test`，25 项全过）：四种模式解析、空值回退、色板锁定矩阵、
  切回 Custom 恢复原色、图标版本映射、`SetMode` 幂等与广播次数、序列化（字符串/数字/旧配置缺字段）、
  前景对比色随主题切换（浅色主题下黑底盒子判为浅底等）。
- 端到端「跟随系统」：应用以 System 模式运行，改注册表 `AppsUseLightTheme` 并广播
  `WM_SETTINGCHANGE(ImmersiveColorSet)`：
  - 深色 → 浅色：盒子主色 `(24,35,41)` → `(236,236,236)`，平均亮度 71.7 → 193.6
  - 浅色 → 深色：恢复 71.7
  - 实测后已把系统主题恢复为原值（`AppsUseLightTheme=0`）
- 任务栏对比：同框截取盒子与任务栏，主体颜色一致；盒子数据未被改写（`BackColor` 仍是 `#F2F2F2`）。
- Debug / Release / publish 均 0 警告 0 错误，单文件 1,015,410 B。
- 截图：`theme-taskbar-dark.png`、`theme-taskbar-light.png`、`settings-theme-light.png`、
  `settings-theme-dark.png`、`taskbar-vs-box.png`（均在 `%TEMP%\tdbx_restore_out\`）。

### 11.5 遗留

- 任务栏若被用户改成强调色或开启"透明效果"，盒子仍是 `#F3F3F3`/`#202020`，不会跟着变
  （当前按 Win11 默认任务栏色取值，未读 `ColorPrevalence`/accent）。
- 主题切换不重建窗口，只重算笔刷；若后续给盒子加了位图类装饰，需要补一次刷新。

## 12. 面板打开闪现修复 + 样式优化（2026-11-16）

### 12.1 现象

用户反馈面板「打开时会闪现一下才正常打开」，并附截图：面板左侧出现一块**位置偏移、尺寸偏小的
白色矩形**，与面板本体不重合。

### 12.2 定位：逐帧抓取 + HWND 采样

写了一个专用宿主（`tdbx_panel_frames`），在预定时刻调用 `BoxWindow.TogglePanel()`，
再用两种手段取证：

1. **Python 逐帧截图**（`tdbx_frames_capture.py`）：宿主写 `fire.txt` 标记开火时刻，
   Python 轮询到该文件后立刻以 ~74ms 间隔连拍 26 帧。
   结果：**帧 #09 面板出现在左上角、与盒子左对齐，帧 #10 才跳到正确位置** —— 位置确实跳变过。
2. **C# 每 4ms 采样面板 HWND 的 DWM 扩展边框**：只记录变化。

### 12.3 根因

`PanelWindow` 原实现把「设为透明 + 定位」都写在 `ContentRendered` 里：

```csharp
ContentRendered += (_, _) => { UpdatePlacement(); PlayOpenAnimation(); };
// PlayOpenAnimation() 内部才 OuterGrid.Opacity = 0
```

而 **`ContentRendered` 是在首帧已经画到屏幕上之后才触发的**。于是：

- 第 1 帧：窗口以 `WindowStartupLocation` 的默认位置 + `OuterGrid.Opacity = 1` 被 DWM 合成 → 用户看到一次完整闪现；
- 第 2 帧起：才被设为透明、移到正确位置、开始淡入。

白色的来源：分层窗口不显式设置 `CompositionTarget.BackgroundColor` 时，
DWM 首帧会先铺一层默认背景（白），叠加尚未渲染的内容，就是截图里那块白色矩形。

### 12.4 修复

```csharp
// 构造函数末尾（Show 之前）
OuterGrid.Opacity = 0;
OuterGrid.RenderTransform = new TranslateTransform(12, -6);
WindowStartupLocation = WindowStartupLocation.Manual;

SourceInitialized += (_, _) =>
{
    // HWND 已建、窗口未显示：此刻定位才真正「一次到位」
    var hwnd = new WindowInteropHelper(this).Handle;
    if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } t })
        t.BackgroundColor = Colors.Transparent;   // 消除首帧白底
    UpdatePlacement();
};

ContentRendered += (_, _) =>
{
    UpdatePlacement();   // SizeToContent 尺寸定稿后再校一次
    PlayOpenAnimation(); // 只负责播放，不再负责「变透明」
};
```

`PlayOpenAnimation()` 复用构造里预置的 `TranslateTransform`，不再重复 `Opacity = 0`。

### 12.5 验证

采样输出（修复后）：

```
=== 触发 TogglePanel ===
  t= 156ms  OuterGrid.Opacity=0.01   ← 窗口可见的第一刻就是透明的
  t= 161ms  rect=958,455 809x987     ← 位置一次到位，全程无变化
  t= 175ms  OuterGrid.Opacity=0.30
  t= 207ms  OuterGrid.Opacity=0.78
  t= 235ms  OuterGrid.Opacity=0.93
  t= 257ms  OuterGrid.Opacity=0.98
  t= 279ms  OuterGrid.Opacity=1.00
```

修复前同口径采样中，窗口首次出现的位置与最终位置不同（对应逐帧截图 #09→#10 的跳变）。

### 12.6 样式优化

| 问题 | 处理 |
|---|---|
| 文件名带 `.lnk`，被挤成 `CodeBuddy.l` + `nk` | 新增 `Services/DisplayNames.cs`，去掉 `.lnk`；撞名时该组退回全名。`Box.Order` 仍存真实文件名（`Path.GetFileName(FilePath)`） |
| 格子是「卡片 + 图标底板」两层圆角嵌套，40 项显得发闷 | 去掉图标底板，图标 42×42 直接放在 88×94 卡片上，hover 只改卡片底色 |
| 格子间距 3，密集 | 间距改 4；虚线拖入位 `Margin.Left` 同步 97 → 96 |
| 顶部强调条左右各外扩 6px，圆角调大时会戳出圆角 | 改为 `Margin="0,-8,0,0"`，只在内容区内铺开 |
| 滚动条被认为「粗糙」 | 复查后确认 `SharedStyles.xaml` **已有** 12px 细滚动条样式（无箭头、hover 变强调色），无需改动。本轮一度误判为缺失并重复添加，导致 `XamlParseException: Item has already been added`，已回退 |

### 12.7 验证与产物

- 逻辑：Debug / Release / `dotnet publish` 均 **0 警告 0 错误**，单文件 1,016,946 B。
- 视觉：40 项 / 1 项两种场景、折叠盒子均抓图核对；`.txt` 后缀保留、`.lnk` 去掉、断词消失、
  格子 4 列对齐、虚线空位与格子同高。
- 真实运行：重启后 `error.log` 无新增（仍停在 11-13 16:11），配置未被改写。

### 12.8 遗留

- 需要用户肉眼确认：连续快速开合面板时是否还有感知得到的闪烁（自动化只能证明首帧透明 + 位置不跳）。
- `Logi Options +`、`Codex++ 管理工具` 这类超长名字仍会自然折行，属文字长度问题，未做缩字号处理。

## 13. 面板搜索 + 排序方式扩充 + 无底色外观（2026-11-16 下午）

### 13.1 需求

1. 已打开的面板里新增搜索按钮，按关键字筛选图标；
2. 排序方式增加「按放入时间」，保留原有「按名称排序」；
3. 去掉每个图标底部的背景色卡片，让图标更简洁统一；
4. 现有交互与功能不受影响。

### 13.2 搜索：把「全量」和「显示」拆成两层

原实现只有一个 `_items`，直接绑给 `ItemsControl`，框选、拖拽排序、右键都靠它的索引。
若用 `ICollectionView` 过滤，`ItemContainerGenerator.ContainerFromIndex(i)` 的索引会错位，
所有基于索引的命中测试都会失效。

所以改成：

| 字段 | 含义 |
|---|---|
| `_allItems` | 全量，按 `Box.Order` 排列 |
| `_items` | `_allItems` 经关键字过滤后的**显示集合** |

`RefreshItems()` 只负责「扫目录 → 构建 `_allItems` → 定宽度基线 → `BindVisible()`」；
`BindVisible()` 才是唯一改动 `Items.ItemsSource` 的地方。

这样框选、拖拽、右键、打开天然只作用于可见项，不需要改动它们的实现。

配套处理：

- 计数胶囊在搜索时显示 `命中/总数`（如 `3/40`），一眼看出被筛掉多少；
- 空结果复用一个 `EmptyCard`，文案切成「没有匹配「xxx」的图标」，不再显示"拖进来收纳"；
- 虚线拖入位（只有 1 项时补位）在搜索状态下不显示，否则会挤乱网格；
- `BandHost.Width` **只在重新扫描目录时计算**。如果每次按键都按命中数重算，
  面板宽度会跟着变、位置还要重算，每敲一个字界面就晃一下；
- 筛选期间禁用拖拽排序（`StartSort` 与 `CommitSort` 双重守卫）：
  此时 `_items` 只是子集，用它重建全量 `Box.Order` 会丢掉未匹配的项。

快捷键：`Ctrl+F` 打开搜索；`Esc` 逐层退出（退搜索 → 关菜单 → 清选中 → 收面板）；
焦点在 `TextBox` 时除 `Esc` 外的快捷键一律让给输入框（否则 `Ctrl+A` 会去全选图标而不是文字）。

展开/收起搜索栏会改变窗口高度（`SizeToContent`），所以在 `DispatcherPriority.Loaded`
再跑一次 `UpdatePlacement()`，否则面板会按旧高度定位再跳一下。

### 13.3 排序：两种方式都写回 Box.Order

`⋯` 菜单由原来的一个「按名称排序」扩成两个可勾选项：

- **按名称排序**：按去掉 `.lnk` 后的显示名，`StringComparer.CurrentCulture`；
- **按放入时间排序**：新放入的在前。`Box.Order` 本身就是放入顺序（拖入时追加到末尾），
  所以「越靠后越新」；不在 `Order` 里的（直接拷进盒子目录的文件）视为最新。

两者都重排并 `_store.Save()`，与原有「按名称排序」的持久化行为一致。

**验证数据**（宿主灌入真实 `Order` 后调用 `ApplySort`）：

```
输入（收纳顺序，旧→新）
  前 3: 百度网盘.lnk, 钉钉.lnk, 抖音.lnk
  末 3: win-ia32-unpacked, WorkBuddy.lnk, YueToniaBatteryDetector.lnk

按放入时间排序后
  前 3: YueToniaBatteryDetector.lnk, WorkBuddy.lnk, win-ia32-unpacked   ← 原末 3 倒序
  末 3: 抖音.lnk, 钉钉.lnk, 百度网盘.lnk                                 ← 原前 3 倒序
```

按名称排序仍正常（中文按拼音在前、英文按字母序在后）。

> 一度踩坑：宿主自己 `new BoxConfig` 没带 `Order`，导致「按放入时间」退化成按名称排序，
> 看起来"通过"其实什么都没测到。改成从真实 `config.json` 只读灌入 `Order` 后才拿到有效结论。

### 13.4 视觉：去掉底部背景卡片

`SelBg` 的 `Background` 由 `Theme.Fill` 改为 `Transparent`，40 个图标直接浮在面板上、靠间距分组。
悬停才浮现 `Theme.FillHover`，选中是 `Theme.FillStrong` + 分类色描边 + 勾号。

标题栏同时新增放大镜按钮（`⋯` 左侧），搜索栏是一条 `Theme.Fill` 底色的圆角条，
内含前置放大镜、输入框（无边框、透明底）、尾部清除按钮。

### 13.5 验证

- Debug / Release / `dotnet publish` 均 **0 警告 0 错误**，单文件 1,021,042 B。
- 三个场景抓图核对（`%TEMP%\tdbx_v2_out\`）：
  - `plain` —— 无底色外观、放大镜按钮在位；
  - `search` —— 输入 `qoder` 后剩 3 项（Qoder CN IDE / Qoder CN / Qoder），胶囊显示 `3/40`，
    面板高度由 987 自动收缩到 460；
  - `sort-added` —— 排序后顺序符合「新放入在前」。
- 排序逻辑两组对照（真实 `Order` 灌入）：`added` 与 `name` 均写回正确顺序。
- 真实运行：重启后 `error.log` 无新增（仍停在 11-13 16:11），两个盒子 40 / 1 项完好。

### 13.6 遗留

- 搜索无结果时仍复用空盒的虚线框（文字已改），如果想更轻可以换成纯文字提示。
- 排序方式的「当前选中」只在本次面板生命周期内记忆（菜单勾选），
  重新打开面板会重置为不勾选；实际顺序已经通过 `Box.Order` 持久化，不受影响。
- 搜索是逐次重建 `ItemsSource`（40 项量级无感知）；若将来盒子容量上千，
  需要换成增量过滤或虚拟化。
