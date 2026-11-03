using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;

namespace HDs2_SDK
{
	public partial class MainWindow : Window
	{
		// 状态枚举（3状态灯：待机/高速/低速；运行/过载/故障通过状态条联动）
	private enum MotorStatus { Standby, HighSpeed, LowSpeed, Overload, Fault }
	private MotorStatus _status = MotorStatus.Standby;

	// 全局变量：默认转速——与下位机上电默认 SetRpm（=0）保持一致
	public const double DefaultSpeed = 0;
	// 运行状态
	private bool _isRunning = false;
	private bool _isForward = true;  // true=正转(CW), false=反转(CCW)
	// 脚踏=控制开关：位置由下位机实测转速判定（Rpm>0 且无报警=踩住运行中），
	// 心跳 TBAJ 位因固件调试文本吞尾可能被污染，不作为脚踏/运行依据
	// 运行判据：Rpm>0 持续 RpmRunHoldMs 升运行；Rpm=0 不升也不降（不动作）——
	//   ① 不升：本固件未启动（SoftwareRun=0/Rpm=0）也发长帧，帧类型判据会误判运行
	//      （长帧间隔 1.17s > 保鲜 1s → 运行↔停止 每 1.17s 翻转 = 频闪）；
	//   ② 不降：低速测速层丢脉冲 Rpm 偶发 0，若立即降会 0↔真实值 频闪；
	//   真正停止（固件改发短帧、无任何 Rpm>0 帧）由 CheckDataFreshness 保鲜兜底复位
	private long _rpmPositiveStartTick = 0;    // 正判起点：零→Rpm>0 切换时设，持续超 RpmRunHoldMs 升运行
	private const long RpmRunHoldMs = 50;      // 正判去抖窗口
	// 有效转速数据保鲜：超过该时长未收到"运行证据"（Rpm>0 帧/寄存器#11 非零值/脚踏踩住短帧）
	// 时强制数码管回 0 并复位运行态。时长必须 > 寄存器#11 轮询周期(3×200ms=600ms)：
	// 若 ≤600ms 会在两次 #11 之间提前复位，运行态↔停止态来回切换（频闪+设置按钮抖动）
	private long _lastValidDataTick = 0;
	private const long RpmDataTimeoutMs = 1000;
	// 当前报警码（心跳帧 Alarm 写入，UpdateDisplay 据此显示 E 码）
	private byte _alarmCode = 0;
	// 上下位机通讯监测：收到任何协议帧/ACK 刷新 _lastRxTick；
	// 链路未建立（LinkAlive=false）或链路在但超 NoCommTimeoutMs 无帧 = 未通讯，
	// 数码管显示 E100 提示（_linkLost 防重复进入）
	private long _lastRxTick = 0;
	private bool _linkLost = false;
	private const long NoCommTimeoutMs = 2000;
	// 寄存器长轮询：每间隔发 0x0C 读关键字段（编号：3=MotorRun 7=TBAJ 10=SetRpm 11=Rpm），
	// 上位机持续发即可持续读取结构体字段（覆盖心跳帧之外的状态查询）
	private System.Windows.Threading.DispatcherTimer? _regPollTimer;
	private const double RegPollIntervalMs = 200;    // 寄存器长轮询间隔：每 200ms 发一次 0x0C
	// 长轮询的字段编号队列（每次 tick 轮转一个，避免打满总线）
	// 11=Rpm 实时转速（运行判据 + 数码管，可靠 ACK 源）、
	// 14=ERR 报警码（上位机主动探测报警——下位机报警态心跳 ~2.6s/帧太慢，
	//    由寄存器轮询 200ms 主动查，报警显示反应不受心跳周期限制）、
	// 5=MotorDir 方向（只读显示下位机实际方向，上位机无法设置）
	private readonly byte[] _pollRegs = { 11, 14, 5 };
	private int _pollRegIdx = 0;
	public double _targetSpeed = DefaultSpeed;  // 全局目标转速
	private double _currentSpeed = 0;
	private bool _hasAlarm = false;  // 报警态：Alarm!=0 时显示 E+报警码 且禁止启动
	private uint _lastSetRpm = 0;    // 下位机传回的 SetRpm（待机/设置显示用；无数据=0）
	// 下位机状态闭环同步：新架构下电机启停由下位机实体脚踏自动控制，
	// 上位机不主动发启停指令，也不维护期望运行/补发/宽限期——运行态
	// 完全由 0x81 心跳帧 Status 驱动显示（SyncRunStateFromFeedback）
	// 当前显示动画计时器（反馈帧接管显示前需停掉，防动画与反馈互相覆盖）
	private System.Windows.Threading.DispatcherTimer? _animTimer;
	// 变化检测（避免下位机每帧上报刷屏：只在值变化时打印一次）
	private uint _lastLogSetRpm = uint.MaxValue;
	private uint _lastLogRpm = uint.MaxValue;
	private byte _lastLogSoftRun = byte.MaxValue;
	private byte _lastLogStatus = byte.MaxValue;
	private byte _lastLogAlarm = byte.MaxValue;
	private int _lastLogAlarmCode = -1;
	private int _lastLogDigits = -1;
	// 设置面板：转速直接输入（PopupTxtSpeed 文本框）
	private double _popupSpeed = DefaultSpeed;
	// 面板拖动
	private bool _isPopupDragging = false;
	private Point _popupDragStart;
	private double _popupStartX;
	private double _popupStartY;
	// 端口识别（航插/脚踏热插拔监视）
	private Control.InfChange? _infChange;
	// 下位机链路是否已建立（用于真实反馈/指令与本地模拟的切换）
	private bool LinkAlive => _infChange?.LinkPortName != null;

	public MainWindow()
	{
		InitializeComponent();
		// 待机不在字典中（永远无高亮框）
		SetStatus(MotorStatus.Standby);
		SetDigits(0);   // 主界面进入默认 00000（实际转速 0；有实测值后由反馈刷新）
		SourceInitialized += MainWindow_SourceInitialized;
		Loaded += MainWindow_Loaded;
		// Escape 关闭设置面板
		PreviewKeyDown += (s, e) =>
		{
			if (e.Key == System.Windows.Input.Key.Escape && SettingsPopup.IsOpen)
				CloseSettingsPanel();
		};
	}

	private void OpenSettingsPanel()
	{
		SettingsPopup.HorizontalOffset = 0;
		SettingsPopup.VerticalOffset = 0;
		MainPanel.IsHitTestVisible = false;  // 禁用主面板所有交互
		MainPanel.Opacity = 0.4;             // 主面板变暗（蒙版效果）
		SettingsPopup.IsOpen = true;
		// 打开设置：复用下位机 0x02 原本机制置 SoftwareRun_flag=1（联机握手位）
		if (LinkAlive)
		{
			_infChange?.SendCommand(Control.InfChange.CmdStart);
			Control.SdkLog.Info("[设置] 打开面板 → 0x02 置 SoftwareRun_flag=1");
		}
	}

	private void CloseSettingsPanel()
	{
		SettingsPopup.IsOpen = false;
		MainPanel.IsHitTestVisible = true;   // 恢复主面板交互
		MainPanel.Opacity = 1.0;              // 恢复主面板亮度
		// 退出设置：复用下位机 0x03 DATA=1（断开语义）清 SoftwareRun_flag=0
		if (LinkAlive)
		{
			_infChange?.SendCommand(Control.InfChange.CmdStop, (byte)1);
			Control.SdkLog.Info("[设置] 退出面板 → 0x03 DATA=1 清 SoftwareRun_flag=0");
		}
	}

	/// <summary>运行中禁用设置按钮（置灰），停止后恢复（参考 v0.6.0-beta SetInputEnabled）</summary>
	private void SetInputEnabled(bool enabled)
	{
		BtnSetSpeed.IsEnabled = enabled;
	}

	/// <summary>设置面板点击：阻止事件冒泡到主 Grid，防止触发 DragMove</summary>
	private void SettingsPanel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		e.Handled = true;
	}

		private void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		// 模板应用后初始化图标
		BtnPower.ApplyTemplate();
		UpdatePowerIcon();
		// 启动后置顶显示（最前化）：控制面板常驻前台，方便边调试边观察
		Activate();          // 激活窗口（抢焦点）
		Topmost = true;      // 永远置顶
		// 启动端口识别（建立与下位机的通信链路，供后续协议反馈/指令使用）
		_infChange = new Control.InfChange(Dispatcher);
		_infChange.FeedbackReceived += OnFeedbackReceived;
		_infChange.AckReceived += OnAckReceived;
		_infChange.LinkOpened += OnLinkOpened;
		_infChange.Start(2000);   // 显式传参避免与 bool Start()（电机启动指令）重载冲突
		// 寄存器长轮询：无条件持续发 0x0C 轮转读 7=TBAJ / 11=Rpm——
		// #7 需常发才能兜底（心跳 TBAJ 可能被吞尾污染，ACK 帧可靠）；
		// #11 持续读实时转速驱动主界面数码管（停止态显示 0/保持，见 case 11）
		_regPollTimer = new System.Windows.Threading.DispatcherTimer
		{ Interval = TimeSpan.FromMilliseconds(RegPollIntervalMs) };
		_regPollTimer.Tick += (s, e) =>
		{
			CheckCommStatus();       // 通讯监测：未通讯显示 E100
			CheckDataFreshness();   // 数据保鲜：有效转速长时间缺席 → 数码管强制回 0
			if (LinkAlive && _infChange != null)
			{
				byte reg = _pollRegs[_pollRegIdx];
				_pollRegIdx = (_pollRegIdx + 1) % _pollRegs.Length;
				_infChange.SendCommand(Control.InfChange.CmdRegRead, reg);
			}
		};
		_regPollTimer.Start();
		Control.SdkLog.Info($"[App] 端口识别启动，波特率={Control.InfChange.LinkBaudRate}，目标转速={_targetSpeed}");
		// 自动识别：InfChange.Start() 轮询扫描 → 探测下位机(航插) → 命中即自动建链路；
		// 无需硬编码端口（ForceOpenLink 仅作兜底/备用，可据此注释按需启用）
	}

	protected override void OnClosed(EventArgs e)
	{
		// 关闭前恢复下位机状态（连接状态通知）：
		// 0x03 DATA=1（断开语义）→ 下位机 SoftwareRun_flag 清 0
		// （状态位归0，心跳首位回 0=已断开）；不停电机（电机启停
		// 由下位机硬件自循环控制）——上位机退出即"回复为 0"，
		// 下次程序启动 0x02 重新置 1（重新握手）。
		// 链路已断则 SendCommand 内部直接返回
		try
		{
			_infChange?.SendCommand(Control.InfChange.CmdStop, (byte)1);
		}
		catch { /* 关闭流程吞异常：链路可能已断，尽力而为 */ }
		_infChange?.Dispose();
		_infChange = null;
		base.OnClosed(e);
	}

	// ===== 下位机协议集成（对应 SDK/Mic_Sdk.c 简化版，指令下发 + 反馈接收） =====
	/// <summary>
	/// 发送指令并等待下位机 ACK 确认（超时自动重发）。
	/// 新固件（SDK/Mic_Sdk.c）rx_poll 有数据即解析——单帧即处理即应答，
	/// 无需旧版的连发填缓冲（攒满触发已废弃）。
	/// </summary>
	private async Task<Control.InfAck?> SendCheckedAsync(byte cmd, params byte[]? data)
	{
		var inf = _infChange;
		if (inf == null || inf.LinkPortName == null) return null;
		return await inf.SendCommandCheckedAsync(cmd, data, 1000, 1);  // 超时 1s 重发 1 次
	}

	/// <summary>链路建立回调（阶段1·握手验证）：
	/// 不主动发任何指令（既不发 0x03 也不发 0x02）：
	///   0x03 会把下位机已有运行态（脚踏启动/上电残留）打停，违背
	///   "下位机显示运行时上位机同步显示启动"——反馈帧是显示的唯一
	///   权威数据源，建链后第一个 0x81 心跳帧（≈10ms）即驱动 UI 同步：
	///   长帧(Status=1)进入启动态，短帧(Status=0)回待机；
	///   电机启停由下位机实体脚踏自动控制，上位机不主动发启停指令</summary>
	private void OnLinkOpened(string portName)
	{
		// 建链宽限期：探测阶段命中的帧不刷新 _lastRxTick，这里手动置为当前时刻，
		// 避免建链后首帧未到就判未通讯；若 2s 内仍无任何帧则转 E100
		_lastRxTick = Environment.TickCount64;
		Control.SdkLog.Info($"[链路] {portName} 已建立，等待 0x81 心跳帧同步下位机状态");
	}

	/// <summary>收到下位机指令确认应答（0x80）：
	/// 0x0B 查询 SetRpm：ACK 附带下位机当前实际存储 SetRpm（4 字节大端），
	/// 回显到数码管与日志，验证设置面板下发后下位机确认写入的转速。
	/// 0x0C 寄存器读（长轮询）：ACK 附带编号+字段值，持续更新对应状态。</summary>
	private void OnAckReceived(Control.InfAck ack)
	{
		_lastRxTick = Environment.TickCount64;   // 收到 ACK = 通讯中
		if (ack.Cmd == Control.InfChange.CmdQuerySetRpm)
		{
			// 0x0B 查询：值字段（SetRpm）才是权威——当前固件 0x0B 的 result 字节恒 0
			// （下位机 ACK 结果 switch 未含 0x0B 分支被 default 覆盖），不依赖 Result==1；
			// 固件修复后 Result=1 也不影响此逻辑
			uint rpm = ack.SetRpm;                                 // 下位机实际存储的 SetRpm
			_lastSetRpm = rpm;
			Control.SdkLog.Info($"[ACK 0x0B] 下位机当前 SetRpm={rpm}（回读，回显到数码管）");
			// 停止态/设置场景下数码管显示下位机确认的设定值
			if (!_isRunning)
			{
				SetDigits(rpm);
			}
		}
		else if (ack.Cmd == Control.InfChange.CmdRegRead && ack.Result == 1)
		{
			// 0x0C 长轮询回传：按编号更新对应状态（心跳帧外的持续查询）
			Control.SdkLog.Info($"[ACK 0x0C] 寄存器#{ack.RegIndex} 值={ack.RegValue}");
			switch (ack.RegIndex)
			{
			case 5:   // MotorDir 方向 —— 只读显示下位机实际方向（1=正转 0=反转），上位机无法设置
				if (ack.RegValue == 0 || ack.RegValue == 1)
				{
					_isForward = ack.RegValue == 1;
					UpdateDirToggleLabel();
				}
				break;
			case 10:  // SetRpm
				if (!_isRunning) { _lastSetRpm = ack.RegValue; SetDigits(ack.RegValue); }
				break;
			case 11:  // Rpm —— 持续轮询：显示兜底（运行态由心跳长帧帧类型驱动）
				// 合法范围 ≤99999；超范围（如 0xFFFFFFD5=-43 的坏值/字节污染）当坏值忽略，
				// 不刷新保鲜时间戳——避免坏值连坐触发保鲜复位（心跳长帧才是运行证据）
				if (ack.RegValue <= 99999)
				{
					if (ack.RegValue > 0)
					{
						// 非零值：刷新保鲜 + 心跳异常时的运行兜底（Rpm 判据，与心跳同源）
						_lastValidDataTick = Environment.TickCount64;
						SyncRunStateByRpm(ack.RegValue);
					}
					UpdateDisplay(ack.RegValue);
				}
				else
				{
					Control.SdkLog.Info($"[坏值] 寄存器#11 Rpm={ack.RegValue} 超出合法范围(≤99999)，视为污染忽略");
				}
				break;
			case 14:  // ERR 报警码 —— 上位机主动探测报警（不等下位机报警心跳）
				{
					byte err = (byte)ack.RegValue;
					// ERR 合法范围 0~10（0=正常 1~10=有效报警码）；>10 的
					// 值（如 66 来自 ACK 字节交错污染）视为坏值忽略，不触发报警
					if (err > 10)
					{
						Control.SdkLog.Info($"[报警] 寄存器#14 ERR={err} 超出合法范围(0~10)，视为污染忽略");
						break;
					}
					if (err != 0 && !_hasAlarm)
					{
						// 进入报警：置报警态 + 复位运行态 + 立即显示 E 码
						_hasAlarm = true;
						_alarmCode = (byte)(err - 1);   // 显示 E+(ERR-1)：ERR=1→E0 ERR=2→E1
						if (_isRunning)
						{
							_isRunning = false;
							UpdatePowerIcon();
							StopRotor();
							SetStatus(MotorStatus.Standby);
							SetInputEnabled(true);
						}
						Control.SdkLog.Info($"[报警] 寄存器#14 ERR={err}，上位机探测到报警，UI 复位待机");
						// 立即刷新：rpm 参数保持真实转速上下文（_hasAlarm 时 UpdateDisplay
						// 无视 rpm 直接显示 E 码），不传 0 避免语义混淆
						UpdateDisplay((uint)_currentSpeed);
					}
					else if (err == 0 && _hasAlarm)
					{
						// 报警恢复：清除报警态，显示交给 case 11（转速）自然接管
						_hasAlarm = false;
						Control.SdkLog.Info($"[报警] 寄存器#14 ERR=0，报警恢复");
					}
					// 正常态（ERR=0 且未变化）不写屏——转速显示由 case 11 负责，
					// 避免 0 覆盖转速造成数码管抖动
					break;
				}
			}
		}
	}

	/// <summary>下位机综合反馈（0x81）真实数据驱动显示：
	/// 反馈帧是显示的唯一数据源——状态字段同步运行态（图标/转子/按钮），
	/// 转速字段刷新数码管与高低速灯，电流/报警留作扩展。
	/// 缩小版 Mic_Sdk.c 心跳变长：未启动 LEN=2（软件运行位+报警），
	/// 运行中 LEN=7（软件运行位+转速(3)+电流(2)+报警）——均不含 SetRpm，
	/// 待机显示回退用本地 _targetSpeed（上位机自己下发过的设置值）</summary>
	private void OnFeedbackReceived(Control.InfFeedback fb)
	{
		_lastRxTick = Environment.TickCount64;   // 收到心跳帧 = 通讯中
		// 变化才打印，避免下位机每帧上报时日志刷屏
		if (fb.SoftwareRun != _lastLogSoftRun || fb.SetRpm != _lastLogSetRpm
			|| fb.Rpm != _lastLogRpm || fb.Status != _lastLogStatus || fb.Alarm != _lastLogAlarm)
		{
			_lastLogSoftRun = fb.SoftwareRun;
			_lastLogSetRpm = fb.SetRpm; _lastLogRpm = fb.Rpm;
			_lastLogStatus = fb.Status; _lastLogAlarm = fb.Alarm;
			Control.SdkLog.Info($"[显示驱动] 软件运行={fb.SoftwareRun} SetRpm={fb.SetRpm} Rpm={fb.Rpm} Status={fb.Status} Alarm={fb.Alarm}");
		}

	// 新架构：电机启停由下位机实体脚踏自动控制（踩住=运行/松开=停止），
	// 上位机不主动发 0x02/0x03/0x0A 启停指令，也无需期望运行/补发机制
	// 报警优先：Alarm 合法范围 0~10（0=正常 1~10=有效报警码），>10 视为污染忽略
	if (fb.Alarm != 0 && fb.Alarm <= 10)
	{
		_hasAlarm = true;
		_alarmCode = (byte)(fb.Alarm - 1);   // 显示 E+(ERR-1)：ERR=1→E0 ERR=2→E1
		// 报警时强制回到待机态（图标/转子/按钮/输入框）：
		// 若不主动复位，UI 会卡在运行态而启动按钮被报警禁用（死锁）
		if (_isRunning)
		{
			_isRunning = false;
			UpdatePowerIcon();
			StopRotor();
			SetStatus(MotorStatus.Standby);
			SetInputEnabled(true);
			Control.SdkLog.Info($"[报警] 下位机报警={fb.Alarm}，UI 复位到待机（按钮恢复可启动）");
		}
		UpdateDisplay((uint)_currentSpeed);   // 立即显示 E 码（rpm 参数保持真实转速上下文，_hasAlarm 时无视）
		return;
	}
	// 报警恢复：清除报警态
	_hasAlarm = false;
	_lastSetRpm = fb.SetRpm;   // 记录下位机传回的 SetRpm（待机/设置显示用）
	// 短帧（HasRpm=false，LEN=3）：固件 MotorRun=0 仅作心跳握手，
	// Rpm 被硬编码为 0 只占位——不得据此驱动运行态/数码管，否则
	// 运行中固件偶发短帧（测速层瞬丢脉冲）会导致 0↔真实值 频闪；
	// 脚踏=1（踩着）时刷新保鲜：本固件运行中心跳可能 MotorRun 回 0
	// 发短帧（#11 才是运行证据），踩着脚踏即"运行意图"——刷新保鲜
	// 保证 #11 偶发读到 0 时也不误复位；真正停止（松开脚踏→短帧脚踏=0
	// 且 #11=0）不再刷新，由 CheckDataFreshness 兜底回零
	if (!fb.HasRpm)
	{
		if (fb.TBAJ == 1)
			_lastValidDataTick = Environment.TickCount64;
		return;
	}
	// 心跳帧（0x81）已在上层完成合法性校验（Rpm<=99999、ERR 0/1/2、SoftRun 0/1，坏帧直接丢弃），
	// 用其 Rpm 驱动运行态 + 数码管：刷新率≈10ms 一帧，转速显示接近实时；
	// 寄存器 #11 轮询保留作兜底/校准——同一 UpdateDisplay 仲裁，双源同值不冲突
	// 运行证据 = Rpm>0：未启动也发长帧（Rpm=0）不刷新保鲜，否则真停止后保鲜失效卡旧值
	if (fb.Rpm > 0)
		_lastValidDataTick = Environment.TickCount64;   // Rpm>0：刷新数据保鲜时间戳
	SyncRunStateByRpm(fb.Rpm);
	UpdateDisplay(fb.Rpm);
	}

	/// <summary>运行判据：Rpm>0 持续 RpmRunHoldMs 升运行（去抖滤单帧毛刺）；
	/// Rpm=0 不升不降（不动作）——不升避免"未启动也发长帧"的固件误判运行，
	/// 不降避免低速测速层丢脉冲闪零；真正停止由 CheckDataFreshness 保鲜兜底。</summary>
	private void SyncRunStateByRpm(uint rpm)
	{
		if (_hasAlarm)
		{
			_rpmPositiveStartTick = 0;
			SyncRunStateFromFeedback(false);
			return;
		}
		if (rpm == 0)
			return;   // Rpm=0 不动作：不升（未启动长帧不误判）也不降（丢脉冲不闪）
		long now = Environment.TickCount64;
		if (_rpmPositiveStartTick == 0) _rpmPositiveStartTick = now;
		if (now - _rpmPositiveStartTick >= RpmRunHoldMs)
			SyncRunStateFromFeedback(true);
	}

	/// <summary>数码管唯一显示仲裁（所有数据源只喂 rpm，不直接写屏）：
	/// 优先级 报警 > 运行 > 停止归 0；报警码由心跳帧/寄存器#14 写入 _alarmCode，
	/// 此处据 _hasAlarm 显示 E 码；停止后立即回 0（不保持最后转速）。
	/// 运行态收到 rpm=0 时保持上次有效值（测速层临时丢脉冲的宽容，不闪 0）</summary>
	private void UpdateDisplay(uint rpm)
	{
		if (_hasAlarm)
		{
			ShowAlarm(_alarmCode);
			return;
		}
		if (_isRunning)
		{
			// 运行态零值宽容：测速层临时丢脉冲/寄存器偶发读 0 时，
			// 保持上次有效值——避免数码管 0↔真实值 频闪；
			// 真正停止由 SyncRunStateFromFeedback(false) → 回落到此分支外再清屏
			if (rpm == 0 && _currentSpeed > 0)
				return;
			_currentSpeed = rpm;
			SetDigits(rpm);
			UpdateSpeedStatus();
			return;
		}
		_currentSpeed = 0;
		SetDigits(0);   // 停止：立即回 0
	}

	/// <summary>有效转速数据保鲜：超过 RpmDataTimeoutMs 未收到"运行证据"
	/// （心跳长帧/寄存器#11 非零值）即强制数码管回 0 并复位运行态——
	/// 真正停止后固件只发短帧（不刷新时间戳），快速兜底回零，不再卡旧值</summary>
	private void CheckDataFreshness()
	{
		if (_hasAlarm || _lastValidDataTick == 0) return;
		if (Environment.TickCount64 - _lastValidDataTick <= RpmDataTimeoutMs) return;
		if (!_isRunning && _currentSpeed == 0) return;   // 已停止且显示 0：无需处理
		_lastValidDataTick = Environment.TickCount64;
		Control.SdkLog.Info($"[保鲜] 超过 {RpmDataTimeoutMs}ms 无运行证据，强制回 0 并复位运行态");
		SyncRunStateFromFeedback(false);
		_rpmPositiveStartTick = 0;   // 复位正判起点（下次 Rpm>0 重新起算）
		_currentSpeed = 0;
		SetDigits(0);
	}

	/// <summary>上下位机通讯监测：链路未建立（LinkAlive=false）或链路在但
	/// 超过 NoCommTimeoutMs 无任何协议帧/ACK（下位机掉线/断流）→ 未通讯，
	/// 数码管显示 E100 提示并复位运行态；通讯恢复后回 0 由反馈接管。
	/// 仅状态翻转时动作（_linkLost 防每次 tick 重复写屏）</summary>
	private void CheckCommStatus()
	{
		// 未通讯判定：链路未建立 或 从未收到过帧（建链后首帧未到）→ 未通讯。
		// 关键：探测阶段的帧不走 OnFeedbackReceived，_lastRxTick 可能为 0；
		// 若只判 !LinkAlive，下位机断电但探测曾命中（LinkAlive=true 且 _lastRxTick=0）
		// 会误判"通讯正常"→ E100 被清成 00000 且永不转回
		bool noComm = !LinkAlive || _lastRxTick == 0;
		if (LinkAlive && _lastRxTick != 0
			&& Environment.TickCount64 - _lastRxTick > NoCommTimeoutMs)
			noComm = true;   // 链路在但帧断流（下位机死机/串口堵）

		if (noComm && !_linkLost)
		{
			_linkLost = true;
			_hasAlarm = false;            // 清旧报警态，避免旧 E 码覆盖 E100
			_isRunning = false;
			_currentSpeed = 0;
			_rpmPositiveStartTick = 0;
			_lastValidDataTick = 0;       // 停保鲜（无数据可保鲜）
			UpdatePowerIcon();
			StopRotor();
			SetStatus(MotorStatus.Standby);
			SetInputEnabled(true);
			ShowAlarm(100);               // E100：上下位机未通讯
			Control.SdkLog.Info("[通讯] 上下位机未通讯，显示 E100");
		}
		else if (!noComm && _linkLost)
		{
			_linkLost = false;
			SetDigits(0);                 // 通讯恢复：清 E100，由反馈帧接管显示
			Control.SdkLog.Info("[通讯] 通讯恢复");
		}
	}

	/// <summary>按反馈状态同步本地运行态：下位机是唯一权威数据源，
	/// 本地按钮状态只是下发意图；两者不一致时以反馈为准修正 UI</summary>
	private void SyncRunStateFromFeedback(bool running)
	{
		if (running == _isRunning) return;
		_isRunning = running;
		if (!running) _rpmPositiveStartTick = 0;   // 停止：复位正判起点（下次 Rpm>0 重新起算）
		UpdatePowerIcon();
		SetInputEnabled(!running);
		if (running)
		{
			SetStatus(MotorStatus.LowSpeed);
			StartRotor();
		}
		else
		{
			StopRotor();
			SetStatus(MotorStatus.Standby);
		}
	}

		// ===== 取消 Aero Snap =====
	private const int WM_WINDOWPOSCHANGING = 0x0046;
	private const int SWP_NOSIZE = 0x0001;

	[StructLayout(LayoutKind.Sequential)]
	private struct WINDOWPOS
	{
		public IntPtr hwnd;
		public IntPtr hwndInsertAfter;
		public int x;
		public int y;
		public int cx;
		public int cy;
		public int flags;
	}

	private void MainWindow_SourceInitialized(object? sender, EventArgs e)
	{
		var helper = new WindowInteropHelper(this);
		var hwndSource = HwndSource.FromHwnd(helper.Handle);
		hwndSource?.AddHook(WndProc);
	}

	private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WM_WINDOWPOSCHANGING)
		{
			var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
			pos.flags |= SWP_NOSIZE;
			Marshal.StructureToPtr(pos, lParam, false);
		}
		return IntPtr.Zero;
	}

		private void Grid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		// 模态：设置弹窗打开时不允许拖动主窗口
		if (SettingsPopup.IsOpen) return;
		try { DragMove(); } catch { /* 窗口状态不合法时忽略 */ }
	}

		// ===== 数码管更新 =====
		private void SetDigits(double value)
		{
			int v = Math.Max(0, Math.Min(99999, (int)value));
			int d1 = (v / 10000) % 10;
			int d2 = (v / 1000) % 10;
			int d3 = (v / 100) % 10;
			int d4 = (v / 10) % 10;
			int d5 = v % 10;
			// 变化才打印当前显示的数字码（避免每帧刷屏）
			if (v != _lastLogDigits)
			{
				_lastLogDigits = v;
				Control.SdkLog.Info($"[显示] 数值码={v}  数码=[{d1}{d2}{d3}{d4}{d5}]");
			}
			Digit1.Digit = d1;
			Digit2.Digit = d2;
			Digit3.Digit = d3;
			Digit4.Digit = d4;
			Digit5.Digit = d5;
		}

		/// <summary>报警优先显示：数码管显示 "E" + 补零报警码。
		/// 100~999 三位码（如 E100 未通讯提示：E + 1 0 0，末位熄灭）；
		/// 0~99 两位补零码（如 E02、E12），其余熄灭</summary>
		private void ShowAlarm(int alarm)
		{
			int a = Math.Max(0, Math.Min(999, alarm));
			if (a != _lastLogAlarmCode)                 // 报警码变化才打印，避免持续报警刷屏
			{
				_lastLogAlarmCode = a;
				Control.SdkLog.Info($"[报警] 报警码 E{a:D3}，覆盖转速显示");
			}
			Digit1.Digit = SevenSegmentDigit.DigitE;      // 'E'
			if (a >= 100)
			{
				// 三位码：E 百位 十位 个位，末位熄灭
				Digit2.Digit = (a / 100) % 10;
				Digit3.Digit = (a / 10) % 10;
				Digit4.Digit = a % 10;
				Digit5.Digit = SevenSegmentDigit.DigitBlank;
			}
			else
			{
				// 两位补零码：E 十位 个位，后两位熄灭
				Digit2.Digit = (a / 10) % 10;
				Digit3.Digit = a % 10;
				Digit4.Digit = SevenSegmentDigit.DigitBlank;
				Digit5.Digit = SevenSegmentDigit.DigitBlank;
			}
		}

		// ===== 状态切换（仅维护内部状态，提示凹槽已移除） =====
	private void SetStatus(MotorStatus status)
	{
		_status = status;
	}

		// ===== 图标切换：Play↔Pause =====
	// 暂停状态配色：#163986 主题深蓝
	private static readonly LinearGradientBrush PowerRunBg = new LinearGradientBrush
	{
		StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
		GradientStops = new GradientStopCollection
		{
			new GradientStop(Color.FromRgb(0x16, 0x39, 0x86), 0),
			new GradientStop(Color.FromRgb(0x10, 0x28, 0x60), 0.15),
			new GradientStop(Color.FromRgb(0x0A, 0x1A, 0x40), 1),
		}
	};
	private static readonly LinearGradientBrush PowerRunHl = new LinearGradientBrush
	{
		StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
		GradientStops = new GradientStopCollection
		{
			new GradientStop(Color.FromArgb(0x80, 0x40, 0x7C, 0xC0), 0),
			new GradientStop(Color.FromArgb(0x00, 0x40, 0x7C, 0xC0), 1),
		}
	};
	private static readonly LinearGradientBrush PowerStopBg = new LinearGradientBrush
	{
		StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
		GradientStops = new GradientStopCollection
		{
			new GradientStop(Color.FromRgb(0x22, 0x2A, 0x38), 0),
			new GradientStop(Color.FromRgb(0x1A, 0x22, 0x30), 0.15),
			new GradientStop(Color.FromRgb(0x15, 0x19, 0x1F), 1),
		}
	};
	private static readonly LinearGradientBrush PowerStopHl = new LinearGradientBrush
	{
		StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
		GradientStops = new GradientStopCollection
		{
			new GradientStop(Color.FromArgb(0x80, 0x60, 0xA8, 0xE0), 0),
			new GradientStop(Color.FromArgb(0x00, 0x60, 0xA8, 0xE0), 1),
		}
	};

	private void UpdatePowerIcon()
	{
		var iconPlay = BtnPower.Template.FindName("IconPlay", BtnPower) as Path;
		var iconPause = BtnPower.Template.FindName("IconPause", BtnPower) as Path;
		var lblPower = BtnPower.Template.FindName("LblPower", BtnPower) as TextBlock;
		var bd = BtnPower.Template.FindName("bd", BtnPower) as Border;
		var hl = BtnPower.Template.FindName("powerHl", BtnPower) as Border;
		if (iconPlay == null || iconPause == null) return;
		// 图标+文字一致：启动语义始终用播放图标（运行中高亮，未启动置灰不可点）
		iconPlay.Visibility = Visibility.Visible;
		iconPause.Visibility = Visibility.Collapsed;
		// 脚踏=控制开关，位置由转速判定：Rpm>0 且无报警 = 脚踏踩住电机在转；
		// Rpm=0 或报警 = 脚踏静止/未启动 → 禁用启动按钮（置灰）并提示"请踩脚踏"；
		// （设置入口不受影响——未启动仍可打开"设置"修改参数）
		BtnPower.IsEnabled = _isRunning;
		if (lblPower != null)
		{
			lblPower.Text = _isRunning ? "启动" : "请踩脚踏";
			// 未运行提示文字暗化（暗灰），运行态恢复白字
			lblPower.Foreground = _isRunning
				? Brushes.White
				: new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA4));
		}
		// 运行中切换为 #163986 主题色；未启动与设置键保持一致的深色
		if (bd != null) bd.Background = _isRunning ? PowerRunBg : PowerStopBg;
		if (hl != null) hl.Background = _isRunning ? PowerRunHl : PowerStopHl;
		// 图标颜色跟随底色：蓝底用白色，深底用蓝色
		iconPlay.Fill = iconPause.Fill = _isRunning
			? Brushes.White
			: new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xFF));
	}

		/// <summary>同步设置弹窗中的方向标签——只读显示下位机实际方向（_isForward 由寄存器 #5 轮询驱动）</summary>
		private void UpdateDirToggleLabel()
		{
			BtnDirToggle.ApplyTemplate();
			var lbl = BtnDirToggle.Template.FindName("LblDirToggle", BtnDirToggle) as TextBlock;
			if (lbl != null) lbl.Text = _isForward ? "正转" : "反转";
		}

	// ===== 按钮事件 =====
	private void BtnPower_Click(object sender, RoutedEventArgs e)
	{
		// 新架构：电机启停由下位机实体脚踏自动控制（踩住=运行/松开=停止），
		// 上位机不主动发 0x02/0x03/0x0A 启停指令——启动按钮仅作为
		// 设置入口（打开设置面板设置转速/方向），运行态由 0x81 反馈驱动显示
		Control.SdkLog.Info("[启动] 电机启停由下位机脚踏自动控制，打开设置面板设置参数");
		BtnSetSpeed_Click(sender, e);
	}

		/// <summary>启动旋转：用 CompositionTarget.Rendering 驱动，每帧渲染前更新</summary>
	private void StartRotor()
	{
		_lastRenderTime = System.Diagnostics.Stopwatch.GetTimestamp();
		CompositionTarget.Rendering -= Rotor_Rendering;
		CompositionTarget.Rendering += Rotor_Rendering;
	}

	private void Rotor_Rendering(object? sender, EventArgs e)
	{
		if (!_isRunning) return;
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		double dtMs = (now - _lastRenderTime) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		_lastRenderTime = now;

		// 固定周期：60000rpm 对应 1000ms/圈，不随当前速度变化
		double durationMs = 1000.0;
		double delta = 360.0 * dtMs / durationMs;
		_rotorAngle += _isForward ? delta : -delta;
		if (_rotorAngle >= 360) _rotorAngle -= 360;
		if (_rotorAngle < 0) _rotorAngle += 360;
		RotorRotate.Angle = _rotorAngle;
	}

	/// <summary>停止旋转</summary>
	private void StopRotor()
	{
		CompositionTarget.Rendering -= Rotor_Rendering;
	}

	private double _rotorAngle = 0;
	private long _lastRenderTime = 0;

		/// <summary>根据当前速度动态切换高速/低速状态灯</summary>
		private void UpdateSpeedStatus()
		{
			if (!_isRunning) return;
			var newStatus = _currentSpeed >= 50000 ? MotorStatus.HighSpeed : MotorStatus.LowSpeed;
			if (newStatus != _status)
				SetStatus(newStatus);
		}

		/// <summary>使用 DispatcherTimer 驱动数字滚动动画</summary>
		private void AnimateSpeed(double from, double to, int durationMs, Action? completed)
		{
			_animTimer?.Stop();   // 切换动画前先停旧动画（防双计时器互相覆盖）
			var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
			_animTimer = timer;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			double startVal = from;
			double targetVal = to;
			timer.Tick += (s, e) =>
			{
				double p = Math.Min(1.0, (double)watch.ElapsedMilliseconds / durationMs);
				// EaseOut: 1 - (1-p)^3
				double eased = 1 - Math.Pow(1 - p, 3);
				_currentSpeed = startVal + (targetVal - startVal) * eased;
				SetDigits(_currentSpeed);
				UpdateSpeedStatus();  // 动画过程中动态切换高/低速
				if (p >= 1.0)
				{
					timer.Stop();
					_currentSpeed = targetVal;
					SetDigits(_currentSpeed);
					UpdateSpeedStatus();
					completed?.Invoke();
				}
			};
			timer.Start();
		}

		private async void BtnSetSpeed_Click(object sender, RoutedEventArgs e)
	{
		// 打开设置面板：方向只读显示下位机实际状态（寄存器 #5 轮询驱动 _isForward）
		OpenSettingsPanel();
		UpdateDirToggleLabel();
		// 转速输入框先留空，等 0x0B 识别到下位机当前 SetRpm 后再填入显示
		PopupTxtSpeed.Text = "";
		PopupTxtSpeed.Focus();   // 打开即聚焦输入框，可直接键入
		// 报警态：输入框只读（禁止修改/下发转速）
		PopupTxtSpeed.IsReadOnly = _hasAlarm;
		// 主动 0x0B 查询下位机当前 SetRpm，识别成功后才显示（超时留空，不显示未经确认的值）；
		// 下位机回读值若不在合法步进档位（如默认 0 / 越界坏值），留空让用户输入
		var qack = await SendCheckedAsync(Control.InfChange.CmdQuerySetRpm);
		if (qack != null && !_hasAlarm)
		{
			if (IsValidStep((int)qack.SetRpm))
			{
				_popupSpeed = qack.SetRpm;
				PopupTxtSpeed.Text = ((int)Math.Round(_popupSpeed)).ToString();
			}
			else
			{
				_popupSpeed = 0;
				PopupTxtSpeed.Text = "";
				Control.SdkLog.Info($"[设置] 下位机 SetRpm={qack.SetRpm} 不在步进档位（需 10000~70000），留空待输入");
			}
			PopupTxtSpeed.SelectAll();
		}
		else if (qack == null)
		{
			Control.SdkLog.Info("[设置] 查询下位机 SetRpm 超时，输入框保持留空");
		}
	}

	// ===== 面板拖动 =====
	// 已去除 LayoutTransform 缩放（1:1 渲染），Popup 拖动鼠标位移即布局位移
	private const double PopupScale = 1.0;
	private void PopupHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		_isPopupDragging = true;
		_popupDragStart = e.GetPosition(this);
		_popupStartX = SettingsPopup.HorizontalOffset;
		_popupStartY = SettingsPopup.VerticalOffset;
		(sender as UIElement)?.CaptureMouse();
	}

	private void PopupHeader_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (!_isPopupDragging) return;
		Point pos = e.GetPosition(this);
		SettingsPopup.HorizontalOffset = _popupStartX + (pos.X - _popupDragStart.X) / PopupScale;
		SettingsPopup.VerticalOffset = _popupStartY + (pos.Y - _popupDragStart.Y) / PopupScale;
	}

	private void PopupHeader_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (!_isPopupDragging) return;
		_isPopupDragging = false;
		(sender as UIElement)?.ReleaseMouseCapture();
	}

	// ===== 设置面板：方向 =====
	// 方向只读：显示下位机实际方向（寄存器 #5 轮询），上位机无法设置——按钮已禁用，
	// 点击事件保留空实现（IsEnabled=false 时不触发，此处防御性兜底）
	private void BtnDirToggle_Click(object sender, RoutedEventArgs e)
	{
	}

	// ===== 设置面板：确认（写入主状态） =====
	private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
	{
		// 方向已由下位机控制，确认仅应用转速；转子旋转方向跟随 _isForward（寄存器 #5 驱动）
		if (_isRunning) StartRotor();
		// 应用转速（输入框校验：范围 10000~70000 + 档位步进；非法直接返回不下发，
		// 避免 0/越界值被钳位后误下发）
		if (!TryReadPopupSpeed())
		{
			Control.SdkLog.Info("[设置] 输入非法（需 10000~70000 且符合步进档位），保持原转速不下发");
			ShowPopupHint("转速需 10000~70000 且符合步进档位");
			return;
		}
		_targetSpeed = Math.Max(RpmRangeMin, Math.Min(RpmRangeMax, _popupSpeed));
		Control.SdkLog.Info($"[设置] 确认转速 = {(int)_targetSpeed}");

		// 有链路时传输 0x01 设置转速（SoftwareRun_flag=1 保持置位中，0x02 打开面板时已置 1），
		// 随后 0x0B 查询回读验证；确认只改数据不关闭面板——flag 持续保持 1，
		// 由取消/退出（CloseSettingsPanel）发 0x03 清 0
		if (LinkAlive && _targetSpeed > 0)
		{
			// 0x01 下发：下位机设置完成即清 SetRpm_flag，ACK 结果恒 0
			// （"设置完成回 0"）——不能用 Result==1 判成功；
			// 以 0x0B 查询回读的下位机实际存储值 == 下发值为准
			var ack = await SendCheckedAsync(Control.InfChange.CmdSetRpm,
				Control.InfChange.RpmToBytes((uint)_targetSpeed));
			if (ack == null)
				Control.SdkLog.Info("[设置] 0x01 下发失败（无 ACK/链路断）");
			uint want = (uint)_targetSpeed;
			var qack = await SendCheckedAsync(Control.InfChange.CmdQuerySetRpm);
			// 0x0B 以回读值 == 下发值为准判成功：当前固件 0x0B 的 result 字节恒 0
			// （下位机 ACK 结果 switch 未含 0x0B 分支被 default 覆盖），不看 result；
			// 固件修复后（result=1）同样满足
			if (qack != null && qack.SetRpm == want)
				Control.SdkLog.Info($"[设置] 下位机确认转速 = {want}（0x0B 回读一致，设置成功）");
			else
				Control.SdkLog.Info($"[设置] 转速验证未通过：下发={want} 回读={qack?.SetRpm} 结果={qack?.Result ?? -1}，下次重试");
			Control.SdkLog.Info("[设置] 确认完成，面板保持打开（SoftwareRun_flag 保持 1），取消/退出时置 0");
			return;
		}
	}

	// ===== 设置面板：转速直接输入 =====

	/// <summary>只允许输入数字</summary>
	private void PopupTxtSpeed_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
	{
		e.Handled = !int.TryParse(e.Text, out _);
	}

	/// <summary>回车 = 确认（与点确认按钮等效）</summary>
	private void PopupTxtSpeed_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == System.Windows.Input.Key.Enter)
		{
			e.Handled = true;
			BtnConfirm_Click(sender, e);
		}
	}

	/// <summary>失焦校验：非法/超范围输入回退为当前预览值（无预览值时清空）并提示</summary>
	private void PopupTxtSpeed_LostFocus(object sender, RoutedEventArgs e)
	{
		if (!TryReadPopupSpeed())
		{
			PopupTxtSpeed.Text = _popupSpeed > 0 ? ((int)Math.Round(_popupSpeed)).ToString() : "";
			ShowPopupHint("转速需 10000~70000 且符合步进档位");
		}
	}

	/// <summary>转速合法范围与档位步进校验（产品规格）：
	/// 范围 10000~70000；档位步进：10000≤v≤30000 步进 1000，v&gt;30000 步进 10000
	/// （&lt;10000 档步进 100 因范围下限 10000 不会触达，规格保留备查）</summary>
	private const int RpmRangeMin = 10000;
	private const int RpmRangeMax = 70000;

	private static bool IsValidStep(int v)
	{
		if (v < RpmRangeMin || v > RpmRangeMax) return false;   // 范围 10000~70000
		if (v <= 30000) return v % 1000 == 0;                    // 10000≤v≤30000：步进 1000
		return v % 10000 == 0;                                   // 30000&lt;v≤70000：步进 10000
	}

	/// <summary>读取输入框并校验（范围 10000~70000 + 档位步进）；成功则写入 _popupSpeed</summary>
	private bool TryReadPopupSpeed()
	{
		if (int.TryParse(PopupTxtSpeed.Text.Trim(), out int v) && IsValidStep(v))
		{
			_popupSpeed = v;
			return true;
		}
		return false;
	}

	// 设置弹窗校验提示条：非法输入时在面板底部显示提示，自动淡出
	private System.Windows.Threading.DispatcherTimer? _hintTimer;

	private void ShowPopupHint(string msg)
	{
		PopupHintText.Text = msg;
		PopupHint.Visibility = Visibility.Visible;
		PopupHint.BeginAnimation(UIElement.OpacityProperty,
			new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
		_hintTimer?.Stop();
		var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
		_hintTimer = t;
		t.Tick += (s, e) =>
		{
			t.Stop();
			var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
			anim.Completed += (s2, e2) => PopupHint.Visibility = Visibility.Collapsed;
			PopupHint.BeginAnimation(UIElement.OpacityProperty, anim);
		};
		t.Start();
	}

	// ===== 设置面板：取消 =====
	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		CloseSettingsPanel();
	}
	}
}
