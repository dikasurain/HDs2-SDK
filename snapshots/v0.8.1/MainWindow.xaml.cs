using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
	private DispatcherTimer? _runTimer;
	// 设置面板预览状态（确认前不写入主状态）
	private bool _popupDirPreview = true;
	// 面板拖动
	private bool _isPopupDragging = false;
	private Point _popupDragStart;
	private double _popupStartX;
	private double _popupStartY;

	public MainWindow()
	{
		InitializeComponent();
		// 待机不在字典中（永远无高亮框）
		SetStatus(MotorStatus.Standby);
		SetDigits(DefaultSpeed);
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
			Digit1.Digit = (v / 10000) % 10;
			Digit2.Digit = (v / 1000) % 10;
			Digit3.Digit = (v / 100) % 10;
			Digit4.Digit = (v / 10) % 10;
			Digit5.Digit = v % 10;
		}

		// ===== 状态切换（待机/运行/过载/故障，v0.6 风格 20×20 LED + 18pt 文字） =====
	// 颜色规范：过载黄 #FACC15，故障红 #EF4444
	private static readonly Brush LedOverloadOn = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
	private static readonly Brush LedFaultOn = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

	private void SetStatus(MotorStatus status)
	{
		_status = status;

		// 仅显示错误状态（过载/故障）；待机/运行时隐藏
		switch (status)
		{
			case MotorStatus.Standby:
			case MotorStatus.LowSpeed:
			case MotorStatus.HighSpeed:
				// 无错误：隐藏显示
				LedStandby.Visibility = Visibility.Collapsed;
				LblStandby.Visibility = Visibility.Collapsed;
				break;
			case MotorStatus.Overload:
				LedStandby.Visibility = Visibility.Visible;
				LblStandby.Visibility = Visibility.Visible;
				LedStandby.Fill = LedOverloadOn;
				LedStandby.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFA, 0xCC, 0x15), BlurRadius = 8, ShadowDepth = 0, Direction = 0, Opacity = 0.6 };
				LblStandby.Text = "过载";
				LblStandby.Foreground = Brushes.White;
				break;
			case MotorStatus.Fault:
				LedStandby.Visibility = Visibility.Visible;
				LblStandby.Visibility = Visibility.Visible;
				LedStandby.Fill = LedFaultOn;
				LedStandby.Effect = new DropShadowEffect { Color = Color.FromRgb(0xEF, 0x44, 0x44), BlurRadius = 8, ShadowDepth = 0, Direction = 0, Opacity = 0.6 };
				LblStandby.Text = "故障";
				LblStandby.Foreground = Brushes.White;
				break;
		}
	}

	// ===== 航插/脚踏接入状态 =====
	private static readonly Brush LedConnected = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
	private static readonly Brush LedDisconnected = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

	/// <summary>设置航插接入状态</summary>
	public void SetAviationConnected(bool connected)
	{
		LedAviation.Fill = connected ? LedConnected : LedDisconnected;
		LedAviation.Effect = connected
			? new DropShadowEffect { Color = Color.FromRgb(0x22, 0xC5, 0x5E), BlurRadius = 6, ShadowDepth = 0, Direction = 0, Opacity = 0.6 }
			: null;
	}

	/// <summary>设置脚踏接入状态</summary>
	public void SetFootPedalConnected(bool connected)
	{
		LedFootPedal.Fill = connected ? LedConnected : LedDisconnected;
		LedFootPedal.Effect = connected
			? new DropShadowEffect { Color = Color.FromRgb(0x22, 0xC5, 0x5E), BlurRadius = 6, ShadowDepth = 0, Direction = 0, Opacity = 0.6 }
			: null;
	}

		// ===== 图标切换：Play↔Pause =====
		private void UpdatePowerIcon()
		{
			var iconPlay = BtnPower.Template.FindName("IconPlay", BtnPower) as Path;
			var iconPause = BtnPower.Template.FindName("IconPause", BtnPower) as Path;
			var lblPower = BtnPower.Template.FindName("LblPower", BtnPower) as TextBlock;
			if (iconPlay == null || iconPause == null) return;
			iconPlay.Visibility = _isRunning ? Visibility.Collapsed : Visibility.Visible;
			iconPause.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
			if (lblPower != null) lblPower.Text = _isRunning ? "暂停" : "启动";
		}

		/// <summary>同步设置弹窗中的方向标签（使用预览状态）</summary>
		private void UpdateDirToggleLabel()
		{
			BtnDirToggle.ApplyTemplate();
			var lbl = BtnDirToggle.Template.FindName("LblDirToggle", BtnDirToggle) as TextBlock;
			if (lbl != null) lbl.Text = _popupDirPreview ? "正转" : "反转";
		}

		// ===== 按钮事件 =====
		private void BtnPower_Click(object sender, RoutedEventArgs e)
		{
			// 关闭可能打开的设置面板
			CloseSettingsPanel();
			if (!_isRunning)
		{
			_isRunning = true;
			UpdatePowerIcon();
			SetInputEnabled(false); // 运行中禁用设置按钮
			// 启动时先设为低速（速度从0开始）
			SetStatus(MotorStatus.LowSpeed);
			StartRotor(); // 启动旋转图片
			AnimateSpeed(0, _targetSpeed, 1500, () =>
			{
				_runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
				_runTimer.Tick += (s2, e3) =>
				{
					if (_isRunning)
					{
						var rnd = new Random();
						double noise = (rnd.NextDouble() - 0.5) * 0.1 * _targetSpeed;
						_currentSpeed = _targetSpeed + noise;
						SetDigits(_currentSpeed);
						UpdateSpeedStatus();
					}
				};
				_runTimer.Start();
			});
		}
		else
		{
			_isRunning = false;
			UpdatePowerIcon();
			SetInputEnabled(true); // 停止后恢复设置按钮
			_runTimer?.Stop();
			StopRotor(); // 停止旋转
			SetStatus(MotorStatus.Standby);
			// 停止时还原到默认转速
			AnimateSpeed(_currentSpeed, _targetSpeed, 1000, null);
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

		double durationMs = 60000.0 / Math.Max(1, _currentSpeed) * 1000.0;
		durationMs = Math.Max(200, Math.Min(8000, durationMs));
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
			var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
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
		PopupTxtSpeed.Text = ((int)_targetSpeed).ToString();
		PopupTxtSpeed.SelectAll();
		PopupTxtSpeed.Focus();
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
	private void BtnConfirm_Click(object sender, RoutedEventArgs e)
	{
		// 应用方向
		if (_popupDirPreview != _isForward)
		{
			_isForward = _popupDirPreview;
			if (_isRunning) StartRotor();
		}
		// 应用转速
		if (double.TryParse(PopupTxtSpeed.Text, out double v))
		{
			_targetSpeed = Math.Max(0, Math.Min(99999, v));
			if (!_isRunning)
			{
				AnimateSpeed(_currentSpeed, _targetSpeed, 800, null);
			}
		}
		CloseSettingsPanel();
	}

	// ===== 设置面板：取消 =====
	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		CloseSettingsPanel();
	}

		// ===== 设置弹窗：转速输入 Enter 触发确认 =====
		private void PopupTxtSpeed_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == System.Windows.Input.Key.Enter)
			{
				BtnConfirm_Click(sender, e);
			}
		}
	}
}
