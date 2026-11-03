using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
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

		// 浅蓝色系
		private static readonly Color RingColor = Color.FromRgb(0x4A, 0x90, 0xE2);
		private static readonly Color MajorColor = Color.FromRgb(0x3A, 0x7B, 0xC8);
		private static readonly Color MinorColor = Color.FromRgb(0x8A, 0xB4, 0xE8);

		public MainWindow()
		{
			InitializeComponent();
			BuildGauge();
			// 按键事件绑定
			BtnStart.Click += BtnStart_Click;
			BtnStop.Click += BtnStop_Click;
			BtnSpeedUp.PreviewMouseLeftButtonDown += BtnSpeed_Down;
			BtnSpeedUp.PreviewMouseLeftButtonUp += BtnSpeed_Up;
			BtnSpeedDown.PreviewMouseLeftButtonDown += BtnSpeed_Down;
			BtnSpeedDown.PreviewMouseLeftButtonUp += BtnSpeed_Up;
			// 初始：停止状态，显示默认转速 60000（无误差），灯全灭
			SetFault(false);
			SetOverload(false);
			SetRun(false);
			SetPort(false);
			SetGaugeValue(_targetSpeed, GaugeEaseMode.Smooth);
		}

		private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			// 拖动无边框窗口
			DragMove();
		}

		// ===== 状态灯控制（故障/过载/运行） =====
		// 灭态灰色
		private static readonly Brush LedOff = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
		// 亮态正色：故障红、过载黄、运行绿
		private static readonly Brush LedFaultOn = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
		private static readonly Brush LedOverloadOn = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
		private static readonly Brush LedRunOn = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

		/// <summary>故障灯开关（on=红，off=灰）</summary>
		public void SetFault(bool on) => LedFault.Fill = on ? LedFaultOn : LedOff;

		/// <summary>过载灯开关（on=黄，off=灰）</summary>
		public void SetOverload(bool on) => LedOverload.Fill = on ? LedOverloadOn : LedOff;

		/// <summary>运行灯开关（on=绿，off=灰）</summary>
		public void SetRun(bool on) => LedRun.Fill = on ? LedRunOn : LedOff;

		// 插口识别光环：接入=绿（带辉光），未接入=灰
		private static readonly Brush PortOn = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
		private static readonly Brush PortOff = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
		/// <summary>插口识别光环开关（on=绿+辉光，off=灰）</summary>
		public void SetPort(bool on)
		{
			LedPort.Stroke = on ? PortOn : PortOff;
			LedPort.Effect = on
				? new DropShadowEffect { Color = Color.FromRgb(0x22, 0xC5, 0x5E), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 }
				: null;
		}

		//控制表盘以及刻度圆球
		private const double BallRadius = TickOuter - MajorLen; // 圆球滑动半径（刻度内端）
		private const double BallSize = 4;                      // 圆球半径
		private const double GaugeMaxValue = 99999;            // 满量程（5 位数码管）
		private const int SmoothMs = 450;                      // 平滑缓出时长
		private const int BounceMs = 600;                      // 弹跳回弹时长
		private Ellipse? _ball;                                // 圆球引用（用于动态移动）
		private double _gaugeValue;                             // 当前表盘数值

		// 缓动动画驱动值：DoubleAnimation 改变它 → 回调里即时刷新圆球与数码管
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
		/// 动画期间圆球沿弧线滑动、数码管逐位滚动。
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
		/// 即时刷新表盘显示（无动画）：按给定数值移动圆球并更新数码管。
		/// </summary>
		private void UpdateGaugeDisplay(double value)
		{
			_gaugeValue = Math.Max(0, Math.Min(GaugeMaxValue, value));
			double p = _gaugeValue / GaugeMaxValue;             // 0~1 进度
			double angle = StartDeg + p * (EndDeg - StartDeg);   // 映射到角度
			double a = angle * Math.PI / 180;
			double cx = GaugeSize / 2;
			double cy = GaugeSize / 2;
			double bx = cx + BallRadius * Math.Sin(a);
			double by = cy - BallRadius * Math.Cos(a);
			if (_ball != null)
			{
				Canvas.SetLeft(_ball, bx - BallSize);
				Canvas.SetTop(_ball, by - BallSize);
			}
			// 5 位数码管：万→个
			int iv = (int)Math.Round(_gaugeValue);
			Digit1.Digit = (iv / 10000) % 10;
			Digit2.Digit = (iv / 1000) % 10;
			Digit3.Digit = (iv / 100) % 10;
			Digit4.Digit = (iv / 10) % 10;
			Digit5.Digit = iv % 10;
		}





		// ===== 电机控制：启停 / 加减 / 运行误差 =====
		private static readonly Random _rng = new Random();
		private const double DefaultSpeed = 60000;   // 默认转速
		private const double SpeedStep = 10;         // 单击步进
		private const double SpeedStepLong = 100;    // 长按步进
		private const double ErrorPercent = 0.05;    // 运行时 ±5% 误差

		private bool _isRunning;                      // 运行状态
		private double _targetSpeed = DefaultSpeed;   // 设定转速
		private DispatcherTimer? _runTimer;           // 运行时误差刷新定时器
		private DispatcherTimer? _holdTimer;          // 长按重复定时器
		private bool _longPressActive;                // 是否进入长按重复
		private int _holdDirection;                   // 长按方向：+1 加 / -1 减

		/// <summary>启动：运行灯亮、插口灯环亮、开始按 ±5% 误差刷新显示</summary>
		private void BtnStart_Click(object sender, RoutedEventArgs e)
		{
			if (_isRunning) return;
			_isRunning = true;
			SetRun(true);
			SetPort(true);
			StartRunError();
		}

		/// <summary>停止：运行灯灭、插口灯环灭、显示回到设定值（无误差）</summary>
		private void BtnStop_Click(object sender, RoutedEventArgs e)
		{
			if (!_isRunning) return;
			_isRunning = false;
			SetRun(false);
			SetPort(false);
			StopRunError();
			SetGaugeValue(_targetSpeed, GaugeEaseMode.Smooth);
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

		// ===== 加/减速：短按 10 步进，长按 100 步进重复 =====
		private void BtnSpeed_Down(object sender, MouseButtonEventArgs e)
		{
			int dir = sender == BtnSpeedUp ? +1 : -1;
			StartHold(dir);
		}

		private void BtnSpeed_Up(object sender, MouseButtonEventArgs e)
			=> EndHold();

		private void StartHold(int dir)
		{
			_holdDirection = dir;
			_longPressActive = false;
			_holdTimer?.Stop();
			_holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
			_holdTimer.Tick += (s, e) =>
			{
				if (!_longPressActive)
				{
					_longPressActive = true;
					_holdTimer.Interval = TimeSpan.FromMilliseconds(80); // 进入长按后加速重复
				}
				AdjustSpeed(_holdDirection * SpeedStepLong);
			};
			_holdTimer.Start();
		}

		private void EndHold()
		{
			_holdTimer?.Stop();
			_holdTimer = null;
			if (!_longPressActive)
				AdjustSpeed(_holdDirection * SpeedStep); // 短按：单步 10
			_longPressActive = false;
		}

		/// <summary>调整设定转速并刷新显示（停止时立即显示，运行时由误差定时器刷新）</summary>
		private void AdjustSpeed(double delta)
		{
			_targetSpeed = Math.Max(0, Math.Min(GaugeMaxValue, _targetSpeed + delta));
			if (!_isRunning)
				SetGaugeValue(_targetSpeed, GaugeEaseMode.Smooth);
		}









		/// <summary>
		/// 构建表盘：浅蓝色 270° 圆环 + 内侧刻度 + 起始刻度圆球
		/// </summary>
		private void BuildGauge()
		{
			double cx = GaugeSize / 2;
			double cy = GaugeSize / 2;

			// 外圈 270° 圆环弧（底部留 90° 缺口）
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
				Stroke = new SolidColorBrush(RingColor),
				StrokeThickness = 2,
			};
			GaugeCanvas.Children.Add(ring);

			// 内侧刻度线：每 5° 一根，主刻度每 30°，指向圆心方向
			for (double theta = StartDeg; theta <= EndDeg + 0.0001; theta += 5)
			{
				double k = (theta - StartDeg) / 30.0;
				bool isMajor = Math.Abs(k - Math.Round(k)) < 0.001;
				double len = isMajor ? MajorLen : MinorLen;
				double rInner = TickOuter - len;
				double a = theta * Math.PI / 180;
				// 外端（靠圆环）
				double x1 = cx + TickOuter * Math.Sin(a);
				double y1 = cy - TickOuter * Math.Cos(a);
				// 内端（指向圆心）
				double x2 = cx + rInner * Math.Sin(a);
				double y2 = cy - rInner * Math.Cos(a);
				var tick = new Line
				{
					X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
					Stroke = new SolidColorBrush(isMajor ? MajorColor : MinorColor),
					StrokeThickness = isMajor ? 1.5 : 1,
				};
				GaugeCanvas.Children.Add(tick);
			}

			// 圆球指示器（实体磨砂球，位置由 SetGaugeValue 控制）
			// 实体磨砂球：径向渐变（高光 → 实色浅蓝 → 深蓝边缘）
			var ballBrush = new RadialGradientBrush
			{
				GradientOrigin = new Point(0.32, 0.30),
			};
			ballBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.0));
			ballBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD6, 0xE6, 0xF7), 0.35));
			ballBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x8C, 0xB8, 0xDE), 0.75));
			ballBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x5A, 0x8E, 0xC8), 1.0));

			_ball = new Ellipse
			{
				Width = BallSize * 2,
				Height = BallSize * 2,
				Fill = ballBrush,
				Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
				StrokeThickness = 1,
			};
			GaugeCanvas.Children.Add(_ball);
		}
	}
}
