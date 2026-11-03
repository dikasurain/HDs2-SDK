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

	// 全局变量：默认转速
	public const double DefaultSpeed = 60000;
	// 运行状态
	private bool _isRunning = false;
	private bool _isForward = true;  // true=正转(CW), false=反转(CCW)
	public double _targetSpeed = DefaultSpeed;  // 全局目标转速
	private double _currentSpeed = 0;
	private bool _hasAlarm = false;  // 报警态：Alarm!=0 时显示 E+报警码 且禁止启动
	private uint _lastSetRpm = 0;    // 下位机传回的 SetRpm（待机/设置显示用；无数据=0）
	// 下位机状态闭环同步：仅用户点启动后期望 SoftwareRun_flag=1
	// （0x03 停止不清联机位，此条件只在固件复位后触发——补发 0x02 恢复运行）
	// 用户点停止置 false（不再补发），点启动再置 true
	private bool _expectRun = false;   // 期望运行（仅用户点启动后置 true；0x02=启动指令，不能在程序启动时自动发）
	private DateTime _lastAutoResend = DateTime.MinValue;  // 补发节流
	// 启动宽限期：0x02 生效后，下位机在途的最后一帧旧短帧(Status=0)可能
	// 晚到 0~心跳周期(实测约2.6s)——期间 Status=0 视为陈旧帧忽略，防止
	// 误判停止把 UI 打回暂停（且 running=Status&&_isRunning 会单向锁死，
	// 后续长帧 Status=1 也无法恢复运行态）。宽限 = 心跳周期 + 余量
	private DateTime _startGraceUntil = DateTime.MinValue;
	private const double StartGraceMs = 3500;
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
	// 设置面板预览状态（确认前不写入主状态）
	private bool _popupDirPreview = true;
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
	}

	private void CloseSettingsPanel()
	{
		SettingsPopup.IsOpen = false;
		MainPanel.IsHitTestVisible = true;   // 恢复主面板交互
		MainPanel.Opacity = 1.0;              // 恢复主面板亮度
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
		Control.SdkLog.Info($"[App] 端口识别启动，波特率={Control.InfChange.LinkBaudRate}，目标转速={_targetSpeed}");
		// 自动识别：InfChange.Start() 轮询扫描 → 探测下位机(航插) → 命中即自动建链路；
		// 无需硬编码端口（ForceOpenLink 仅作兜底/备用，可据此注释按需启用）
	}

	protected override void OnClosed(EventArgs e)
	{
		// 关闭前恢复下位机状态（连接状态通知）：
		// 0x03 DATA=1（断开语义）→ 下位机 SoftwareRun_flag 清 0
		// （状态位归0，心跳首位回 0=已断开）+ 顺带停止电机——上位机
		// 退出即"回复为 0"，下次程序启动 0x02 重新置 1（重新握手）。
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

	/// <summary>启动指令序列：方向 → 转速 → 启动（逐条等确认）。
	/// 新固件框架：0x04 写 MotorDir（运行中拒改，故排最前）；
	/// 0x01 纯设置转速（SetRpm_flag 瞬时置 1→赋值→反馈前清 0，不操作启停）；
	/// 0x02 启动——下位机直接设置 MotorRun=1/MotorStop=0 + 置联机位，
	/// 并在 ACK 后立即补发 0x81 长帧（携带 Rpm）给上位机确认。
	/// 缩小版 Mic_Sdk.c 无 0x06 供电指令（MotorPower 由应用层本地维护）。</summary>
	private async Task SendStartSequenceAsync()
	{
		if (!LinkAlive) return;
		Control.SdkLog.Info($"[下发] 启动序列 方向={(_isForward ? 1 : 2)} 转速={_targetSpeed} 启动");
		await SendCheckedAsync(Control.InfChange.CmdSetDir, (byte)(_isForward ? 1 : 2));
		uint rpm = (uint)_targetSpeed;
		await SendCheckedAsync(Control.InfChange.CmdSetRpm, Control.InfChange.RpmToBytes(rpm));
		await SendCheckedAsync(Control.InfChange.CmdStart);
		// 脚踏信号（0x0A key=1）：按下=请求运行——与 0x02 等效的另一条
		// 运行触发路径（写 TBAJ 位，key_event_tba 级联 run_on），
		// 模拟原机脚踏启动行为；松开不发送（保持运行，由 0x03 停止）
		await SendCheckedAsync(Control.InfChange.CmdKey, 1, (byte)1);
	}

	/// <summary>链路建立回调（阶段1·握手验证）：
	/// 不主动发任何指令（既不发 0x03 也不发 0x02）：
	///   0x03 会把下位机已有运行态（脚踏启动/上电残留）打停，违背
	///   "下位机显示运行时上位机同步显示启动"——反馈帧是显示的唯一
	///   权威数据源，建链后第一个 0x81 心跳帧（≈10ms）即驱动 UI 同步：
	///   长帧(Status=1)进入启动态，短帧(Status=0)回待机；
	///   0x02=启动指令，程序启动阶段更不允许触发（启动只由启动按钮触发）</summary>
	private void OnLinkOpened(string portName)
	{
		Control.SdkLog.Info($"[链路] {portName} 已建立，等待 0x81 心跳帧同步下位机状态");
	}

	/// <summary>收到下位机指令确认应答（0x80）：
	/// 0x0B 查询 SetRpm 时，下位机在 ACK 中附带其当前实际存储的
	/// SetRpm（4 字节大端，InfAck.SetRpm），上位机回显到数码管与日志，
	/// 验证设置面板下发后下位机确认写入的转速。</summary>
	private void OnAckReceived(Control.InfAck ack)
	{
		if (ack.Cmd != Control.InfChange.CmdQuerySetRpm) return;   // 只关心 SetRpm 查询确认
		uint rpm = ack.SetRpm;                                 // 下位机实际存储的 SetRpm
		if (ack.Result == 1)
		{
			_lastSetRpm = rpm;
			Control.SdkLog.Info($"[ACK 0x0B] 下位机当前 SetRpm={rpm}（结果=1 查询成功，回显到数码管）");
			// 停止态/设置场景下数码管显示下位机确认的设定值
			if (!_isRunning)
			{
				SetDigits(rpm);
			}
		}
		else
		{
			Control.SdkLog.Info($"[ACK 0x0B] SetRpm 查询失败（结果={ack.Result}）");
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
		// 变化才打印，避免下位机每帧上报时日志刷屏
		if (fb.SoftwareRun != _lastLogSoftRun || fb.SetRpm != _lastLogSetRpm
			|| fb.Rpm != _lastLogRpm || fb.Status != _lastLogStatus || fb.Alarm != _lastLogAlarm)
		{
			_lastLogSoftRun = fb.SoftwareRun;
			_lastLogSetRpm = fb.SetRpm; _lastLogRpm = fb.Rpm;
			_lastLogStatus = fb.Status; _lastLogAlarm = fb.Alarm;
			Control.SdkLog.Info($"[显示驱动] 软件运行={fb.SoftwareRun} SetRpm={fb.SetRpm} Rpm={fb.Rpm} Status={fb.Status} Alarm={fb.Alarm}");
		}

		// ===== 陈旧帧过滤（启动宽限期）=====
		// 刚下发 0x02 后，在途旧短帧(Status=0)可能晚到一个心跳周期(≈2.6s)，
		// 若不忽略会把 UI 误判回暂停（且与 _isRunning 与运算后单向锁死，
		// 电机实际在转但 UI 永远显示暂停）。宽限期内 Status=0 直接跳过
		bool inStartGrace = _expectRun && DateTime.Now < _startGraceUntil;
		if (inStartGrace && fb.Status == 0)
		{
			Control.SdkLog.Info("[反馈0x81] 启动宽限期内忽略陈旧短帧(Status=0)");
			return;
		}

		// ===== 闭环自动同步：期望运行（用户点过启动）但下位机实际停止
		// （Status=0=短帧=MotorRun=0，如固件复位/异常停机）→ 补发 0x02 =====
		// 注：看 Status 不看 SoftwareRun——0x03 停止不清联机位（恒1会漏判）
		// 节流：下位机心跳约 10ms 一帧（HEART_DIV 分频），500ms 限一次防刷
		if (_expectRun && fb.Status == 0 && LinkAlive
			&& (DateTime.Now - _lastAutoResend).TotalMilliseconds > 500)
		{
			_lastAutoResend = DateTime.Now;
			_startGraceUntil = DateTime.Now.AddMilliseconds(StartGraceMs);  // 补发后重开宽限
			Control.SdkLog.Info($"[自动补发] 期望运行 实际停止(Status=0)，补发 0x02");
			_infChange?.SendCommand(Control.InfChange.CmdStart);
		}
		// 报警优先：Alarm!=0 → 显示 E+两位报警码，覆盖转速显示，且置报警态（禁止启动/改转速）
	if (fb.Alarm != 0)
	{
		bool wasAlarm = _hasAlarm;
		_hasAlarm = true;
		ShowAlarm(fb.Alarm);
		// 报警时强制回到待机态（图标/转子/按钮/输入框）：
		// 报警帧是短帧（固件 ERR≠0 只发 2 字节），Status 判定不到运行，
		// 若不主动复位，UI 会卡在运行态而启动按钮被报警禁用（死锁）
		if (!wasAlarm && _isRunning)
		{
			_isRunning = false;
			_expectRun = false;
			UpdatePowerIcon();
			StopRotor();
			SetStatus(MotorStatus.Standby);
			SetInputEnabled(true);
			Control.SdkLog.Info($"[报警] 下位机报警={fb.Alarm}，UI 复位到待机（按钮恢复可启动）");
		}
		return;
	}
	_hasAlarm = false;
	_lastSetRpm = fb.SetRpm;   // 记录下位机传回的 SetRpm（待机/设置显示用）
		// 运行态同步（图标/转子/按钮）：
		// 下位机 Status（MotorRun）为唯一权威——下位机显示运行（长帧
		// Status=1）上位机也同步进入启动态，即使非用户点击触发
		// （如脚踏/面板按键直接启动、上电残留运行态）；
		// Status=0（短帧）同步回待机
		bool running = fb.Status == 1;
		SyncRunStateFromFeedback(running);
		if (running)
	{
		// 显示源：下位机反馈为唯一权威——实测 Rpm 是什么就显示什么
		// （含 0：ERR=0 正常态但测速未接/未起转时如实显示 00000，
		// 不再回退设定值）
		_animTimer?.Stop();   // 反馈接管：停掉本地动画，防互相覆盖
		_currentSpeed = fb.Rpm;
		SetDigits(fb.Rpm);
		UpdateSpeedStatus();
	}
	else
	{
		// 未启动（ERR=0 正常态）：数码管显示 00000（实际转速为 0）；
		// 停止回弹动画进行中不覆盖（动画终点也是 0，避免闪跳）
		if (_animTimer == null || !_animTimer.IsEnabled)
		{
			_currentSpeed = 0;
			SetDigits(0);
		}
	}
	}

	/// <summary>按反馈状态同步本地运行态：下位机是唯一权威数据源，
	/// 本地按钮状态只是下发意图；两者不一致时以反馈为准修正 UI</summary>
	private void SyncRunStateFromFeedback(bool running)
	{
		if (running == _isRunning) return;
		_isRunning = running;
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

		/// <summary>报警优先显示：数码管显示 "E" + 两位补零报警码（如 E02、E12），其余熄灭</summary>
		private void ShowAlarm(int alarm)
		{
			int a = Math.Max(0, Math.Min(99, alarm));   // 报警码固定两位（00-99）
			if (a != _lastLogAlarmCode)                 // 报警码变化才打印，避免持续报警刷屏
			{
				_lastLogAlarmCode = a;
				Control.SdkLog.Info($"[报警] 报警码 E{a:D2}，覆盖转速显示");
			}
			Digit1.Digit = SevenSegmentDigit.DigitE;      // 'E'
			Digit2.Digit = (a / 10) % 10;                 // 十位（补零）
			Digit3.Digit = a % 10;                        // 个位
			Digit4.Digit = SevenSegmentDigit.DigitBlank;  // 熄灭
			Digit5.Digit = SevenSegmentDigit.DigitBlank;  // 熄灭
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
		iconPlay.Visibility = _isRunning ? Visibility.Collapsed : Visibility.Visible;
		iconPause.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
		if (lblPower != null) lblPower.Text = _isRunning ? "停止" : "启动";
		// 运行中切换为 #163986 主题色
		if (bd != null) bd.Background = _isRunning ? PowerRunBg : PowerStopBg;
		if (hl != null) hl.Background = _isRunning ? PowerRunHl : PowerStopHl;
	}

		/// <summary>同步设置弹窗中的方向标签（使用预览状态）</summary>
		private void UpdateDirToggleLabel()
		{
			BtnDirToggle.ApplyTemplate();
			var lbl = BtnDirToggle.Template.FindName("LblDirToggle", BtnDirToggle) as TextBlock;
			if (lbl != null) lbl.Text = _popupDirPreview ? "正转" : "反转";
		}

	// ===== 按钮事件 =====
	private async void BtnPower_Click(object sender, RoutedEventArgs e)
	{
		// 关闭可能打开的设置面板
		CloseSettingsPanel();
		if (!_isRunning)
		{
			// 报警态禁止启动
			if (_hasAlarm)
			{
				Control.SdkLog.Info("[启动] 报警中，禁止启动");
				return;
			}
			_isRunning = true;
			_expectRun = true;  // 用户点启动后期望下位机 SoftwareRun=1，闭环自动补发
			// 启动宽限期：忽略 0x02 生效前在途的最后一帧旧短帧(Status=0)
			// （下位机心跳周期实测≈2.6s，陈旧帧晚到会把 UI 误打回暂停并锁死）
			_startGraceUntil = DateTime.Now.AddMilliseconds(StartGraceMs);
			UpdatePowerIcon();
			SetInputEnabled(false); // 运行中禁用设置按钮
			// 启动时先设为低速（速度从0开始）
			SetStatus(MotorStatus.LowSpeed);
			StartRotor(); // 启动旋转图片
			Control.SdkLog.Info($"[启动] 点击启动，有链路={LinkAlive}，目标转速={_targetSpeed}");
			// 显示策略：不做本地渐变动画——有链路时启动后第一帧长帧（≤10ms）
			// 的 Rpm 直接赋值数码管（固件运行中 Rpm=SetRpm 回显）；
			// 无链路时直接显示本地目标转速（保持待机数值）
			if (!LinkAlive)
			{
				_currentSpeed = _targetSpeed;
				SetDigits(_targetSpeed);
			}
			if (LinkAlive)
			{
				// 启动序列（仅启动按钮触发）：0x04 方向 → 0x01 转速（纯设置，
				// SetRpm_flag 瞬时）→ 0x02 启动（MotorRun=1/MotorStop=0
				// + 联机位置 1，ACK 后下位机补发 0x81 长帧确认）
				Control.SdkLog.Info("[启动] 下发启动序列（方向→转速→启动）");
				await SendStartSequenceAsync();
			}
			else
			{
				// 无链路：未识别到下位机（端口被占用/设备未响应/波特率不符）。
				// 注意：这里不再调 Start() 兜底建链——轮询已由构造时 Start(2000) 启动，
				// 且无参 Start() 是发 0x02 的指令重载，会误启动电机
				Control.SdkLog.Info("[启动] 暂无链路：未识别到下位机，显示本地目标转速");
			}
		}
		else
		{
			_isRunning = false;
			_expectRun = false;  // 用户主动停止，不再自动补发 0x02
			UpdatePowerIcon();
			SetInputEnabled(true); // 停止后恢复设置按钮
			StopRotor(); // 停止旋转
			SetStatus(MotorStatus.Standby);
			// 停止回弹动画：渐变归零（实际转速为 0，与反馈帧显示一致）
			AnimateSpeed(_currentSpeed, 0, 1000, null);
			if (LinkAlive)
			{
				// 下发停止序列：先松开脚踏（0x0A key=1 松开，清 TBAJ）再停止。
				// 启动序列曾发 2F 0A 02 01 01（脚踏按下模拟），TBAJ=1 时下位机
				// 0x03 被脚踏优先逻辑拒绝（run_off 不执行、ACK=0、电机不停），
				// 故必须先释放脚踏再发 0x03（MotorRun=0/MotorStop=1，联机位保持）
				Control.SdkLog.Info("[停止] 下发停止序列（松开脚踏→停止）");
				await SendCheckedAsync(Control.InfChange.CmdKey, 1, (byte)0);
				await SendCheckedAsync(Control.InfChange.CmdStop);
			}
			else
			{
				Control.SdkLog.Info("[停止] 无链路，仅本地回弹");
			}
		}
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

		private void BtnSetSpeed_Click(object sender, RoutedEventArgs e)
	{
		// 打开设置面板：用当前主状态初始化预览
		_popupDirPreview = _isForward;
		OpenSettingsPanel();
		UpdateDirToggleLabel();
		// 转速输入框预填当前值（缩小版反馈无 SetRpm → 回退本地目标转速）
		_popupSpeed = _lastSetRpm > 0 ? _lastSetRpm : _targetSpeed;
		PopupTxtSpeed.Text = ((int)Math.Round(_popupSpeed)).ToString();
		PopupTxtSpeed.SelectAll();
		PopupTxtSpeed.Focus();   // 打开即聚焦输入框，可直接键入
		// 报警态：输入框只读（禁止修改/下发转速）
		PopupTxtSpeed.IsReadOnly = _hasAlarm;
	}

	// ===== 面板拖动 =====
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
		SettingsPopup.HorizontalOffset = _popupStartX + (pos.X - _popupDragStart.X);
		SettingsPopup.VerticalOffset = _popupStartY + (pos.Y - _popupDragStart.Y);
	}

	private void PopupHeader_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (!_isPopupDragging) return;
		_isPopupDragging = false;
		(sender as UIElement)?.ReleaseMouseCapture();
	}

	// ===== 设置面板：方向切换（仅修改预览，确认时才写入主状态） =====
	private void BtnDirToggle_Click(object sender, RoutedEventArgs e)
	{
		_popupDirPreview = !_popupDirPreview;
		UpdateDirToggleLabel();
	}

	// ===== 设置面板：确认（写入主状态） =====
	private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
	{
		// 应用方向
		if (_popupDirPreview != _isForward)
		{
			_isForward = _popupDirPreview;
			if (_isRunning) StartRotor();
		}
		// 应用转速（输入框校验后的值写入 _targetSpeed；非法输入回退原值）
		if (!TryReadPopupSpeed())
		{
			Control.SdkLog.Info("[设置] 输入非法，保持原转速");
		}
		_targetSpeed = Math.Max(0, Math.Min(99999, _popupSpeed));
		Control.SdkLog.Info($"[设置] 确认转速 = {(int)_targetSpeed}");

		// 有链路时关闭面板后下发 0x01 设置转速，随后 0x0B 查询回读
		// 下位机实际存储值，OnAckReceived 接收 ACK 并回显到数码管/日志
		if (LinkAlive && _targetSpeed > 0)
		{
			CloseSettingsPanel();   // 先关面板，避免 await 期间面板残留
			var ack = await SendCheckedAsync(Control.InfChange.CmdSetRpm,
				Control.InfChange.RpmToBytes((uint)_targetSpeed));
			if (ack != null && ack.Result == 1)
				Control.SdkLog.Info($"[设置] 下位机确认转速 = {(int)_targetSpeed}（ACK=1 设置成功）");
			else
				Control.SdkLog.Info($"[设置] 转速下发未确认（ACK={(ack != null ? ack.Result : -1)}），下次启动时重试");
			// 0x0B 查询：读取下位机实际存储值验证设置生效（ACK 回显）
			await SendCheckedAsync(Control.InfChange.CmdQuerySetRpm);
			return;
		}
		CloseSettingsPanel();
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

	/// <summary>失焦校验：非法/超范围输入回退为当前预览值</summary>
	private void PopupTxtSpeed_LostFocus(object sender, RoutedEventArgs e)
	{
		if (!TryReadPopupSpeed())
			PopupTxtSpeed.Text = ((int)Math.Round(_popupSpeed)).ToString();
	}

	/// <summary>读取输入框并校验（0~99999）；成功则写入 _popupSpeed</summary>
	private bool TryReadPopupSpeed()
	{
		if (int.TryParse(PopupTxtSpeed.Text.Trim(), out int v) && v >= 0 && v <= 99999)
		{
			_popupSpeed = v;
			return true;
		}
		return false;
	}

	// ===== 设置面板：取消 =====
	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		CloseSettingsPanel();
	}
	}
}
