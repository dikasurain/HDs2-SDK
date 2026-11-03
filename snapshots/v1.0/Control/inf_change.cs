using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Threading;
using System.Windows.Threading;

namespace HDs2_SDK.Control
{
	/// <summary>可识别的接入设备类型（设备“是什么”，与归属无关）</summary>
	public enum InfDeviceType
	{
		/// <summary>未识别设备</summary>
		Unknown = 0,
		/// <summary>航插（下位机的通信口）：上位机经串口连至下位机航插，协议探测识别</summary>
		Aviation = 1,
		/// <summary>脚踏开关（上位机外设）：直接接在电脑上，串口关键字识别</summary>
		FootPedal = 2,
	}

	/// <summary>设备归属（单拎维度：脚踏属上位机，航插属下位机）</summary>
	public enum InfHost
	{
		/// <summary>上位机（电脑）：脚踏是上位机外设，接在电脑上由本模块扫描识别</summary>
		Upper = 0,
		/// <summary>下位机（骨动力主机）：航插是下位机的通信口，
		/// 上位机通过串口与下位机航插建立通信链路</summary>
		Lower = 1,
	}

	/// <summary>端口识别结果</summary>
	public sealed class InfPortInfo
	{
		/// <summary>COM 口名称（如 COM3）</summary>
		public string PortName { get; init; } = string.Empty;
		/// <summary>识别出的设备类型</summary>
		public InfDeviceType Type { get; init; } = InfDeviceType.Unknown;
		/// <summary>设备描述（WMI Caption，注册表回退时为设备路径）</summary>
		public string Description { get; init; } = string.Empty;
		/// <summary>PNP 设备 ID（含 VID_/PID_，可用于精确匹配）</summary>
		public string PnpDeviceId { get; init; } = string.Empty;
		/// <summary>设备归属：识别出协议的端口（对端为下位机航插）= Lower；其余（脚踏等上位机外设）= Upper</summary>
		public InfHost Host { get; init; } = InfHost.Upper;

		public override string ToString() =>
			Type == InfDeviceType.Unknown ? PortName : $"{PortName} → {Type}";
	}

	/// <summary>下位机综合反馈帧数据（CMD=0x81）</summary>
	public sealed class InfFeedback
	{
		/// <summary>软件运行位（帧首位，上电默认 0；下位机心跳主发，0=未运行 1=已运行）</summary>
		public byte SoftwareRun { get; init; }
		/// <summary>设置转速 SetRpm（4 字节大端，上位机 0x01 设的值；待机时据此显示）</summary>
		public uint SetRpm { get; init; }
		/// <summary>实时转速 RPM（3 字节大端，支持 > 65535）</summary>
		public uint Rpm { get; init; }
		/// <summary>电流 mA</summary>
		public ushort CurrentMa { get; init; }
		/// <summary>状态：0=空闲 1=运行 2=故障</summary>
		public byte Status { get; init; }
		/// <summary>报警：0=无报警，非 0=报警码</summary>
		public byte Alarm { get; init; }
	}

	/// <summary>下位机指令确认应答帧数据（CMD=0x80）</summary>
	public sealed class InfAck
	{
		/// <summary>被确认的上位机指令码</summary>
		public byte Cmd { get; init; }
		/// <summary>执行结果 = 对应指令所写旗帜位的当前值（直接镜像，语义见 CmdAck 注释）：
		/// 上位机拿"回显值 vs 下发值"交叉验证执行结果（如 0x02 启动回显 SoftwareRun=1）</summary>
		public byte Result { get; init; }
		/// <summary>0x0B 查询 SetRpm 的 ACK 附带的下位机当前实际存储 SetRpm（4 字节大端）；
		/// 其他指令无此字段 = 0</summary>
		public uint SetRpm { get; init; }
	}

	/// <summary>
	/// 端口识别与通信模块（对应 SDK/Mic_Sdk.c 简化版下位机协议）：
	///
	/// 职责流水线（USB → RS232 → 可识别数据）：
	///   1. 接收：USB 口上经 RS232 协议（115200 8N1）传入的原始字节流；
	///   2. 转换：字节流按状态机组帧（帧头+LEN 定长），转换为可识别的协议模组（帧）；
	///   3. 识别：判定哪个 USB 口对应哪个设备 —— 协议命中 = 下位机航插链路，
	///      关键字命中 = 脚踏（上位机外设）；
	///   4. 解析：识别到数据后按 CMD 解析，根据数据内容产出对应的结构化数据
	///      （反馈帧 → Rpm/电流/状态/报警，经事件抛出到 UI 线程）。
	///
	/// 协议：RS232 全双工 115200 8N1
	/// 帧格式：起始位(0x2F) + CMD + LEN + DATA（无 CRC、无帧尾，总长 = LEN+3）；
	///   下位机每帧后可能追加 0x55 0xAA 诊断尾字节（吞尾定位用），解析器作噪声丢弃
	///
	/// 全双工设计（收发两条独立通路，互不阻塞）：
	///   RX：DataReceived → 独立解析锁 → 状态机组帧 → 校验 → 事件（UI 线程）
	///   TX：调用方只入队（O(1) 非阻塞）→ 专用发送线程串行写串口，
	///       一帧一次原子写、多帧不交错；队列有背压上限，链路断开自动清空。
	///
	/// 指令可靠性闭环：每条控制指令（0x01~0x04/0x0A）下位机执行后回 ACK（0x80）确认；
	/// ACK 结果字节 = 对应指令所写旗帜位的当前值（直接镜像，语义见 CmdAck 注释）；
	/// 捕捉到一次确认即停止发送 —— ACK 到达时自动作废发送队列中该指令的待发重发副本，
	/// 避免延迟 ACK 场景下下位机重复执行。可用 SendCommandChecked 阻塞等待确认
	/// （超时自动重发）；周期反馈帧（0x81）可进一步交叉验证执行结果。
	/// </summary>
	public sealed class InfChange : IDisposable
	{
		// ===== 协议常量（与下位机 SDK/Mic_Sdk.c 简化版保持一致） =====
		public const byte FrameHead = 0x2F;        // 起始位（无帧尾）
		public const byte CmdSetRpm = 0x01;        // 设置转速：4 字节大端 RPM（完整值直接赋值）
		public const byte CmdQuerySetRpm = 0x0B;   // 查询 SetRpm：ACK 附带下位机当前 SetRpm（4 字节大端），不修改状态
		public const byte CmdStart = 0x02;         // 启动
		public const byte CmdStop = 0x03;          // 停止
		public const byte CmdSetDir = 0x04;        // 设置方向：1=正转 2=反转（运行中固件拒改，回显旧值）
		public const byte CmdHandshake = 0x07;    // 握手查询（仅端口探测用）：DATA=2字节随机数 nonce（大端）；
		                                          //   简化版固件无此指令——落默认分支仍回 ACK(结果=0)，探测据此命中
		public const byte CmdKey = 0x0A;         // 按键信号：DATA=按键类型(1)+按下状态(1)（1=脚踏TBAJ 2=面板ZXAJ 3=高低速GDQHAJ；0=松开 1=按下，写对应按键设置位）
		public const byte CmdAck = 0x80;          // 指令确认应答：DATA=被确认CMD(1)+执行结果(1)=对应指令所写旗帜位的当前值（直接镜像：
		                                           //   0x01→SetRpm_flag(1=已设置) 0x02→SoftwareRun_flag(1=运行) 0x03→MotorStop(1=已停止)
		                                           //   0x04→MotorDir(1=正转 2=反转) 0x0A→按键位电平；未知指令=0——
		                                           //   上位机拿"回显值 vs 下发值"交叉验证执行结果）
		public const byte CmdFeedback = 0x81;     // 综合反馈：未启动 LEN=2（软件运行位+ERR）/ 运行中 LEN=7（软件运行位+转速(3)+电流(2)+ERR）
		public const byte CmdHandshakeAck = 0x82; // 握手应答（旧完整版固件）：DATA="HDs2"(4)+固件版本(1)+nonce回显(2)
		private const int RxDataMax = 60;          // DATA 最大字节数（与下位机一致）
		// 注：0x05 电流 / 0x06 供电 / 0x08/09 转速加减已随缩小版固件裁剪（收到回 ACK=0 不执行），故不再提供
		public const int LinkBaudRate = 115200;    // RS232 波特率
		public const int ProbeTimeoutMs = 700;     // 主动握手后的应答等待窗口
		public const int ProbeListenMs = 300;      // 握手前的静默监听窗口（推送型固件在此期间命中）

		/// <summary>关键字识别规则（协议未命中的端口回退使用，主要识别脚踏）</summary>
		private readonly (string Keyword, InfDeviceType Type)[] _rules;

		private readonly Dispatcher? _dispatcher;
		private Timer? _pollTimer;
		private volatile bool _running;
		private readonly object _sync = new();

		// 已识别端口缓存：端口号 → 类型（会话内避免重复探测）
		private readonly Dictionary<string, InfDeviceType> _classified = new();

		// 航插持续监听链路
		private readonly object _linkLock = new();
		private SerialPort? _link;

		// 全双工 TX 通路：发送队列 + 专用发送线程（一帧一次原子写，多帧不交错）
		// 队列元素附带 CMD：收到该指令的 ACK 后可清除队列中未发出的同 CMD 重发副本
		private readonly ConcurrentQueue<(byte Cmd, byte[] Frame)> _txQueue = new();
		private readonly SemaphoreSlim _txSignal = new(0);
		private Thread? _txThread;
		private volatile bool _txRunning;
		private const int TxQueueMax = 32;   // 背压上限：超过即拒绝新指令（返回 false）

		// 指令确认闭环：待确认指令表（cmd → 等待者），ACK 到达时唤醒
		private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte>> _pendingAcks = new();

		// 当前状态
		private List<InfPortInfo> _ports = new();
		private string _lastKey = string.Empty;

		/// <summary>航插当前是否已接入（协议探测命中，对端为下位机）</summary>
		public bool AviationConnected { get; private set; }
		/// <summary>脚踏当前是否已接入（上位机串口关键字识别）</summary>
		public bool FootPedalConnected { get; private set; }
		/// <summary>最近一次识别的端口列表</summary>
		public IReadOnlyList<InfPortInfo> Ports => _ports;
		/// <summary>航插链路当前使用的端口号（未连接为 null）</summary>
		public string? LinkPortName { get; private set; }

		// ===== 事件（均在 UI 线程触发） =====
		/// <summary>航插接入状态变化</summary>
		public event Action<bool>? AviationConnectedChanged;
		/// <summary>脚踏接入状态变化</summary>
		public event Action<bool>? FootPedalConnectedChanged;
		/// <summary>端口列表或识别结果变化（参数为全量最新列表）</summary>
		public event Action<IReadOnlyList<InfPortInfo>>? PortsChanged;
		/// <summary>收到下位机综合反馈帧（校验通过后解析）</summary>
		public event Action<InfFeedback>? FeedbackReceived;
		/// <summary>收到下位机指令确认应答帧（每条控制指令执行后回送）</summary>
		public event Action<InfAck>? AckReceived;
		/// <summary>航插链路建立</summary>
		public event Action<string>? LinkOpened;
		/// <summary>航插链路断开</summary>
		public event Action<string>? LinkClosed;

		/// <summary>默认关键字规则（协议探测优先，此处主要兜底识别脚踏）</summary>
		public static readonly (string Keyword, InfDeviceType Type)[] DefaultRules =
		{
			// 脚踏：WCH CH340/CH341
			("VID_1A86", InfDeviceType.FootPedal),
			("CH340",    InfDeviceType.FootPedal),
			("CH341",    InfDeviceType.FootPedal),
			// 脚踏：名称匹配
			("脚踏",      InfDeviceType.FootPedal),
			("PEDAL",    InfDeviceType.FootPedal),
			// 航插：USB 转串口芯片（协议探测未命中时兜底）
			("VID_10C4", InfDeviceType.Aviation),
			("CP210",    InfDeviceType.Aviation),
			("SILABSER", InfDeviceType.Aviation),
			("VID_0403", InfDeviceType.Aviation),
			("FTDI",     InfDeviceType.Aviation),
			("FT232",    InfDeviceType.Aviation),
			("航插",      InfDeviceType.Aviation),
			("AVIATION", InfDeviceType.Aviation),
		};

		/// <param name="dispatcher">UI Dispatcher，事件将封送到该线程；null 则在调用线程触发</param>
		/// <param name="rules">自定义关键字规则；null 使用 DefaultRules</param>
		public InfChange(Dispatcher? dispatcher = null,
			IEnumerable<(string Keyword, InfDeviceType Type)>? rules = null)
		{
			SdkLog.Info("[InfChange] 构造函数已执行");   // 诊断：确认 inf_change.cs 代码真的在跑
			_dispatcher = dispatcher;
			_rules = rules?.ToArray() ?? DefaultRules;
		}

		// ============================================================
		// 协议编解码（公共静态，可独立使用）
		// ============================================================

		/// <summary>构造指令帧：起始位 + CMD + LEN + DATA（无 CRC 无帧尾，总长 len+3）</summary>
		public static byte[] BuildFrame(byte cmd, params byte[]? data)
		{
			int len = data?.Length ?? 0;
			var f = new byte[3 + len];
			f[0] = FrameHead;
			f[1] = cmd;
			f[2] = (byte)len;
			if (len > 0) data!.CopyTo(f, 3);
			return f;
		}

		/// <summary>校验完整帧：帧头 + LEN 合法 + 长度吻合（简化版无 CRC，与下位机一致）。
		/// 帧格式：head(0x2F)+cmd+len+data，总长=len+3</summary>
		public static bool ValidateFrame(ReadOnlySpan<byte> f)
		{
			if (f.Length < 3 || f[0] != FrameHead) return false;
			int len = f[2];
			return len <= RxDataMax && f.Length == len + 3;
		}

		/// <summary>
	/// 协议帧解析状态机（同下位机接收模型：按 LEN 定长组帧，
	/// DATA 收满即整帧——无 CRC 无帧尾；任何异常回 IDLE 重同步。
	/// 帧后若跟下位机诊断尾 0x55 0xAA，在 IDLE 态作噪声自然丢弃）
	/// </summary>
	public sealed class ProtocolParser
		{
			private readonly byte[] _buf = new byte[72];
			private readonly Action<byte[]> _onFrame;
			private int _idx;
			private int _len;
			private int _state; // 0=IDLE 1=CMD 2=LEN 3=DATA

			public ProtocolParser(Action<byte[]> onFrame) => _onFrame = onFrame;

			public void Feed(byte b)
			{
				switch (_state)
				{
					case 0: // 等起始位，其余字节丢弃（含诊断尾 55 AA 与调试文本）
						if (b == FrameHead) { _buf[0] = b; _idx = 1; _state = 1; }
						break;
					case 1: // CMD
						_buf[_idx++] = b;
						_state = 2;
						break;
					case 2: // LEN
						_buf[_idx++] = b;
						_len = b;
						if (_len > RxDataMax) Reset();          // 超长非法，重同步
						else if (_len == 0) Emit();             // 无数据区：帧完整
						else _state = 3;
						break;
					case 3: // DATA（收满即完整帧）
						_buf[_idx++] = b;
						if (_idx == 3 + _len) Emit();
						break;
					default:
						Reset();
						break;
				}
			}

			private void Emit()
			{
				var frame = new byte[_idx];
				Array.Copy(_buf, frame, _idx);
				_onFrame(frame);
				Reset();
			}

			private void Reset()
			{
				_state = 0;
				_idx = 0;
				_len = 0;
			}
		}

		// ============================================================
		// 轮询识别
		// ============================================================

		/// <summary>开始轮询监视（默认 2 秒一次，首次立即执行）</summary>
		public void Start(int pollMs = 2000)
		{
			SdkLog.Info($"[Start] 进入 Start(_running={_running})");   // 确认入口与当前状态
			lock (_sync)
			{
				if (_running) return;
				_running = true;
				SdkLog.Info($"[InfChange] 启动轮询 pollMs={pollMs} 波特率={LinkBaudRate} 8N1");
				_pollTimer = new Timer(_ => Poll(), null, 0, Math.Max(200, pollMs));
			}
		}

		/// <summary>停止轮询并断开链路</summary>
		public void Stop()
		{
			lock (_sync)
			{
				_running = false;
				_pollTimer?.Dispose();
				_pollTimer = null;
			}
			CloseLink();
		}

		public void Dispose() => Stop();

		/// <summary>轮询回调：枚举 → 协议探测 → 维护链路 → 状态变化触发事件</summary>
		private void Poll()
		{
			if (!_running) return;
			try
			{
				var raw = Scan();

				// 1. 清理已拔出的端口缓存；航插拔出时断开链路
				foreach (var gone in _classified.Keys
					.Where(p => raw.All(r => !string.Equals(r.PortName, p, StringComparison.OrdinalIgnoreCase)))
					.ToList())
				{
					_classified.Remove(gone);
					if (string.Equals(gone, LinkPortName, StringComparison.OrdinalIgnoreCase))
						CloseLink();
				}

				// 2. 新接入端口：先协议探测（下位机会周期推送反馈帧），命中即航插；
				//    未命中回退关键字识别（脚踏/未知）。结果缓存避免重复探测。
				foreach (var p in raw.Where(r => !_classified.ContainsKey(r.PortName)))
				{
					_classified[p.PortName] = ProbePort(p.PortName)
						? InfDeviceType.Aviation
						: p.Type;
				}

				// 3. 汇总识别结果（协议命中 = 下位机航插链路 → Lower；其余 = 上位机外设 → Upper）
				var latest = raw.Select(p =>
				{
					var t = _classified.TryGetValue(p.PortName, out var v) ? v : InfDeviceType.Unknown;
					return new InfPortInfo
					{
						PortName = p.PortName,
						Type = t,
						Description = p.Description,
						PnpDeviceId = p.PnpDeviceId,
						Host = t == InfDeviceType.Aviation ? InfHost.Lower : InfHost.Upper,
					};
				}).ToList();

				bool aviation = latest.Any(x => x.Type == InfDeviceType.Aviation);
				bool pedal = latest.Any(x => x.Type == InfDeviceType.FootPedal);

				// 扫描汇总日志：端口与识别结果（便于确认 Control 是否发现、接入下位机）
				SdkLog.Info($"[扫描] 端口=[{string.Join(",", latest.Select(p => $"{p.PortName}:{p.Type}"))}] 航插={aviation} 脚踏={pedal}");

				// 4. 航插在线但链路未建立 → 建立持续监听
				if (aviation && _link == null)
				{
					var port = latest.First(x => x.Type == InfDeviceType.Aviation).PortName;
					OpenLink(port);
				}

				// 5. 状态变化 → 封送事件
				string key = string.Join("|", latest.Select(p => $"{p.PortName}:{(int)p.Type}"));
				if (key == _lastKey && aviation == AviationConnected && pedal == FootPedalConnected)
					return;

				var oldAviation = AviationConnected;
				var oldPedal = FootPedalConnected;
				_ports = latest;
				_lastKey = key;
				AviationConnected = aviation;
				FootPedalConnected = pedal;

				Raise(() =>
				{
					PortsChanged?.Invoke(latest);
					if (aviation != oldAviation) AviationConnectedChanged?.Invoke(aviation);
					if (pedal != oldPedal) FootPedalConnectedChanged?.Invoke(pedal);
				});
			}
			catch (Exception ex)
			{
				// 单次轮询失败不再静默吞掉：打日志便于定位（如 WMI 慢/异常、端口枚举失败等）
				SdkLog.Info($"[Poll] 轮询异常 {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>封送事件到 UI 线程</summary>
		private void Raise(Action action)
		{
			if (_dispatcher != null) _dispatcher.BeginInvoke(action);
			else action();
		}

		// ============================================================
	// 协议探测（两阶段，兼容两类下位机固件）：
	//   阶段1 静默监听 ProbeListenMs：推送型固件周期发 0x81 反馈帧，
	//        收到合法反馈帧即命中（只收不发，绝不误触发下位机动作）；
	//   阶段2 主动握手：发送 0x07 握手查询（DATA=2字节随机数 nonce）——
	//        简化版固件无此指令，落默认分支仍回 ACK(0x80) 即命中；
	//        旧静默固件回 0x82 应答，核对 "HDs2" 魔数 + nonce 回显
	//        全部匹配才命中（随机数回显，其他设备不可能伪造应答）。
	// ============================================================
		private ushort _probeNonce;

		private bool ProbePort(string portName)
		{
			bool hit = false;
			try
			{
				using var sp = new SerialPort(portName, LinkBaudRate, Parity.None, 8, StopBits.One)
				{ ReadTimeout = 120, WriteTimeout = 120 };
				var parser = new ProtocolParser(frame =>
				{
					if (!ValidateFrame(frame))
					{
						// 验证失败：多为波特率不匹配或线路噪声
						SdkLog.Info($"[探测] {portName} 无效帧(校验失败) {SdkLog.Hex(frame)}");
						return;
					}
					byte cmd = frame[1];
					SdkLog.Info($"[探测] {portName} 收到帧 cmd=0x{cmd:X2} {SdkLog.Hex(frame)}");
					if (cmd == CmdFeedback) hit = true;                                       // 推送型固件（心跳）
					else if (cmd == CmdAck) hit = true;                                       // 简化版固件：任何指令必回 ACK
					else if (cmd == CmdHandshakeAck && MatchHandshakeAck(frame)) hit = true;  // 旧静默固件
				});
				sp.Open();

				// 阶段1：静默监听（推送型固件在此命中）
				long listen = Environment.TickCount64 + ProbeListenMs;
				while (!hit && Environment.TickCount64 < listen)
					FeedPort(sp, parser);

				// 阶段2：主动握手，发 0x07 后等应答。
				// 新固件 rx_poll 有数据即解析（无需攒满缓冲），单帧即达：
				//   简化版固件无 0x07 指令——落默认分支仍回 ACK(0x80)；
				//   旧静默固件回 0x82 握手应答（核对魔数+nonce）
				if (!hit)
				{
					_probeNonce = (ushort)Random.Shared.Next(1, 0x10000);
					try
					{
						var hs = BuildFrame(CmdHandshake, (byte)(_probeNonce >> 8), (byte)(_probeNonce & 0xFF));
						sp.Write(hs, 0, hs.Length);
					}
					catch { /* 写失败（端口异常）按未命中处理 */ }
					long deadline = Environment.TickCount64 + ProbeTimeoutMs;
					while (!hit && Environment.TickCount64 < deadline)
						FeedPort(sp, parser);
				}
				sp.Close();
			}
			catch
			{
				// 端口打不开（被占用/无权限）视为非目标设备
				SdkLog.Info($"[探测] {portName} 端口打不开（被占用/无权限）");
			}
			SdkLog.Info($"[探测] {portName} 结果={(hit ? "命中(下位机航插)" : "未命中")} 波特率={LinkBaudRate}");
			return hit;
		}

		/// <summary>从串口读回所有可用字节喂解析器（非阻塞批量读，同 serial.c Serial_ReciveData）</summary>
		private static void FeedPort(SerialPort sp, ProtocolParser parser)
		{
			try
			{
				int n = sp.BytesToRead;
				if (n <= 0) return;
				byte[] buf = new byte[n];
				int read = sp.Read(buf, 0, n);
				for (int i = 0; i < read; i++)
					parser.Feed(buf[i]);
			}
			catch (TimeoutException) { /* 窗口内继续等 */ }
			catch (InvalidOperationException) { /* 端口已关闭 */ }
			catch (System.IO.IOException) { /* 读取异常忽略 */ }
		}

		/// <summary>核对握手应答：DATA = "HDs2"魔数(4) + 固件版本(1) + nonce回显(2大端)</summary>
		private bool MatchHandshakeAck(byte[] frame)
		{
			if (frame[2] < 7) return false;
			return frame[3] == (byte)'H' && frame[4] == (byte)'D' && frame[5] == (byte)'s' && frame[6] == (byte)'2'
				&& (ushort)((frame[8] << 8) | frame[9]) == _probeNonce;
		}

		// ============================================================
		// 航插持续监听链路与指令发送
		// ============================================================

		/// <summary>建立航插链路：持续接收并解析反馈帧</summary>
		private void OpenLink(string portName)
		{
			lock (_linkLock)
			{
				CloseLinkCore();
				try
				{
					var sp = new SerialPort(portName, LinkBaudRate, Parity.None, 8, StopBits.One)
				{ ReadTimeout = 50, WriteTimeout = 200 };
				var parser = new ProtocolParser(OnLinkFrame);
				var parserLock = new object();   // 独立于 _linkLock：Close 等待处理器返回时不会死锁
				sp.DataReceived += (s, e) =>
				{
					lock (parserLock)
					{
						// 非阻塞批量读：一次读回所有可用字节喂给解析器（同 serial.c Serial_ReciveData）
						try
						{
							int n = sp.BytesToRead;
							if (n <= 0) return;
							byte[] buf = new byte[n];
							int read = sp.Read(buf, 0, n);
							for (int i = 0; i < read; i++)
								parser.Feed(buf[i]);
						}
						catch { /* 端口关闭/读取异常忽略 */ }
					}
				};
					sp.Open();
					_link = sp;
					LinkPortName = portName;
					SdkLog.Info($"[链路] 已建立 {portName} 波特率={LinkBaudRate} 等待下位机反馈...");
					StartTxThread();   // 全双工 TX 通路：专用发送线程
					Raise(() => LinkOpened?.Invoke(portName));
				}
				catch
				{
					SdkLog.Info($"[链路] {portName} 打开失败");
					CloseLinkCore();
				}
			}
		}

		/// <summary>断开航插链路</summary>
		public void CloseLink()
		{
			lock (_linkLock) CloseLinkCore();
		}

		/// <summary>
		/// 强制把指定串口当下位机链路打开（绕过识别）。
		/// 用于已知下位机端口的场景：直接 OpenLink 建立监听并解析 0x81 反馈。
		/// 返回 true 表示已建立链路（LinkPortName 非空）。
		/// </summary>
		public bool ForceOpenLink(string portName)
		{
			OpenLink(portName);
			bool ok = LinkPortName != null;
			SdkLog.Info($"[强制] {portName} 打开={(ok ? "成功" : "失败")}");
			return ok;
		}

		private void CloseLinkCore()
		{
			if (_link == null) return;
			StopTxThread();   // 先停发送线程（等待在途帧写完），再关串口
			try { _link.Close(); } catch { /* 忽略关闭异常 */ }
			_link.Dispose();
			_link = null;
			var port = LinkPortName;
			LinkPortName = null;
			if (port != null)
			{
				SdkLog.Info($"[链路] {port} 已断开");
				Raise(() => LinkClosed?.Invoke(port));
			}
		}

		/// <summary>链路收到完整帧：校验并解析 ACK（0x80）/ 综合反馈（0x81）</summary>
	private void OnLinkFrame(byte[] frame)
	{
		if (!ValidateFrame(frame))
		{
			// 长度/帧头校验失败：多为波特率不匹配或线路噪声（按原始字节记录，便于定位）
			SdkLog.Info($"[坏帧] 帧头/长度校验失败 {SdkLog.Hex(frame)}");
			return;
		}
		if (frame[1] == CmdFeedback)
		{
			// 当前固件（SDK/Mic_Sdk.c）两种布局（帧长依据=MotorRun 电机实际运行）：
			//   LEN=2 短心跳（MotorRun=0）: SoftwareRun(1) + ERR(1)（停止省流量）
			//   LEN=7 全量帧（MotorRun=1）: SoftwareRun(1) + Rpm(3) + 电流(2) + ERR(1)
			// 运行状态看帧长（长帧=运行中/短帧=停止），SoftwareRun 仅是联机握手位
			// （0x03 停止不清除；脚踏启动时=0 但电机在转——不能用其判运行态）
			InfFeedback fb;
			if (frame[2] == 2)
			{
				fb = new InfFeedback
				{
					SoftwareRun = frame[3],
					SetRpm = 0,   // 短心跳无转速/电流数据
					Rpm = 0,
					CurrentMa = 0,
					Status = frame[4] != 0 ? (byte)2 : (byte)0,   // 短帧=停止：ERR≠0→故障，否则空闲
					Alarm = frame[4],
				};
			}
			else if (frame[2] == 7)
			{
				fb = new InfFeedback
				{
					SoftwareRun = frame[3],
					SetRpm = 0,   // 新帧无 SetRpm（待机显示回退本地值）
					Rpm = ((uint)frame[4] << 16) | ((uint)frame[5] << 8) | frame[6],
					CurrentMa = (ushort)((frame[7] << 8) | frame[8]),
					Status = frame[9] != 0 ? (byte)2 : (byte)1,   // 长帧=MotorRun=1 运行中：ERR≠0→故障，否则运行
					Alarm = frame[9],
				};
			}
			else return;   // 其他 LEN：忽略
			// 持续打印下位机每帧内容（联调时可监控；按需可再改成"变化才打印"）
			SdkLog.Info($"[反馈0x81] 软件运行={fb.SoftwareRun} SetRpm={fb.SetRpm} Rpm={fb.Rpm} 电流={fb.CurrentMa}mA 状态={fb.Status} 报警={fb.Alarm}  帧={SdkLog.Hex(frame)}");
			Raise(() => FeedbackReceived?.Invoke(fb));
		}
		else if (frame[1] == CmdAck && frame[2] >= 2)
		{
			// 0x0B 查询 SetRpm：下位机 ACK 附带当前实际存储 SetRpm（4 字节大端，LEN=6）；
			// 其余指令 ACK 仅 CMD+Result（LEN=2），SetRpm=0
			uint setRpm = 0;
			if (frame[3] == CmdQuerySetRpm && frame[2] >= 6)
				setRpm = ((uint)frame[5] << 24) | ((uint)frame[6] << 16) | ((uint)frame[7] << 8) | frame[8];
			var ack = new InfAck { Cmd = frame[3], Result = frame[4], SetRpm = setRpm };
			SdkLog.Info($"[应答0x80] 确认指令=0x{ack.Cmd:X2} 结果={ack.Result} SetRpm={ack.SetRpm}");
			// 捕捉到一次确认即停止发送：作废队列中该指令的待发重发副本
			PurgePendingTx(ack.Cmd);
			// 唤醒等待者（接收线程同步完成），再抛事件
			if (_pendingAcks.TryRemove(ack.Cmd, out var tcs))
				tcs.TrySetResult(ack.Result);
			Raise(() => AckReceived?.Invoke(ack));
		}
	}

		// ============================================================
		// 全双工 TX 通路：发送队列 + 专用发送线程
		// 调用方（UI/任意线程）只入队，O(1) 非阻塞；发送线程串行写串口，
		// 一帧一次原子写（多线程并发发送也不会字节交错）；与 RX 通路零锁冲突。
		// ============================================================

		/// <summary>发送指令帧（非阻塞入队；链路未建立或队列超限返回 false）</summary>
		public bool SendCommand(byte cmd, params byte[]? data)
		{
			if (_link?.IsOpen != true) { SdkLog.Info($"[发送] 指令=0x{cmd:X2} 失败：链路未建立"); return false; }
			if (_txQueue.Count >= TxQueueMax) { SdkLog.Info($"[发送] 指令=0x{cmd:X2} 失败：队列超限({_txQueue.Count})"); return false; }
			var frame = BuildFrame(cmd, data);
			_txQueue.Enqueue((cmd, frame));
			_txSignal.Release();
			SdkLog.Info($"[发送] 指令=0x{cmd:X2} 入队成功 队列={_txQueue.Count} 帧={SdkLog.Hex(frame)}");
			return true;
		}

		/// <summary>
		/// 收到某指令的 ACK 后，清除发送队列中该指令的待发帧：
		/// 捕捉到一次确认即停止继续发送（超时重发的副本若尚未写出，直接作废，
		/// 避免下位机重复执行）。ConcurrentQueue 逐项原子出队，过滤后保序回填。
		/// </summary>
		private void PurgePendingTx(byte cmd)
		{
			if (_txQueue.IsEmpty) { SdkLog.Info($"[Purge] ACK 0x{cmd:X2} 队列已空"); return; }
			var keep = new List<(byte, byte[])>();
			while (_txQueue.TryDequeue(out var item))
				if (item.Cmd != cmd) keep.Add(item);
			SdkLog.Info($"[Purge] ACK 0x{cmd:X2} 过滤后保留={keep.Count} 原队列={keep.Count + (_txQueue.IsEmpty ? 0 : 0)}");
			if (keep.Count == 0) return;
			foreach (var item in keep) _txQueue.Enqueue(item);
			_txSignal.Release();   // 保序回填后唤醒发送线程处理剩余帧
		}

		// ============================================================
		// 指令确认闭环（ACK 机制）
		// 发送后等待下位机 ACK（0x80）；超时自动重发，重试耗尽返回 null。
		// 注意：同一指令不可并发等待（按 CMD 键控），UI 单线程调用天然满足。
		// ============================================================

		/// <summary>发送指令并等待下位机确认（同步阻塞版；勿在 UI 线程调用）</summary>
		/// <returns>确认结果；null=链路断开/发送失败/重试耗尽无应答</returns>
		public InfAck? SendCommandChecked(byte cmd, byte[]? data = null,
			int timeoutMs = 500, int retries = 2)
			=> SendCommandCheckedAsync(cmd, data, timeoutMs, retries).GetAwaiter().GetResult();

		/// <summary>发送指令并等待下位机确认（异步版；超时自动重发）</summary>
		/// <returns>确认结果；null=链路断开/发送失败/重试耗尽无应答</returns>
		public async Task<InfAck?> SendCommandCheckedAsync(byte cmd, byte[]? data = null,
			int timeoutMs = 500, int retries = 2)
		{
			if (_link?.IsOpen != true) return null;
			var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
			_pendingAcks[cmd] = tcs;
			try
			{
				for (int attempt = 0; attempt <= retries; attempt++)
				{
					if (!SendCommand(cmd, data)) return null;   // 链路失效/背压
					var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
					if (done == tcs.Task)
						return new InfAck { Cmd = cmd, Result = tcs.Task.Result };
					// 超时：重发（可能是帧丢失或对端忙）
				}
				return null;   // 重试耗尽仍无 ACK
			}
			finally
			{
				_pendingAcks.TryRemove(cmd, out _);
			}
		}

		/// <summary>启动发送线程（OpenLink 成功后调用）</summary>
		private void StartTxThread()
		{
			StopTxThread();
			_txQueue.Clear();
			_txRunning = true;
			_txThread = new Thread(TxLoop) { IsBackground = true, Name = "InfChange.TxLoop" };
			_txThread.Start();
		}

		/// <summary>停止发送线程并清空队列（CloseLink 前调用，等待在途帧写完）</summary>
		private void StopTxThread()
		{
			_txRunning = false;
			_txSignal.Release();   // 唤醒线程使其观察到退出标志
			_txThread?.Join(500);
			_txThread = null;
			_txQueue.Clear();
		}

		/// <summary>发送线程主循环：等待信号 → 逐帧写出；链路异常清空余量帧</summary>
		private void TxLoop()
		{
			while (_txRunning)
			{
				try { if (!_txSignal.Wait(200)) continue; }   // 周期超时兜底退出检查
			catch { break; }
			while (_txQueue.TryDequeue(out var item))
			{
				var sp = _link;
				if (sp is not { IsOpen: true })
				{
					SdkLog.Info($"[TX] 链路已断，清空队列");
					_txQueue.Clear();   // 链路已断：丢弃余量帧
					break;
				}
				try
				{
					sp.Write(item.Frame, 0, item.Frame.Length);
					SdkLog.Info($"[TX] 指令=0x{item.Cmd:X2} 已发出 帧={SdkLog.Hex(item.Frame)} 剩余队列={_txQueue.Count}");
				}
				catch
				{
					SdkLog.Info($"[TX] 指令=0x{item.Cmd:X2} 写失败，清空队列");
					_txQueue.Clear();   // 写失败（拔出/占用）：丢弃，等待链路重建
					break;
				}
			}
			}
		}

		/// <summary>转速参数编码为 4 字节大端（32位完整值，与下位机 SetRpm 直接赋值对应）</summary>
	public static byte[] RpmToBytes(uint rpm) =>
		new[] { (byte)((rpm >> 24) & 0xFF), (byte)((rpm >> 16) & 0xFF), (byte)((rpm >> 8) & 0xFF), (byte)(rpm & 0xFF) };

		/// <summary>设置转速（RPM）</summary>
		public bool SetRpm(uint rpm) => SendCommand(CmdSetRpm, RpmToBytes(rpm));
		/// <summary>启动</summary>
		public bool Start() => SendCommand(CmdStart);
		/// <summary>停止</summary>
		public bool StopMotor() => SendCommand(CmdStop);
		/// <summary>设置方向：true=正转(1) false=反转(2)</summary>
		public bool SetDirection(bool forward) => SendCommand(CmdSetDir, (byte)(forward ? 1 : 2));
		/// <summary>按键信号（0x0A）：模拟下位机原机按键按下/松开（写对应按键设置位）
		/// key：1=脚踏 TBAJ 2=面板 ZXAJ 3=高低速 GDQHAJ；pressed：true=按下(1) false=松开(0)</summary>
		public bool SendKey(byte key, bool pressed) => SendCommand(CmdKey, key, (byte)(pressed ? 1 : 0));

		// ============================================================
		// 端口枚举（WMI 主路径 + 注册表回退）
		// ============================================================

		/// <summary>立即扫描一次所有串口（关键字初分类，探测在轮询中补充）</summary>
		public List<InfPortInfo> Scan()
		{
			var list = QueryByWmi();
			if (list.Count == 0) list = QueryByRegistry();
			return list;
		}

		/// <summary>WMI 查询：枚举 PnP 串口设备（Caption 形如 "USB-SERIAL CH340 (COM3)"）</summary>
		private List<InfPortInfo> QueryByWmi()
		{
			var list = new List<InfPortInfo>();
			try
			{
				using var searcher = new ManagementObjectSearcher(
					"SELECT Caption, Description, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports' OR Caption LIKE '%(COM%'");
				using var results = searcher.Get();
				foreach (ManagementObject mo in results)
				{
					string caption = mo["Caption"]?.ToString() ?? string.Empty;
					string desc = mo["Description"]?.ToString() ?? string.Empty;
					string pnpId = mo["PNPDeviceID"]?.ToString() ?? string.Empty;

					// 从 Caption 尾部提取 "(COMx)"
					int s = caption.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
					if (s < 0) continue;
					int e = caption.IndexOf(')', s);
					if (e < 0) continue;
					string port = caption.Substring(s + 1, e - s - 1);
					if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;

					var type = MatchType($"{caption} {desc} {pnpId}");
					list.Add(new InfPortInfo
					{
						PortName = port.ToUpperInvariant(),
						Type = type,
						Description = caption,
						PnpDeviceId = pnpId,
					});
				}
			}
			catch
			{
				// WMI 不可用时由注册表回退
			}
			return list;
		}

		/// <summary>注册表回退：SERIALCOMM 键枚举串口（设备路径参与识别）</summary>
		private List<InfPortInfo> QueryByRegistry()
		{
			var list = new List<InfPortInfo>();
			try
			{
				using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
				if (key == null) return list;
				foreach (var name in key.GetValueNames())
				{
					if (key.GetValue(name) is not string port || string.IsNullOrEmpty(port)) continue;
					// 设备路径如 \Device\Silabser0 / \Device\VCP0 / \Device\USBSER000
					var type = MatchType(name);
					list.Add(new InfPortInfo { PortName = port, Type = type, Description = name });
				}
			}
			catch
			{
				// 注册表读取失败返回空列表
			}
			return list;
		}

		/// <summary>按关键字匹配设备类型（不区分大小写）</summary>
		private InfDeviceType MatchType(string text)
		{
			foreach (var (keyword, type) in _rules)
			{
				if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
					return type;
			}
			return InfDeviceType.Unknown;
		}
	}
}
