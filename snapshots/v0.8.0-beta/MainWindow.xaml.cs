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
		// ===== 表盘参数（按规格） =====
		private const double CanvasSize = 180;
		private const double Center = 90;
		private const double ArcRadius = 80;
		private const double ArcWidth = 4;
		// 270° 弧带：225° → 135°（顺时针）
		private const double StartDeg = 135;   // 起点角度（右下，4点钟方向）
		private const double EndDeg = 405;      // 终点角度（225°+270°，左下，8点钟方向）
		private const int TickCount = 60;
		private const int MajorEvery = 10;
		private const double MajorLen = 6;
		private const double MinorLen = 3;

		// 6段渐变色
		private static readonly Color[] ArcColors =
		{
			Color.FromRgb(0x1E, 0x5F, 0xBF),  // 深蓝
			Color.FromRgb(0x3A, 0x9F, 0xE0),  // 青蓝
			Color.FromRgb(0x5F, 0xE0, 0xE0),  // 青绿
			Color.FromRgb(0x7F, 0xE8, 0x9A),  // 浅绿
			Color.FromRgb(0xF5, 0xC5, 0x42),  // 黄
			Color.FromRgb(0xF5, 0x78, 0x42),  // 橙红
		};

		// 状态枚举（3状态灯：待机/高速/低速；运行/过载/故障通过状态条联动）
	private enum MotorStatus { Standby, HighSpeed, LowSpeed, Overload, Fault }
	private MotorStatus _status = MotorStatus.Standby;

	// 运行状态
	private bool _isRunning = false;
	private bool _isForward = true;  // true=正转(CW), false=反转(CCW)
	private double _targetSpeed = 60000;
	private double _currentSpeed = 0;
	private DispatcherTimer? _runTimer;

	public MainWindow()
	{
		InitializeComponent();
		BuildGauge();
		BuildParticleWaves();
		// 待机不在字典中（永远无高亮框）
		SetStatus(MotorStatus.Standby);
		SetDigits(60000);
		SourceInitialized += MainWindow_SourceInitialized;
		Loaded += MainWindow_Loaded;
	}

		private void MainWindow_Loaded(object sender, RoutedEventArgs e)
		{
			// 模板应用后初始化图标
			BtnPower.ApplyTemplate();
			BtnDir.ApplyTemplate();
			UpdatePowerIcon();
			UpdateDirIcon();
		}

		// ===== 取消 Aero Snap =====
		private const int WM_WINDOWPOSCHANGING = 0x0046;
		private const int SWP_NOSIZE = 0x0001;

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
				int flags = Marshal.ReadInt32(lParam, 12);
				flags |= SWP_NOSIZE;
				Marshal.WriteInt32(lParam, 12, flags);
				handled = false;
			}
			return IntPtr.Zero;
		}

		private void Grid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			DragMove();
		}

		// ===== 构建表盘 =====
		private void BuildGauge()
		{
			// 1. 渐变弧带（6段，270°）
			for (int i = 0; i < 6; i++)
			{
				double segStart = StartDeg + i * 45;
				double segEnd = segStart + 45;
				var path = BuildArcPath(segStart, segEnd, ArcRadius, ArcWidth);
				path.Stroke = new SolidColorBrush(ArcColors[i]);
				path.StrokeThickness = ArcWidth;
				path.StrokeStartLineCap = PenLineCap.Round;
				path.StrokeEndLineCap = PenLineCap.Round;
				path.Effect = new DropShadowEffect
				{
					Color = ArcColors[i],
					BlurRadius = 5,
					ShadowDepth = 0,
					Direction = 0,
					Opacity = 0.7
				};
				GaugeCanvas.Children.Add(path);
			}

			// 2. 刻度（60根）
			for (int i = 0; i < TickCount; i++)
			{
				bool isMajor = (i % MajorEvery) == 0;
				double deg = StartDeg + (i * 270.0 / (TickCount - 1));
				double len = isMajor ? MajorLen : MinorLen;
				double r1 = ArcRadius - 8;
				double r2 = r1 - len;
				double rad = deg * Math.PI / 180;
				double x1 = Center + r1 * Math.Cos(rad);
				double y1 = Center + r1 * Math.Sin(rad);
				double x2 = Center + r2 * Math.Cos(rad);
				double y2 = Center + r2 * Math.Sin(rad);
				var line = new Line
				{
					X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
					Stroke = new SolidColorBrush(Color.FromArgb(
						isMajor ? (byte)0xB3 : (byte)0x59, 0xFF, 0xFF, 0xFF)),
					StrokeThickness = isMajor ? 1 : 0.5
				};
				GaugeCanvas.Children.Add(line);
			}

			// 3. 白色指示点（135°处，弧带最右端）
			double indRad = StartDeg * Math.PI / 180;
			double ix = Center + ArcRadius * Math.Cos(indRad);
			double iy = Center + ArcRadius * Math.Sin(indRad);
			var indicator = new Ellipse
			{
				Width = 5, Height = 5,
				Fill = Brushes.White,
				Effect = new DropShadowEffect
				{
					Color = Colors.White,
					BlurRadius = 7,
					ShadowDepth = 0,
					Direction = 0,
					Opacity = 0.7
				}
			};
			Canvas.SetLeft(indicator, ix - 2.5);
			Canvas.SetTop(indicator, iy - 2.5);
			GaugeCanvas.Children.Add(indicator);
		}

		// 构建弧段路径
		private Path BuildArcPath(double startDeg, double endDeg, double radius, double width)
		{
			double sRad = startDeg * Math.PI / 180;
			double eRad = endDeg * Math.PI / 180;
			double sx = Center + radius * Math.Cos(sRad);
			double sy = Center + radius * Math.Sin(sRad);
			double ex = Center + radius * Math.Cos(eRad);
			double ey = Center + radius * Math.Sin(eRad);
			double sweep = endDeg - startDeg;
			bool isLarge = sweep > 180;
			var figure = new PathFigure { StartPoint = new Point(sx, sy), IsClosed = false };
			figure.Segments.Add(new ArcSegment
			{
				Point = new Point(ex, ey),
				Size = new Size(radius, radius),
				IsLargeArc = isLarge,
				SweepDirection = SweepDirection.Clockwise
			});
			return new Path { Data = new PathGeometry { Figures = { figure } } };
		}

		// ===== 粒子波纹（底部60°扇形） =====
		private void BuildParticleWaves()
		{
			var rng = new Random(42);
			// 3条并行水平波浪，波峰高≈4px
			for (int wave = 0; wave < 3; wave++)
			{
				double baseY = Center + 25 + wave * 6;
				for (int i = 0; i < 120; i++)
				{
					// 60°扇形：300° → 360°（即 -60° → 0°，底部偏右）
					double ang = 30 + (rng.NextDouble() * 120); // 30°-150°（底部扇形）
					double rad = ang * Math.PI / 180;
					double r = ArcRadius - 15 + rng.NextDouble() * 8;
					double px = Center + r * Math.Cos(rad);
					double py = Center + r * Math.Sin(rad);
					// 仅保留底部
					if (py < Center) continue;
					// 颜色：60% #3A9FE0、30% #5FE0E0、10% #7FE89A
					Color color;
					double c = rng.NextDouble();
					if (c < 0.6) color = Color.FromRgb(0x3A, 0x9F, 0xE0);
					else if (c < 0.9) color = Color.FromRgb(0x5F, 0xE0, 0xE0);
					else color = Color.FromRgb(0x7F, 0xE8, 0x9A);
					var dot = new Ellipse
					{
						Width = 1, Height = 1,
						Fill = new SolidColorBrush(color),
						Opacity = 0.35 + rng.NextDouble() * 0.25
					};
					Canvas.SetLeft(dot, px);
					Canvas.SetTop(dot, py);
					ParticleCanvas.Children.Add(dot);
				}
			}
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

		// ===== 状态切换（待机/高速/低速合一控件 + 状态条联动运行/过载/故障） =====
	private void SetStatus(MotorStatus status)
	{
		_status = status;

		// 高速/低速合一控件：根据状态切换文字、颜色、辉光
		switch (status)
		{
			case MotorStatus.Standby:
			// 待机：浅灰未激活
			LedSpeed.Fill = new SolidColorBrush(Color.FromRgb(0xA0, 0xA8, 0xB4));
			LedSpeed.Effect = null;
			LblSpeed.Text = "低速";
			LblSpeed.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9B, 0xB5));
			StatusSpeedBorderBR.BorderBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA8, 0xB4));
			StatusSpeed.Effect = null;
			StatusSpeedOuter.Effect = null;
			// 待机控件显示"待机"（灰色）
			LedStandby.Fill = new SolidColorBrush(Color.FromRgb(0xB0, 0xB8, 0xC4));
			LedStandby.Effect = null;
			LblStandby.Text = "待机";
			LblStandby.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9B, 0xB5));
			break;
		case MotorStatus.LowSpeed:
			// 低速：左上亮灰 + 右下蓝 #3FA9FF + 外侧辉光
			LedSpeed.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xFF));
			LedSpeed.Effect = null;
			LblSpeed.Text = "低速";
			LblSpeed.Foreground = Brushes.White;
			StatusSpeedBorderBR.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xFF));
			StatusSpeed.Effect = null;
			StatusSpeedOuter.Effect = new DropShadowEffect { Color = Color.FromRgb(0x3F, 0xA9, 0xFF), BlurRadius = 12, ShadowDepth = 0, Direction = 0, Opacity = 0.85 };
			// 待机控件启动后显示"运行"（绿色）
			LedStandby.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xE8, 0x9A));
			LedStandby.Effect = new DropShadowEffect { Color = Color.FromRgb(0x3F, 0xE8, 0x9A), BlurRadius = 3, ShadowDepth = 0, Direction = 0, Opacity = 0.5 };
			LblStandby.Text = "运行";
			LblStandby.Foreground = Brushes.White;
			break;
		case MotorStatus.HighSpeed:
			// 高速：左上亮灰 + 右下亮橙 #FFB84A + 外侧辉光
			LedSpeed.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4A));
			LedSpeed.Effect = null;
			LblSpeed.Text = "高速";
			LblSpeed.Foreground = Brushes.White;
			StatusSpeedBorderBR.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4A));
			StatusSpeed.Effect = null;
			StatusSpeedOuter.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0xB8, 0x4A), BlurRadius = 12, ShadowDepth = 0, Direction = 0, Opacity = 0.85 };
			// 待机控件启动后显示"运行"（绿色）
			LedStandby.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xE8, 0x9A));
			LedStandby.Effect = new DropShadowEffect { Color = Color.FromRgb(0x3F, 0xE8, 0x9A), BlurRadius = 3, ShadowDepth = 0, Direction = 0, Opacity = 0.5 };
			LblStandby.Text = "运行";
			LblStandby.Foreground = Brushes.White;
			break;
	}

		// 更新底部状态条（运行/过载/故障联动变色，双层辉光）
		switch (status)
		{
			case MotorStatus.Standby:
				StatusPillOuter.Visibility = Visibility.Collapsed;
				break;
			case MotorStatus.HighSpeed:
			case MotorStatus.LowSpeed:
				// 运行中：绿色"运行正常" + 紧凑辉光(#3FE89A，被控件遮住)
				StatusPillOuter.Visibility = Visibility.Visible;
				LblStatusPill.Text = "运行正常";
				LblStatusPill.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFF));
				StatusPill.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x28));
				StatusPill.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xE8, 0x9A));
				StatusPill.BorderThickness = new Thickness(1.5);
				StatusPill.Effect = new DropShadowEffect { Color = Color.FromRgb(0x3F, 0xE8, 0x9A), BlurRadius = 2, ShadowDepth = 0, Direction = 0, Opacity = 0.6 };
				StatusPillOuter.Effect = null;
				break;
			case MotorStatus.Overload:
				// 过载：黄色"过载警示" + 紧凑辉光(#FFC107，被控件遮住)
				StatusPillOuter.Visibility = Visibility.Visible;
				LblStatusPill.Text = "过载警示";
				LblStatusPill.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
				StatusPill.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x14, 0x08));
				StatusPill.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
				StatusPill.BorderThickness = new Thickness(1.5);
				StatusPill.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0xC1, 0x07), BlurRadius = 2, ShadowDepth = 0, Direction = 0, Opacity = 0.6 };
				StatusPillOuter.Effect = null;
				break;
			case MotorStatus.Fault:
				// 故障：红色"故障停机" + 紧凑辉光(#FF4444，被控件遮住)
				StatusPillOuter.Visibility = Visibility.Visible;
				LblStatusPill.Text = "故障停机";
				LblStatusPill.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
				StatusPill.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x0A, 0x0A));
				StatusPill.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
				StatusPill.BorderThickness = new Thickness(1.5);
				StatusPill.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x44, 0x44), BlurRadius = 2, ShadowDepth = 0, Direction = 0, Opacity = 0.6 };
				StatusPillOuter.Effect = null;
				break;
		}
	}

		// ===== 图标切换：Play↔Pause, CW↔CCW =====
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

		private void UpdateDirIcon()
		{
			var iconCw = BtnDir.Template.FindName("IconCw", BtnDir) as Path;
			var iconCcw = BtnDir.Template.FindName("IconCcw", BtnDir) as Path;
			var lblDir = BtnDir.Template.FindName("LblDir", BtnDir) as TextBlock;
			if (iconCw == null || iconCcw == null) return;
			iconCw.Visibility = _isForward ? Visibility.Visible : Visibility.Collapsed;
			iconCcw.Visibility = _isForward ? Visibility.Collapsed : Visibility.Visible;
			if (lblDir != null) lblDir.Text = _isForward ? "正转" : "反转";
		}

		// ===== 按钮事件 =====
		private void BtnPower_Click(object sender, RoutedEventArgs e)
		{
			if (!_isRunning)
			{
				_isRunning = true;
				UpdatePowerIcon();
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
				_runTimer?.Stop();
				StopRotor(); // 停止旋转
				SetStatus(MotorStatus.Standby);
				AnimateSpeed(_currentSpeed, 0, 1000, null);
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

		private void BtnDir_Click(object sender, RoutedEventArgs e)
		{
			// 正转/反转切换
			_isForward = !_isForward;
			UpdateDirIcon();
			// 运行中切换方向时重新启动旋转
			if (_isRunning) StartRotor();
		}

		private void BtnSetSpeed_Click(object sender, RoutedEventArgs e)
		{
			// 设置按钮作为确认：读取输入值，中间数码管动画过渡到新设定转速
			if (double.TryParse(TxtSpeed.Text, out double v))
			{
				_targetSpeed = Math.Max(0, Math.Min(99999, v));
				TxtSpeed.Text = ((int)_targetSpeed).ToString();
				if (!_isRunning)
				{
					AnimateSpeed(_currentSpeed, _targetSpeed, 800, null);
				}
			}
		}

		private void TxtSpeed_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == System.Windows.Input.Key.Enter)
			{
				if (double.TryParse(TxtSpeed.Text, out double v))
				{
					_targetSpeed = Math.Max(0, Math.Min(99999, v));
					TxtSpeed.Text = ((int)_targetSpeed).ToString();
					// 中间数码管用动画过渡到新设定值
					if (!_isRunning)
					{
						AnimateSpeed(_currentSpeed, _targetSpeed, 800, null);
						System.Windows.Input.Keyboard.ClearFocus();
					}
				}
			}
		}
	}
}
