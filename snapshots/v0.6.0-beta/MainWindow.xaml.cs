using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace HDs2_SDK
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		// 表盘参数（尺寸 120×120）
		private const double GaugeSize = 120;
		private const double RingRadius = GaugeSize / 2 - 4; // 56
		private const double TickOuter = RingRadius - 3;     // 53，刻度外端紧贴圆环内侧
		private const double MajorLen = 8;
		private const double MinorLen = 5;
		private const double StartDeg = -135; // 左下起点
		private const double EndDeg = 135;    // 右下终点，270° 跨度，底部留 90° 缺口

		// 浅蓝色系（正转）
		private static readonly Color RingColor = Color.FromRgb(0x4A, 0x90, 0xE2);
		private static readonly Color MajorColor = Color.FromRgb(0x3A, 0x7B, 0xC8);
		private static readonly Color MinorColor = Color.FromRgb(0x8A, 0xB4, 0xE8);
		// 琥珀色系（反转）
		private static readonly Color MajorColorRev = Color.FromRgb(0xC8, 0x84, 0x3A);
		private static readonly Color MinorColorRev = Color.FromRgb(0xE8, 0xC8, 0x8A);
		// 光条底色（未点亮浅色）
		private static readonly Color RingDimColor = Color.FromRgb(0x2A, 0x40, 0x60);
		private static readonly Color RingDimColorRev = Color.FromRgb(0x4A, 0x38, 0x20);
		// 刻度未点亮（暗）
		private static readonly Color TickDim = Color.FromRgb(0x1E, 0x2D, 0x3D);
		private static readonly Color TickDimRev = Color.FromRgb(0x2D, 0x25, 0x1A);
		// 刻度点亮（亮）
		private static readonly Color TickLitMajor = Color.FromRgb(0x6B, 0xB0, 0xFF);
		private static readonly Color TickLitMinor = Color.FromRgb(0x4A, 0x90, 0xE2);
		private static readonly Color TickLitMajorRev = Color.FromRgb(0xFF, 0xB8, 0x4A);
		private static readonly Color TickLitMinorRev = Color.FromRgb(0xE2, 0xA0, 0x4A);

		public MainWindow()
		{
			InitializeComponent();
			BuildGauge();
			// 按键事件绑定
			BtnPower.Click += BtnPower_Click;
			BtnDir.Click += BtnDir_Click;
			BtnSetSpeed.Click += BtnSetSpeed_Click;
			// 初始：停止状态，刻度全暗，数码管显示设定转速
			SetStatus(MotorStatus.Stop);
			BtnDir.Content = "正";
			UpdateSpeedLevel();
			TxtSpeed.Text = ((int)_targetSpeed).ToString();
			AnimatedValue = _targetSpeed; // 同步依赖属性值，后续 SetGaugeValue 的 From 才正确
			SourceInitialized += MainWindow_SourceInitialized;
		}

		// ===== 取消拖动到屏幕边界时的 Aero Snap 放大行为 =====
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
			HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
		}

		private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			// 拦截窗口尺寸变化：强制保留原尺寸，阻止 Aero Snap 贴边放大
			if (msg == WM_WINDOWPOSCHANGING)
			{
				var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
				pos.flags |= SWP_NOSIZE;
				Marshal.StructureToPtr(pos, lParam, false);
			}
			return IntPtr.Zero;
		}

		private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			// 拖动无边框窗口
			DragMove();
		}

		// ===== 数据灯（运行绿/过载黄/故障红/停止灰，合一显示） =====
		public enum MotorStatus { Stop, Run, Overload, Fault }
		private static readonly Brush LedOff = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
		private static readonly Brush LedFaultOn = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
		private static readonly Brush LedOverloadOn = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
		private static readonly Brush LedRunOn = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

		/// <summary>设置数据灯状态（颜色与文字随状态变化：待机/运行/过载/故障）</summary>
		public void SetStatus(MotorStatus s)
		{
			LedStatus.Fill = s switch
			{
				MotorStatus.Run => LedRunOn,
				MotorStatus.Overload => LedOverloadOn,
				MotorStatus.Fault => LedFaultOn,
				_ => LedOff,
			};
			LblStatus.Text = s switch
			{
				MotorStatus.Run => "运行",
				MotorStatus.Overload => "过载",
				MotorStatus.Fault => "故障",
				_ => "待机",
			};
		}

		//控制表盘以及光条
		private const double GaugeMaxValue = 99999;            // 满量程（5 位数码管）
		private const int SmoothMs = 450;                      // 平滑缓出时长
		private const int BounceMs = 600;                      // 弹跳回弹时长
		private Path? _ring;                                   // 底环引用（未点亮的光条浅色）
		private readonly List<(Line line, double angle, bool isMajor)> _ticks = new();
		private static readonly Brush RingReverse = new SolidColorBrush(Color.FromRgb(0xE2, 0xA0, 0x4A)); // 反转=琥珀色
		private double _gaugeValue;                             // 当前表盘数值

		// 缓动动画驱动值：DoubleAnimation 改变它 → 回调里即时刷新光弧与数码管
		public static readonly DependencyProperty AnimatedValueProperty =
			DependencyProperty.Register(nameof(AnimatedValue), typeof(double), typeof(MainWindow),
				new PropertyMetadata(0.0, OnAnimatedValueChanged));
		public double AnimatedValue
		{
			get => (double)GetValue(AnimatedValueProperty);
			set => SetValue(AnimatedValueProperty, value);
		}
		private static void OnAnimatedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
			=> ((MainWindow)d).UpdateGaugeDisplay((double)e.NewValue);

		/// <summary>表盘动画缓动方式</summary>
		public enum GaugeEaseMode
		{
			/// <summary>三次缓出：平滑到位，无回弹</summary>
			Smooth,
			/// <summary>弹性缓出：过冲后回弹</summary>
			Bounce,
		}

		/// <summary>
		/// 设置表盘目标数值：按 mode 选择缓动方式，从当前值过渡到目标值，
		/// 动画期间光弧递进延伸、数码管逐位滚动。
		/// </summary>
		/// <param name="value">目标值（自动限制到 0~GaugeMaxValue）</param>
		/// <param name="mode">Smooth=平滑缓出，Bounce=弹跳回弹</param>
		public void SetGaugeValue(double value, GaugeEaseMode mode = GaugeEaseMode.Smooth)
		{
			double target = Math.Max(0, Math.Min(GaugeMaxValue, value));
			EasingFunctionBase ease;
			int ms;
			if (mode == GaugeEaseMode.Bounce)
			{
				ease = new ElasticEase
				{
					Oscillations = 2,   // 回弹次数
					Springiness = 3,    // 弹簧刚度：越小过冲越大
					EasingMode = EasingMode.EaseOut,
				};
				ms = BounceMs;
			}
			else
			{
				ease = new CubicEase { EasingMode = EasingMode.EaseOut };
				ms = SmoothMs;
			}
			var anim = new DoubleAnimation
			{
				From = AnimatedValue,
				To = target,
				Duration = TimeSpan.FromMilliseconds(ms),
				EasingFunction = ease,
			};
			BeginAnimation(AnimatedValueProperty, anim);
		}

		/// <summary>
		/// 即时刷新表盘显示：按给定数值点亮刻度并更新数码管。
		/// </summary>
		private void UpdateGaugeDisplay(double value)
		{
			_gaugeValue = Math.Max(0, Math.Min(GaugeMaxValue, value));
			// 停止过渡中：只更新数码管，刻度由淡出动画控制
			if (!_isStopping)
			{
				double p = _gaugeValue / GaugeMaxValue;             // 0~1 进度
				// 正转：起点(-135°)→终点(+135°)；反转：终点→起点
				double angle = _isForward
					? StartDeg + p * (EndDeg - StartDeg)
					: EndDeg - p * (EndDeg - StartDeg);
				// 刻度逐个点亮：停止时全暗，运行时从起点亮到当前角度
				Color litMaj = _isForward ? TickLitMajor : TickLitMajorRev;
				Color litMin = _isForward ? TickLitMinor : TickLitMinorRev;
				Color dim = _isForward ? TickDim : TickDimRev;
				foreach (var (tick, tickAngle, isMajor) in _ticks)
				{
					if (tick.Stroke is not SolidColorBrush brush) continue;
					brush.BeginAnimation(SolidColorBrush.ColorProperty, null); // 取消可能正在运行的动画
					if (!_ticksActive || p < 0.002)
					{
						brush.Color = dim;
						continue;
					}
					bool isLit = _isForward
						? tickAngle <= angle + 0.01
						: tickAngle >= angle - 0.01;
					brush.Color = isLit ? (isMajor ? litMaj : litMin) : dim;
				}
			}
			// 5 位数码管：万→个
			int iv = (int)Math.Round(_gaugeValue);
			Digit1.Digit = (iv / 10000) % 10;
			Digit2.Digit = (iv / 1000) % 10;
			Digit3.Digit = (iv / 100) % 10;
			Digit4.Digit = (iv / 10) % 10;
			Digit5.Digit = iv % 10;
		}





		// ===== 电机控制：启停 / 速度输入 / 运行误差 / 正反转 / 高低速 =====
		private static readonly Random _rng = new Random();
		private const double DefaultSpeed = 60000;   // 默认转速
		private const double ErrorPercent = 0.05;    // 运行时 ±5% 误差
		private const double HighSpeedThreshold = 50000; // 高/低速分界

		private bool _isRunning;                      // 运行状态
		private bool _ticksActive;                    // 刻度是否激活（运行时亮起，停止时全暗）
		private bool _isStopping;                     // 正在停止中（刻度淡出，数码管过渡）
		private double _targetSpeed = DefaultSpeed;   // 设定转速
		private bool _isForward = true;               // 正反转（true=正）
		private DispatcherTimer? _runTimer;           // 运行时误差刷新定时器

		/// <summary>启停合一：点击切换运行/停止，按钮文字随之变化</summary>
		private void BtnPower_Click(object sender, RoutedEventArgs e)
		{
			if (_isRunning)
			{
				// 停止：刻度淡出 + 数码管平滑回到设定转速
				_isRunning = false;
				_isStopping = true;
				SetStatus(MotorStatus.Stop);
				SetInputEnabled(true);
				StopRunError();
				// 数码管：当前值 → 设定值
				BeginAnimation(AnimatedValueProperty, null);
				var stopAnim = new DoubleAnimation
				{
					From = AnimatedValue,
					To = _targetSpeed,
					Duration = TimeSpan.FromMilliseconds(SmoothMs),
					EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
				};
				stopAnim.Completed += (s, e) =>
				{
					if (!_isRunning)
					{
						_isStopping = false;
						_ticksActive = false;
					}
				};
				BeginAnimation(AnimatedValueProperty, stopAnim);
				// 刻度：当前颜色 → 暗色（淡出）
				Color dim = _isForward ? TickDim : TickDimRev;
				foreach (var (tick, _, _) in _ticks)
				{
					if (tick.Stroke is not SolidColorBrush brush) continue;
					var fadeAnim = new ColorAnimation
					{
						To = dim,
						Duration = TimeSpan.FromMilliseconds(SmoothMs),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
					};
					brush.BeginAnimation(SolidColorBrush.ColorProperty, fadeAnim);
				}
				BtnPower.Content = "启";
			}
			else
			{
				// 启动：刻度从 0 逐步点亮到目标值
				_isRunning = true;
				_ticksActive = true;
				_isStopping = false;
				SetStatus(MotorStatus.Run);
				SetInputEnabled(false);
				// 直接构造动画，From 硬编码 0，避免 HoldEnd 导致读取旧值
				BeginAnimation(AnimatedValueProperty, null);
				var startAnim = new DoubleAnimation
				{
					From = 0,
					To = _targetSpeed,
					Duration = TimeSpan.FromMilliseconds(SmoothMs),
					EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
				};
				BeginAnimation(AnimatedValueProperty, startAnim);
				StartRunError();
				BtnPower.Content = "停";
			}
		}

		/// <summary>运行中禁用正反切换与转速输入（置灰），停止后恢复</summary>
		private void SetInputEnabled(bool enabled)
		{
			BtnDir.IsEnabled = enabled;
			TxtSpeed.IsEnabled = enabled;
			BtnSetSpeed.IsEnabled = enabled;
		}

		/// <summary>运行时定时刷新：显示 = 设定值 ± 5% 随机误差</summary>
		private void StartRunError(int intervalMs = 500)
		{
			_runTimer?.Stop();
			_runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
			_runTimer.Tick += (s, e) =>
			{
				if (!_isRunning) return;
				double err = (_rng.NextDouble() * 2 - 1) * ErrorPercent * _targetSpeed;
				SetGaugeValue(_targetSpeed + err, GaugeEaseMode.Smooth);
			};
			_runTimer.Start();
		}

		private void StopRunError()
		{
			_runTimer?.Stop();
			_runTimer = null;
		}

		// ===== 速度输入：点击"设"按钮或回车应用输入值 =====
		private void BtnSetSpeed_Click(object sender, RoutedEventArgs e)
			=> ApplySpeedInput();

		private void TxtSpeed_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter) ApplySpeedInput();
		}

		/// <summary>解析输入框数值，更新设定转速、高低速识别与数码管显示</summary>
		private void ApplySpeedInput()
		{
			if (_isRunning) return;
			if (int.TryParse(TxtSpeed.Text, out int v))
			{
				_targetSpeed = Math.Max(0, Math.Min(GaugeMaxValue, v));
				TxtSpeed.Text = ((int)_targetSpeed).ToString();
				UpdateSpeedLevel();
				// 停止时数码管平滑过渡到新设定值（刻度保持暗）
				SetGaugeValue(_targetSpeed, GaugeEaseMode.Smooth);
			}
		}

		// ===== 正反转切换（点击按钮切换；运行中禁止） =====
		private void BtnDir_Click(object sender, RoutedEventArgs e)
		{
			if (_isRunning) return;
			_isForward = !_isForward;
			UpdateDirection();
		}

		private void UpdateDirection()
		{
			BtnDir.Content = _isForward ? "正" : "反";
			// 目标颜色：正转蓝 / 反转琥珀
			Color targetRingDim = _isForward ? RingDimColor : RingDimColorRev;
			var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
			var dur = TimeSpan.FromMilliseconds(300);
			// 底环颜色动画
			if (_ring?.Stroke is SolidColorBrush ringBrush)
			{
				ringBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
				{
					To = targetRingDim,
					Duration = dur,
					EasingFunction = ease,
				});
			}
			// 刻度颜色动画（按新方向的点亮状态逐根过渡；未激活时全暗）
			double p = _gaugeValue / GaugeMaxValue;
			double angle = _isForward
				? StartDeg + p * (EndDeg - StartDeg)
				: EndDeg - p * (EndDeg - StartDeg);
			Color litMaj = _isForward ? TickLitMajor : TickLitMajorRev;
			Color litMin = _isForward ? TickLitMinor : TickLitMinorRev;
			Color dimC = _isForward ? TickDim : TickDimRev;
			foreach (var (tick, tickAngle, isMajor) in _ticks)
			{
				bool isLit = _ticksActive && (_isForward
					? tickAngle <= angle + 0.01
					: tickAngle >= angle - 0.01);
				Color target = isLit ? (isMajor ? litMaj : litMin) : dimC;
				if (tick.Stroke is SolidColorBrush brush)
				{
					brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
					{
						To = target,
						Duration = dur,
						EasingFunction = ease,
					});
				}
			}
		}

		// ===== 高低速识别（左侧状态灯：高速琥珀 / 低速蓝） =====
		private static readonly Brush SpeedHighColor = new SolidColorBrush(Color.FromRgb(0xE2, 0xA0, 0x4A));
		private static readonly Brush SpeedLowColor = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
		private void UpdateSpeedLevel()
		{
			bool high = _targetSpeed >= HighSpeedThreshold;
			LedSpeed.Fill = high ? SpeedHighColor : SpeedLowColor;
			LblSpeed.Text = high ? "高速" : "低速";
		}









		/// <summary>
		/// 构建表盘：270° 光条底环（浅色）+ 递进光弧（亮色带辉光）+ 内侧刻度
		/// </summary>
		private void BuildGauge()
		{
			double cx = GaugeSize / 2;
			double cy = GaugeSize / 2;

			// 外圈 270° 底环（未点亮的光条浅色）
			double sx = cx + RingRadius * Math.Sin(StartDeg * Math.PI / 180);
			double sy = cy - RingRadius * Math.Cos(StartDeg * Math.PI / 180);
			double ex = cx + RingRadius * Math.Sin(EndDeg * Math.PI / 180);
			double ey = cy - RingRadius * Math.Cos(EndDeg * Math.PI / 180);
			var figure = new PathFigure { StartPoint = new Point(sx, sy) };
			figure.Segments.Add(new ArcSegment
			{
				Point = new Point(ex, ey),
				Size = new Size(RingRadius, RingRadius),
				IsLargeArc = true,
				SweepDirection = SweepDirection.Clockwise,
			});
			var ring = new Path
			{
				Data = new PathGeometry { Figures = { figure } },
				Stroke = new SolidColorBrush(RingDimColor),
				StrokeThickness = 3,
			};
			_ring = ring;
			GaugeCanvas.Children.Add(ring);

			// 内侧刻度线：每 5° 一根，主刻度每 30°，初始全暗（未点亮）
			for (double theta = StartDeg; theta <= EndDeg + 0.0001; theta += 5)
			{
				double k = (theta - StartDeg) / 30.0;
				bool isMajor = Math.Abs(k - Math.Round(k)) < 0.001;
				double len = isMajor ? MajorLen : MinorLen;
				double rInner = TickOuter - len;
				double a = theta * Math.PI / 180;
				double x1 = cx + TickOuter * Math.Sin(a);
				double y1 = cy - TickOuter * Math.Cos(a);
				double x2 = cx + rInner * Math.Sin(a);
				double y2 = cy - rInner * Math.Cos(a);
				var tick = new Line
				{
					X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
					Stroke = new SolidColorBrush(TickDim),
					StrokeThickness = isMajor ? 1.5 : 1,
				};
				GaugeCanvas.Children.Add(tick);
				_ticks.Add((tick, theta, isMajor));
			}
		}
	}
}
