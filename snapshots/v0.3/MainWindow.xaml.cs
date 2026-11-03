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

		// 浅蓝色系（正转）
		private static readonly Color RingColor = Color.FromRgb(0x4A, 0x90, 0xE2);
		private static readonly Color MajorColor = Color.FromRgb(0x3A, 0x7B, 0xC8);
		private static readonly Color MinorColor = Color.FromRgb(0x8A, 0xB4, 0xE8);
		// 琥珀色系（反转）
		private static readonly Color MajorColorRev = Color.FromRgb(0xC8, 0x84, 0x3A);
		private static readonly Color MinorColorRev = Color.FromRgb(0xE8, 0xC8, 0x8A);

		public MainWindow()
		{
			InitializeComponent();
			BuildGauge();
			// 按键事件绑定
			BtnPower.Click += BtnPower_Click;
			BtnDir.Click += BtnDir_Click;
			BtnSetSpeed.Click += BtnSetSpeed_Click;
			// 初始：停止状态，显示默认转速 60000（无误差），灯灭
			SetStatus(MotorStatus.Stop);
			UpdateDirection();
			UpdateSpeedLevel();
			TxtSpeed.Text = ((int)_targetSpeed).ToString();
			SetGaugeValue(_targetSpeed, GaugeEaseMode.Smooth);
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

		//控制表盘以及刻度圆球
		private const double BallRadius = TickOuter - MajorLen; // 圆球滑动半径（刻度内端）
		private const double BallSize = 4;                      // 圆球半径
		private const double GaugeMaxValue = 99999;            // 满量程（5 位数码管）
		private const int SmoothMs = 450;                      // 平滑缓出时长
		private const int BounceMs = 600;                      // 弹跳回弹时长
		private Ellipse? _ball;                                // 圆球引用（用于动态移动）
		private Path? _ring;                                   // 圆环引用（用于切换方向时改色）
		private static readonly Brush RingReverse = new SolidColorBrush(Color.FromRgb(0xE2, 0xA0, 0x4A)); // 反转=琥珀色
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
			// 正转：起点(-135°)→终点(+135°) 顺时针；反转：终点→起点 逆时针（角度镜像）
			double angle = _isForward
				? StartDeg + p * (EndDeg - StartDeg)
				: EndDeg - p * (EndDeg - StartDeg);
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





		// ===== 电机控制：启停 / 速度输入 / 运行误差 / 正反转 / 高低速 =====
		private static readonly Random _rng = new Random();
		private const double DefaultSpeed = 60000;   // 默认转速
		private const double ErrorPercent = 0.05;    // 运行时 ±5% 误差
		private const double HighSpeedThreshold = 50000; // 高/低速分界

		private bool _isRunning;                      // 运行状态
		private double _targetSpeed = DefaultSpeed;   // 设定转速
		private bool _isForward = true;               // 正反转（true=正）
		private DispatcherTimer? _runTimer;           // 运行时误差刷新定时器

		/// <summary>启停合一：点击切换运行/停止，按钮文字随之变化</summary>
		private void BtnPower_Click(object sender, RoutedEventArgs e)
		{
			if (_isRunning)
			{
				_isRunning = false;
				SetStatus(MotorStatus.Stop);
				SetInputEnabled(true);
				StopRunError();
				SetGaugeValue(_targetSpeed, GaugeEaseMode.Smooth);
				BtnPower.Content = "启";
			}
			else
			{
				_isRunning = true;
				SetStatus(MotorStatus.Run);
				SetInputEnabled(false);
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

		/// <summary>解析输入框数值，更新设定转速、高低速识别与显示（运行中禁止）</summary>
		private void ApplySpeedInput()
		{
			if (_isRunning) return;
			if (int.TryParse(TxtSpeed.Text, out int v))
			{
				_targetSpeed = Math.Max(0, Math.Min(GaugeMaxValue, v));
				TxtSpeed.Text = ((int)_targetSpeed).ToString();
				UpdateSpeedLevel();
				if (!_isRunning)
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
			Color targetRing = _isForward ? RingColor : Color.FromRgb(0xE2, 0xA0, 0x4A);
			Color majorC = _isForward ? MajorColor : MajorColorRev;
			Color minorC = _isForward ? MinorColor : MinorColorRev;
			var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
			var dur = TimeSpan.FromMilliseconds(300);
			// 圆环颜色动画
			if (_ring?.Stroke is SolidColorBrush ringBrush)
			{
				ringBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
				{
					To = targetRing,
					Duration = dur,
					EasingFunction = ease,
				});
			}
			// 刻度颜色动画（主/次分别过渡）
			foreach (var child in GaugeCanvas.Children)
			{
				if (child is Line line && line.Stroke is SolidColorBrush brush)
				{
					brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
					{
						To = line.StrokeThickness > 1.2 ? majorC : minorC,
						Duration = dur,
						EasingFunction = ease,
					});
				}
			}
			UpdateGaugeDisplay(_gaugeValue);
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
			_ring = ring;
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
