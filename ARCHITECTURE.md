

# HDs2-SDK 上位机软件设计与架构说明

- 文档版本：v1.0（对应代码版本 v0.7.x 联调分支）
- 适用对象：上位机维护者、固件联调人员
- 技术栈：.NET 10（net10.0-windows）/ WPF / C#，串口通信 `System.IO.Ports`，设备枚举 `System.Management`

---

## 1. 概述

HDs2-SDK 是骨动力主机的 Windows 上位机控制面板，运行形态为单窗口常驻置顶界面。核心职责：

1. **设备发现**：自动枚举 USB 转串口设备，通过协议探测识别"下位机航插"，无需手工指定 COM 口。
2. **协议通信**：与下位机（`SDK/Mic_Sdk.c`，RS232 115200 8N1）进行指令下发 / 心跳接收 / 寄存器轮询。
3. **状态呈现**：把下位机上报的实测转速、报警、脚踏状态映射为数码管、状态灯、转子动画、按钮可用性。
4. **参数设置**：设置转速（10000~70000，按档位步进校验）并回读确认。
5. **异常提示**：上下位机未通讯显示 `E100`，下位机报警显示 `E+报警码`。

设计原则：**下位机是唯一权威数据源**，上位机不主动启停电机（启停由实体脚踏控制），本地只做显示、校验、兜底。

### 文档结构

| 部分 | 章节 | 内容 |
|---|---|---|
| 第一部分 | 第 1~10 章 | 上位机（WPF 控制面板）的架构、协议、状态机、参数 |
| 第二部分 | 第 11 章 | 下位机控制（`SDK/Mic_Sdk.c`）的架构、数据模型、收发模型、指令处理 |
| 对齐 | 第 12 章 | 上位机 ↔ 下位机协议接口逐项对齐清单（含双方版本差异） |
| 附录 | 第 13~14 章 | 已知限制与风险、设计决策演进记录 |

---

## 2. 系统上下文

```
        ┌──────────────────────────────┐        RS232 115200 8N1        ┌───────────────────┐
        │  上位机（本程序, WPF）          │  ───────────────────────────►  │  下位机（Mic_Sdk.c）│
        │                              │  0x2F 帧（无 CRC/无帧尾）        │  电机/测速/按键/ERR │
        │  ┌────────┐   ┌───────────┐  │  ◄───────────────────────────  │                   │
        │  │ UI 层   │◄──┤ 通信层     │  │        0x81 心跳 / 0x80 ACK      └───────────────────┘
        │  └────────┘   └───────────┘  │
        └──────────────────────────────┘
                 ▲ 自动枚举 + 协议探测（0x81 静默监听 / 0x07 握手）
                 │
        USB 转串口（CP210x / CH34x / FTDI 等）→ 设备 COM 口
```

- 航插（下位机通信口）与脚踏（上位机外设）都可能表现为 USB 串口，靠**协议命中**区分航插、靠**关键字（VID/PID）**识别脚踏。
- 只有"协议命中"的端口才允许建立航插链路，关键字分类只用于显示。

---

## 3. 代码结构

| 路径 | 职责 |
|---|---|
| `MainWindow.xaml` | 主面板布局（247×140 悬浮窗）、设置弹窗 `SettingsPopup`、校验提示条 `PopupHint` |
| `MainWindow.xaml.cs` | 全部业务流程：链路回调、反馈解析、运行判据、显示仲裁、设置面板、窗体外壳（去 Aero Snap） |
| `Control/inf_change.cs` | 通信层：端口枚举、协议探测、链路收发、协议解析、ACK 闭环、事件分发 |
| `Control/SdkLog.cs` | 追加写文件日志（`sdk_run.log`）+ Debug 输出 + HEX 格式化 |
| `SevenSegmentDigit.xaml(.cs)` | 七段数码管自绘控件（段码表支持 0~9 / E / 熄灭） |
| `App.xaml(.cs)`、`AssemblyInfo.cs` | 应用入口与程序集信息 |
| `SDK/Mic_Sdk.c`、`Mic_Sdk.h` | 下位机固件参考实现（本仓库只读，不参与编译） |
| `snapshots/`、`Out/`、`publish/`、`publish_single/` | 历史快照、交付副本、发布产物（`csproj` 已排除编译） |

依赖包：`System.Management 8.0.0`（WMI 枚举）、`System.IO.Ports 8.0.0`（串口）。

---

## 4. 分层架构

```
┌───────────────────────────────────────────────────────────┐
│ 表现层  MainWindow.xaml(.cs)                              │
│   · 数码管 Digit1~5   · 状态灯 SetStatus   · 转子 RotorRotate│
│   · 设置弹窗 SettingsPopup / 校验 IsValidStep              │
└────────────────────────▲──────────────────────────────────┘
                         │ 事件（UI 线程 Dispatcher.BeginInvoke）
┌────────────────────────┴──────────────────────────────────┐
│ 通信层  InfChange                                         │
│  ┌─────────────┐ ┌─────────────┐ ┌──────────────────────┐ │
│  │ 端口枚举     │ │ 协议探测     │ │ 链路 RX / TX          │ │
│  │ WMI→注册表   │ │ 静默+握手    │ │ 解析状态机/发送队列    │ │
│  └─────────────┘ └─────────────┘ └──────────────────────┘ │
│  事件：FeedbackReceived / AckReceived / LinkOpened / ...   │
└────────────────────────▲──────────────────────────────────┘
                         │ SerialPort（System.IO.Ports）
┌────────────────────────┴──────────────────────────────────┐
│ 设备层  下位机固件（Mic_Sdk.c）：rx_poll / Mic_Sdk_Exec    │
│        / heart_feedback                                  │
└───────────────────────────────────────────────────────────┘
```

**单向依赖**：表现层只依赖 `InfChange` 的公开事件与 `SendCommand*`；通信层不引用任何 WPF 控件类型，仅通过 `Dispatcher` 封送事件。

---

## 5. 通信层设计（`Control/inf_change.cs`）

### 5.1 数据模型

| 类型 | 说明 |
|---|---|
| `InfPortInfo` | 端口识别结果：`PortName / Type / Description / PnpDeviceId / Host` |
| `InfDeviceType` | `Unknown / Aviation(航插) / FootPedal(脚踏)` |
| `InfHost` | 归属：航插=Lower（下位机），脚踏等=Upper（上位机） |
| `InfFeedback` | 0x81 心跳解析结果：`SoftwareRun / SetRpm / Rpm / HasRpm / CurrentMa / Status / Alarm / TBAJ` |
| `InfAck` | 0x80 应答解析结果：`Cmd / Result / SetRpm / RegIndex / RegValue` |

`HasRpm` 是运行判据的关键字段：短帧为 `false`（Rpm 恒 0 仅占位），长帧为 `true`。

### 5.2 端口识别流水线

```
Timer(2s) ─► Poll()（Interlocked 防重入）
              │
              ├─ Scan()：WMI Win32_PnPEntity(Ports/COM) 解析 Caption 里的 (COMx)
              │          失败回退注册表 HARDWARE\DEVICEMAP\SERIALCOMM
              │          关键字 MatchType() → 初始分类（主要识别脚踏）
              ├─ 清理拔出端口（同时清 _classified/_lastProbeTick/_probeHitPorts/
              │   _probeFallback；若为当前链路端口则 CloseLink）
              ├─ ShouldProbe() 筛选 → Task.Run(ProbePort) 异步并行（不阻塞 Poll）
              └─ ApplyResults()：汇总分类 → 仅醒目命中端口建链 → 变化时抛事件
```

**探测策略（每端口，后台线程）**

1. 打开串口 115200 8N1；
2. **阶段 1 静默监听 1500ms**（≥下位机心跳周期约 1.2s）：收到合法 `0x81` 或任意 `0x80` 即命中；
3. **阶段 2 主动握手 700ms**：发送 `0x07`（2 字节随机 nonce）——
   - 简化版固件落默认分支仍回 `0x80` → 命中；
   - 旧静默固件回 `0x82`，校验 `"HDs2"` 魔数 + nonce 回显 → 命中。
4. 监听/等待循环中每 5ms `Thread.Sleep` 让步，避免忙等。

**重探规则（`ShouldProbe`）**

| 状态 | 是否重探 |
|---|---|
| 新端口（未分类） | 是 |
| `FootPedal` | 否 |
| `Aviation` 且已协议命中 | 否 |
| `Aviation` 但未命中 / `Unknown` | 每 20s 重探 |

**并发控制**

- `_polling`（`Interlocked.Exchange`）防止 Timer 与探测回调并发进入 `PollCore`；
- `_classifyLock` 串行化分类字典；
- `_probeTasks`（`ConcurrentDictionary`）防同一端口重复发起探测；
- 探测完成 `OnProbeDone` 立即 `Scan + ApplyResults`，不必等下个 2s 周期。

### 5.3 链路与解析

- 链路建成后 `SerialPort.DataReceived` → `ProtocolParser.Feed`（独立 `parserLock`，避免 Close 等待处理器时与 `_linkLock` 死锁）。
- `ProtocolParser`：IDLE/CMD/LEN/DATA 四态定长组帧，异常回 IDLE 重同步；帧后诊断尾 `0x55 0xAA` 与调试文本在 IDLE 态自然丢弃。
- `OnLinkFrame`：
  - `0x81` → 按 `LEN` 分支解析（3=短帧，8=长帧），字段合法性校验后抛 `FeedbackReceived`；
  - `0x80` → 统一 7 字节 ACK 解析，`PurgePendingTx` 作废该指令待发副本，唤醒 `_pendingAcks` 等待者，抛 `AckReceived`。

### 5.4 全双工 TX 通路

```
调用方（UI/任意线程）── SendCommand(cmd, data) ──► ConcurrentQueue（上限 32）
                                                         │
                              专用线程 TxLoop ◄── SemaphoreSlim 信号
                                                         │
                                           SerialPort.Write（一帧一次原子写）
```

- 入队 O(1) 非阻塞，避免串口写阻塞 UI；
- 链路断开/写失败时清空队列；
- `SendCommandCheckedAsync(cmd, data, timeoutMs=500, retries=2)`：注册 `TaskCompletionSource` → 等待 ACK，超时重发，耗尽返回 `null`；`finally` 中按 `(cmd, tcs)` 精确删除，避免误删后续等待者。

### 5.5 事件与线程封送

`Raise(action)`：有 `Dispatcher` 时 `BeginInvoke` 到 UI 线程，否则直接执行。所有对外事件（`PortsChanged / AviationConnectedChanged / FootPedalConnectedChanged / FeedbackReceived / AckReceived / LinkOpened / LinkClosed`）均在 UI 线程触发，表现层无需自行加锁。

---

## 6. 协议规格

### 6.1 帧格式

```
[0x2F][CMD][LEN][DATA × LEN]        总长 = LEN + 3，无 CRC、无帧尾
```

### 6.2 指令表（上位机 → 下位机）

| CMD | 名称 | DATA | 说明 |
|---|---|---|---|
| `0x01` | SetRpm | 4B 大端 RPM | 设置转速，执行后 `SetRpm_flag` 清 0（ACK Result 恒 0） |
| `0x02` | Start | 无 | 置 `SoftwareRun_flag=1`（联机握手语义，非电机启动） |
| `0x03` | Stop | 1B（=1 断开） | 清 `SoftwareRun_flag=0`，退出设置面板时使用 |
| `0x04` | SetDir | 1B（1=正 2=反） | 运行中固件拒改 |
| `0x07` | Handshake | 2B nonce | 仅用于端口探测 |
| `0x0A` | Key | 按键类型 + 状态 | 模拟脚踏/面板/高低速按键 |
| `0x0B` | QuerySetRpm | 无 | 回读下位机实际 `SetRpm` |
| `0x0C` | RegRead | 1B 字段号 | 读结构体字段，回 ACK 带编号+值 |
| `0x0D` | RegWrite | 编号 + 值 | 写结构体字段 |

### 6.3 心跳 `0x81`（下位机 → 上位机）

| 帧长 | 场景 | DATA 布局 |
|---|---|---|
| LEN=3（短帧） | 停止 / 报警 | `SoftwareRun, ERR, TBAJ` → `HasRpm=false` |
| LEN=8（长帧） | 运行中 | `SoftwareRun, Rpm(3), 电流(2), ERR, TBAJ` → `HasRpm=true` |

- 报警优先：`Alarm∈1..10` 直接进入报警态；`Alarm` 若 >10 视为污染忽略。
- 坏值降级：长帧 `Rpm>99999` 降为 0 但**保留整帧**（保运行证据，避免保鲜连坐复位）。
- 短帧 `TBAJ` 非法时降级为 0，不让整帧丢弃。
- 注：仓库内 `SDK/Mic_Sdk.c` 的 `send_feedback` 当前为 **LEN=2 / LEN=7（不含 TBAJ）**，与上位机的 3/8 解析存在版本差异，联调前需对齐（见第 12 节）。

### 6.4 应答 `0x80`

```
[0x2F][0x80][0x07][被确认 CMD][Result][RegIndex][值 v3 v2 v1 v0]
```

- `Result` = 对应指令所写旗帜位的当前值（镜像语义）；
- `0x0B` 用值字段承载 `SetRpm`；`0x0C/0x0D` 用 `RegIndex` + 值字段承载寄存器号与字段值。

### 6.5 寄存器长轮询

`_regPollTimer` 每 200ms 轮转发送一次 `0x0C`，字段序列 `{11, 14, 5}`：

| 字段号 | 含义 | 用途 |
|---|---|---|
| 5 | MotorDir | 只读显示实际方向（1=正 0/2=反） |
| 10 | SetRpm | 待机时显示设定值（轮询序列外，收到即用） |
| 11 | Rpm | 转速兜底 + 运行判据同源输入 |
| 14 | ERR | 主动探测报警（不等报警心跳，心跳在报警期可能很慢） |

---

## 7. 表现层与状态设计（`MainWindow.xaml.cs`）

### 7.1 界面组成

| 元素 | 作用 |
|---|---|
| `MainPanel`（247×140 悬浮窗） | 常驻置顶（`Topmost=true`），去除 Aero Snap，可拖动 |
| `Digit1~5`（`SevenSegmentDigit`） | 5 位转速 / `E+码` / `E100` |
| `RotorImage` + `RotorRotate` | 转子动画，`CompositionTarget.Rendering` 逐帧累加角度（固定 1000ms/圈） |
| `BtnPower` | 启动语义按钮，实际作为"进入设置"入口；运行态才可点 |
| `BtnSetSpeed` | 设置入口，运行中置灰 |
| `SettingsPopup` | 设置弹窗（转速输入、只读方向、确认/取消、`PopupHint` 提示条） |

### 7.2 显示仲裁（唯一写屏入口）

`UpdateDisplay(rpm)` 是所有数据源（心跳长帧、寄存器 #11）的唯一显示仲裁：

```
报警(_hasAlarm)  → ShowAlarm(_alarmCode)        // E+码，优先级最高
运行(_isRunning) → rpm==0 且已有值 ⇒ 保持上次值   // 零值宽容，防丢脉冲闪 0
                  否则 _currentSpeed=rpm → SetDigits
停止            → 0 → SetDigits(0)
```

### 7.3 运行判据（频闪治理核心）

| 输入 | 处理 |
|---|---|
| 短帧（`HasRpm=false`） | 不驱动运行态/显示；仅当 `TBAJ==1`（脚踏踩住）刷新保鲜时间戳 |
| 长帧（`HasRpm=true`）且 `Rpm>0` | 刷新 `_lastValidDataTick`；`SyncRunStateByRpm` |
| 长帧且 `Rpm==0` | 不动作（不升也不降），不刷新保鲜 |
| 寄存器 #11 非零 | 与长帧同源：刷新保鲜 + `SyncRunStateByRpm` |
| 寄存器 #11 = 0 | 只走 `UpdateDisplay`（运行态保持上次值） |

`SyncRunStateByRpm`：`Rpm>0` 持续 `RpmRunHoldMs`(50ms) 才升运行（去抖）；`Rpm=0` 直接 return。

**为什么不是"帧长即运行"**：实测固件在未启动状态也可能发长帧（`2F 81 08 00 00 00 00 00 0E 00 00`），单靠帧长会误判运行；而帧间隔约 1.17s > 保鲜 1s，会导致运行↔停止周期性翻转（频闪）。因此采用"帧长过滤（外层）+ Rpm>0 佐证（内层）"；最终停止由保鲜兜底。

### 7.4 兜底机制

| 机制 | 阈值 | 行为 |
|---|---|---|
| 数据保鲜 `CheckDataFreshness` | 1000ms 无运行证据 | 强制回 0 + 复位运行态 + 清 `_rpmPositiveStartTick` |
| 通讯监测 `CheckCommStatus` | 链路未建立 / `_lastRxTick==0` / 2000ms 无任何帧 | 置 `_linkLost`，复位 UI，显示 `E100`；恢复时清屏由反馈接管 |
| 建链宽限 `OnLinkOpened` | 建链即置 `_lastRxTick` | 避免首帧未到就被判未通讯 |

保鲜周期必须 > 寄存器轮询轮转周期（3×200ms=600ms），否则两次 #11 之间会提前复位，造成运行态↔停止态抖动。

### 7.5 报警处理

- 来源：心跳 `Alarm`（1~10）或寄存器 #14 `ERR`（0~10）；两者都进入 `_hasAlarm`，显示 `E+(ERR-1)`。
- 进入报警：复位运行态、停转子、回待机、恢复设置按钮、立即写屏。
- 设置面板：报警时输入框只读；`Err>10` 视为字节交错污染，忽略。

### 7.6 转速设置与校验

- 范围 `10000~70000`；步进：`≤30000` 用 1000，`>30000` 用 10000（`<10000` 步进 100 规格保留但不可触达）。
- 打开面板：主动 `0x0B` 回读；回读值合法则预填，否则留空。
- 确认：`0x01` 下发 → `0x0B` 回读比对，一致才记"设置成功"（`0x01` 的 ACK Result 恒 0，不可作为成功判据）。
- 非法输入：不下发，底部 `PopupHint` 淡入 120ms、2.2s 后淡出 350ms；失焦时回退到上次合法值。
- 面板生命周期：打开 → `0x02`（`SoftwareRun_flag=1`）；退出/取消 → `0x03 DATA=1`（清 0）。

### 7.7 状态机

```
                    ┌──────────── 报警(ERR/Alarm) ────────────┐
                    ▼                                         │
  [未通讯 E100] ──通讯恢复──► [待机 00000] ──Rpm>0 持续50ms──► [运行]
        ▲                        ▲                             │
        └── 链路断/2s 无帧 ───────┴── 保鲜 1s 无运行证据 ───────┘
```

- 运行态内部再按实测转速分高/低速灯：`_currentSpeed >= 50000` → HighSpeed，否则 LowSpeed（`MotorStatus` 仅维护内部状态）。
- 转子方向跟随 `_isForward`（寄存器 #5 只读同步），速度恒定 1000ms/圈。

---

## 8. 线程模型

| 线程/调度器 | 职责 |
|---|---|
| UI 线程（Dispatcher） | 全部 WPF 更新、事件回调（`Raise` 封送） |
| `Timer`（2s，`InfChange.Start`） | `Poll` → 枚举/发起探测/汇总建链 |
| ThreadPool（`Task.Run`） | `ProbePort` 并行探测；`Task.ContinueWith` → `OnProbeDone` |
| `InfChange.TxLoop`（专用后台线程） | 串口写（单帧原子写） |
| `SerialPort.DataReceived`（系统串口线程） | 读字节喂解析器 → 解析 → `Raise` 事件 |
| `DispatcherTimer`（200ms `_regPollTimer`） | `CheckCommStatus` + `CheckDataFreshness` + 寄存器轮询 |
| `CompositionTarget.Rendering` | 转子逐帧动画 |

同步手段：`_sync`（启停）、`_linkLock`（链路）、`parserLock`（解析）、`_classifyLock`（分类）、`Interlocked._polling`（Poll 防重入）、`ConcurrentDictionary/ConcurrentQueue`（跨线程共享）、`TaskCompletionSource`（ACK 等待）。

---

## 9. 关键参数（集中查阅）

| 常量 | 值 | 位置 | 含义 |
|---|---|---|---|
| `LinkBaudRate` | 115200 | `inf_change.cs` | 波特率 8N1 |
| `ProbeListenMs` | 1500 | `inf_change.cs` | 静默监听窗口 |
| `ProbeTimeoutMs` | 700 | `inf_change.cs` | 握手等待窗口 |
| `ProbeRetryMs` | 20000 | `inf_change.cs` | 未命中端口重探间隔 |
| `TxQueueMax` | 32 | `inf_change.cs` | 发送队列背压上限 |
| `pollMs` | 2000 | `MainWindow_Loaded` | 端口轮询周期 |
| `RegPollIntervalMs` | 200 | `MainWindow.xaml.cs` | 寄存器轮询间隔 |
| `RpmRunHoldMs` | 50 | `MainWindow.xaml.cs` | 运行正判去抖 |
| `RpmDataTimeoutMs` | 1000 | `MainWindow.xaml.cs` | 数据保鲜超时 |
| `NoCommTimeoutMs` | 2000 | `MainWindow.xaml.cs` | 通讯丢失判定 |
| `RpmRangeMin/Max` | 10000 / 70000 | `MainWindow.xaml.cs` | 转速可设置范围 |
| `DefaultSpeed` | 0 | `MainWindow.xaml.cs` | 默认转速（与下位机上电值一致） |

---

## 10. 发布与部署

- 发布方式：`dotnet publish` 单文件自包含，产物在 `publish_single/`（`HDs2-SDK.exe` + WPF 原生依赖 + `sdk_run.log`）。
- 运行日志：程序目录下 `sdk_run.log`（追加写，UTF-8），联调主要依据。
- 启动行为：窗口置顶常驻；`OnClosed` 时尽力发 `0x03 DATA=1` 把联机位清零后释放串口。

---

## 11. 下位机控制架构（`SDK/Mic_Sdk.c`）

### 11.1 角色定位与设计模式

下位机固件是"**中继站 + 结构体输入**"模型：通信 SDK 不承载业务逻辑，只做指令与状态的中转。

```
上位机指令 ──► rx_resolve：直接编辑 sdk 对应设置位（旗帜位）
                        │
                        ├─► send_ack(0x80)：结果字节 = 该旗帜位当前值（镜像）
                        ▼
               sdk（协议层与应用层共享的数据中心）
                        │
                        ├─► Mic_Sdk_Exec：仅镜像 sdk → 硬件（不做裁决）
                        └─► heart_feedback：读 sdk.Rpm / Electricity / ERR → 0x81 上报
```

- 对接点只有 3 个：串口中断里每字节调 `rx_handle_byte()`；主循环每圈调 `Mic_Sdk_Poll()`；在 `uart_send_byte()` 内实现实际串口发送。
- 实时数据（`Rpm` / `Electricity` / `ERR` / `TBAJ`）由**采集层**（定时器中断、ADC、按键扫描）直接写入 `sdk`，SDK 本身不采集，也不做启停裁决。

### 11.2 数据模型 `_Sdk`

| 字段 | 类型 | 语义 |
|---|---|---|
| `SoftwareRun_flag` | u8 | 联机握手位：上电 0，`0x02` 置 1，`0x03 DATA=1` 清 0 |
| `SetRpm_flag` | u8 | 设置转速瞬时状态位：`0x01` 处理中置 1，赋值完成、回 ACK 前清 0 |
| `MotorRun` / `MotorStop` | u8×2 | 互锁启停位：脚踏/面板按下 `run_on()`，松开 `run_off()` |
| `MotorDir` | u8 | 1=正转 2=反转；运行中拒改 |
| `MotorPower` | u8 | 供电位，缩小版无协议写入指令 |
| `TBAJ` / `ZXAJ` / `GDQHAJ` | u8×3 | 脚踏 / 面板 / 高低速按键电平 |
| `SetRpm` | u32 | 设定转速（`0x01` 写入，≤99999） |
| `Rpm` | u32 | 实测转速（采集层写入，心跳上报） |
| `SetElecricity` / `Electricity` | u16×2 | 设定 / 实测电流（mA） |
| `ERR` | u8 | 三态报警：0=正常 1=过载 2=故障（采集层写入） |

### 11.3 接收路径：中断生产 / 主循环消费（免锁 SPSC）

```
USART3 RX 中断 ──► rx_handle_byte(b)：写 rx_buf[rx_w]，rx_w 前移，重装 rx_idle_cnt
                                   │
主循环 rx_poll() ◄─────────────────┘：只读 rx_r，逐帧解析
```

- 环形缓冲 `RX_BUF_SIZE=128`（2 的幂，掩码取模）；满缓冲时**丢新字节保护已攒数据**。
- 单生产者（中断只写 `rx_w`）/ 单消费者（主循环只写 `rx_r`），指针各动各的，天然无竞态，无需关中断。
- 组帧：`找帧头 0x2F → 读 LEN 算总长 → 攒齐即解析（无 CRC）→ 整帧消费`。
- **假帧头清障**：帧未攒齐且连续 `RX_IDLE_POLLS=20` 圈无新字节（中断会重装计数）→ 丢队首 1 字节重找；在途帧因每字节重装计数而永不被误清。依据是字节间隔 87µs@115200 远小于主循环一圈 ≥1ms（阻塞发送）。

### 11.4 指令处理 `rx_resolve`

| CMD | 前置校验 | 动作 | ACK 结果（镜像） |
|---|---|---|---|
| `0x01` | `SetRpm_flag==0` 且 `LEN==4` | 置 `SetRpm_flag=1`，4 字节大端赋值 `SetRpm`；>99999 撤回 | `SetRpm_flag`（完成前置 0，故恒 0） |
| `0x02` | `LEN==0` | `SoftwareRun_flag=1`，并 `run_on()`（当前仓库实现会启动电机） | `SoftwareRun_flag` |
| `0x03` | `LEN==0`；或 `LEN==1 && DATA==1` | `LEN==0`：无按键按住则 `run_off()`；`DATA==1`：清联机位 + 无按键则停机 | `MotorStop` |
| `0x04` | `LEN==1`、值 1/2、`MotorRun==0` | 写 `MotorDir` | `MotorDir` |
| `0x0A` | `LEN==2`、`DATA[1]≤1` | `key_event_tba/zx/gdq()`（与实体按键同入口） | 对应按键位电平 |
| `0x0B` | 无 | 纯查询，不改状态 | `1` + 附带 `SetRpm`(4B) |
| 其他 | — | 不执行（缩小版未实现） | `0` |

收尾补发：`0x01` 设置完成、`0x02` 成功后，ACK 之后**立即补发一帧 `0x81`**（不等周期心跳），让上位机尽快确认。

### 11.5 状态编辑与按键优先级

- `run_on()`：`MotorRun=1, MotorStop=0`；`run_off()`：`MotorRun=0, MotorStop=1, Rpm=0`。
- `key_event_tba(pressed)`：置 `TBAJ`；按下 → `run_on()`；松开 → 仅当 `ZXAJ==0` 才 `run_off()`。
- `key_event_zx(pressed)`：同理，松开时仅当 `TBAJ==0` 才停机（**脚踏优先**）。
- `key_event_gdq`：按下沿（0→1）触发 `gear_toggle()`，成对改写 `SetRpm` / `SetElecricity`；四个 `GEAR_*` 宏未配置（全 0）时不改写，防止切档把转速归零。

### 11.6 硬件输出 `Mic_Sdk_Exec`

只做"状态 → 硬件"的镜像，不含任何裁决：

- `MotorRun / MotorStop` → 电机使能 / 刹车（互锁位）；
- `MotorDir` → 方向 IO；
- `MotorRun` → 输出 PWM（`SetRpm`），未运行则给定归零（不使用 `SetRpm_flag` 门控，因其是瞬时位，Exec 运行时已清 0）；
- `SetElecricity` → 电流限制 DAC。

各驱动点均为 `TODO`，需按实际 MCU 填寄存器/IO。

### 11.7 心跳上报 `send_feedback`

| 条件 | DATA 长度 | 内容 |
|---|---|---|
| `ERR != 0`（报警，无论是否运行） | 2 | `SoftwareRun_flag, ERR` |
| `MotorRun==1` 且 `ERR==0` | 7 | `SoftwareRun_flag, Rpm(3), Electricity(2), ERR` |
| 其余（停止且联机） | 2 | `SoftwareRun_flag, ERR` |

- 周期：`HEART_DIV=8`，主循环每 8 圈发一帧（≈10ms，≈100 帧/秒），兼顾保活与总线占用。
- 心跳**不受联机位门控**：即使 `SoftwareRun_flag=0` 也持续发送，否则上位机探测阶段无法发现设备。
- 该实现**不含 `TBAJ` 字节**（与上位机 3/8 解析有差异，见第 12 章）。

### 11.8 发送路径与诊断尾

```
frame_flush(cmd, data, len)：tx_buf = [2F][cmd][len][data...] → 逐字节 uart_send_byte
uart_send_byte(b)：等 TC（开中断）→ __disable_irq() → 双检 TC → 写 USART3->TDR → __enable_irq()
```

- 临界区仅几时钟周期，防协议帧与调试 `printf` 并发写 TDR 造成吞字节/覆盖。
- **诊断尾**：`frame_flush` 在帧后追加 `0x55 0xAA`（不在 LEN 内）。上位机解析器在 IDLE 态按噪声丢弃，不影响协议；正式版应删除。

### 11.9 主循环时序

```
while (1)
  └─ Mic_Sdk_Poll()
       ├─ ① rx_poll()      ：解析指令 → 编辑旗帜位 → 回 ACK（+ 必要时补发心跳）
       ├─ ② Mic_Sdk_Exec()  ：镜像 sdk → PWM / IO / DAC
       └─ ③ 每 8 圈 heart_feedback()：发 0x81
```

发送是**阻塞**的（每字节等 TC），因此主循环一圈 ≥1ms，这也是空闲清障圈数取 20 的依据。

### 11.10 固件关键参数

| 宏 | 值 | 含义 |
|---|---|---|
| `FRAME_HEAD` | `0x2F` | 帧头 |
| `RX_DATA_MAX` | 60 | DATA 上限（LEN 超限即假帧头） |
| `RX_BUF_SIZE` | 128 | 接收环形缓冲（2 的幂） |
| `RX_IDLE_POLLS` | 20 | 假帧头清障圈数 |
| `HEART_DIV` | 8 | 心跳分频（≈10ms） |
| `RPM_MAX` | 99999 | 设定转速上限 |
| `RPM_BYTES` | 3 | 心跳中转速字节数（0x01 指令为 4 字节） |
| `GEAR_LO/HI_RPM`、`GEAR_LO/HI_MA` | 0（未配置） | 高低速档位参数 |

---

## 12. 上位机 ↔ 下位机接口对齐清单

仓库固件与上位机解析的协议**存在版本差异**，联调前必须逐项对齐（以下"现状"均指仓库代码，非已烧录固件）。

| # | 项目 | 上位机现状 | 仓库固件现状 | 影响 / 处理 |
|---|---|---|---|---|
| 1 | 心跳短帧 | `LEN=3`：`SoftRun, ERR, TBAJ` | `LEN=2`：`SoftRun, ERR` | 上位机仅识别 3/8，2/7 帧被 `else return` 丢弃 → 表现为 E100 或永不更新 |
| 2 | 心跳长帧 | `LEN=8`：`SoftRun, Rpm(3), 电流(2), ERR, TBAJ` | `LEN=7`：无 `TBAJ` | 同上；且上位机"脚踏踩住刷新保鲜"逻辑失效（`TBAJ` 恒 0） |
| 3 | ACK 长度 | 统一 `LEN=7`（`cmd, result, idx, 值4B`），入口判 `frame[2]>=7` | 普通指令 `LEN=2`（`cmd, result`） | 仓库固件的 ACK 会被上位机整体忽略 → 所有 `SendCommandChecked` 超时 |
| 4 | `0x0B` ACK 布局 | `cmd, result, idx=0, SetRpm(4B)` | `cmd, result, SetRpm(4B)`（`LEN=6`） | 缺少 idx 字节且长度不符，回读值无法解析 |
| 5 | `0x0C/0x0D` 寄存器 | 200ms 轮询 `#11 Rpm / #14 ERR / #5 Dir` | **未实现**，落 default 回 ACK result=0 | 上位机要求 `Result==1`，故寄存器值永不更新（报警探测、转速兜底失效） |
| 6 | `0x02` 语义 | 打开设置面板置联机位，**不启动电机** | `LEN==0` 时 `SoftwareRun_flag=1` **并 `run_on()`** | 上位机每次开设置面板都会启动电机 → 固件应改为只置联机位 |
| 7 | `0x03 DATA=1` | 关闭面板 / 退出程序时发 | 清联机位，无按键按住时停机 | 语义一致，可直接沿用 |
| 8 | `0x07` 握手 | 探测阶段 2 发 nonce，收到任意 `0x80` 即命中 | 无此指令，落 default 回 ACK result=0 | 可命中（探测只看 CMD==0x80，不校验长度） |
| 9 | 诊断尾 `55 AA` | 按噪声丢弃 | 每帧追加 | 协议可兼容；正式版应删除 |
| 10 | 心跳门控 | 依赖停止态也有心跳用于端口发现 | 未用联机位门控，持续发 | 一致 |

**结论**：若现场固件与仓库一致，第 1~6 项会导致"识别到端口但不更新 / 设置超时"；应优先统一为"心跳 3/8（带脚踏）+ ACK 7 字节统一格式 + 实现 0x0C 寄存器读"，并修正 `0x02` 不启动电机。

---

## 13. 已知限制与风险

1. **固件帧长版本差异**：仓库 `Mic_Sdk.c` 的 `send_feedback` 为 LEN=2（短）/ LEN=7（长），不含 `TBAJ`；上位机当前解析 LEN=3 / LEN=8。正式烧录固件必须与上位机解析一致，否则心跳会被 `else return` 丢弃（表现为 E100 或永不更新）。完整差异见第 12 节。
2. **长帧间隔与保鲜竞速**：固件心跳约 10ms 一帧，但联调观察到约 1.17s 间隔的"有效心跳"，任何把保鲜缩短到 < 1s 的改动都可能重新引入频闪。
3. **无 CRC**：协议无校验，只能做字段合法性过滤（SoftRun/ERR 范围、Rpm ≤ 99999、TBAJ 0/1）；字节交错污染只能靠范围与降级策略兜底。
4. **寄存器 #11 读路径**：固件侧 Rpm 未钳位（曾出现 `0xFFFFFFD5`），上位机已降级处理，根治在固件。
5. **探测依赖心跳**：若固件上电后不主动发心跳且不回 ACK，端口无法被识别（仅能关键字兜底显示，但不能建链）。
6. **脚踏 TBAJ 可被污染**：不作为运行判据，仅作保鲜刷新与日志参考。

---

## 14. 演进要点（设计决策记录）

| 问题 | 根因 | 最终方案 |
|---|---|---|
| 频闪（第 1 层） | 短帧 Rpm=0 驱动运行态/显示 | 引入 `HasRpm`，短帧不驱动显示 |
| 频闪（第 2 层） | 长帧坏值整帧丢弃 → 保鲜连坐复位 | 坏值降为 0 但保留整帧 |
| 频闪（第 3 层） | 轮询周期 600ms 短于保鲜 500ms | 保鲜提升到 1000ms |
| 频闪（第 4 层，未启动） | 固件未启动也发长帧，1.17s 间隔触发保鲜复位 | 运行判据改为 `Rpm>0` 去抖、`Rpm=0` 不动作 |
| E100 变 00000 | `noComm` 漏判 `_lastRxTick==0` | 补判 + 建链置位宽限 |
| 偶发 00000 | 关键字分类误建假链路 | 仅协议命中端口可建链（`_probeHitPorts`） |
| 热插不更新 | 已缓存 Aviation 端口不重探 | 未命中端口 20s 周期性重探 |
| 重启检测慢 | 探测同步阻塞 Poll，多口串行 | 探测异步化 + 并行 + 完成即建链 |
| 非法转速下发 | 输入未校验 | 范围 + 档位校验，非法不下发并提示 |
