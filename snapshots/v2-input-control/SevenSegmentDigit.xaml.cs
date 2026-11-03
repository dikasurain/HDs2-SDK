using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace HDs2_SDK
{
	/// <summary>
	/// 七段数码管显示控件，Digit 范围 0-9
	/// </summary>
	public partial class SevenSegmentDigit : UserControl
	{
		public static readonly DependencyProperty DigitProperty = DependencyProperty.Register(
			"Digit", typeof(int), typeof(SevenSegmentDigit),
			new PropertyMetadata(0, OnDigitChanged));

		public int Digit
		{
			get => (int)GetValue(DigitProperty);
			set => SetValue(DigitProperty, value);
		}

		// 0-9 对应段 a,b,c,d,e,f,g 的开关
		private static readonly bool[][] Segments =
		{
			new[] { true,  true,  true,  true,  true,  true,  false }, // 0
			new[] { false, true,  true,  false, false, false, false }, // 1
			new[] { true,  true,  false, true,  true,  false, true  }, // 2
			new[] { true,  true,  true,  true,  false, false, true  }, // 3
			new[] { false, true,  true,  false, false, true,  true  }, // 4
			new[] { true,  false, true,  true,  false, true,  true  }, // 5
			new[] { true,  false, true,  true,  true,  true,  true  }, // 6
			new[] { true,  true,  true,  false, false, false, false }, // 7
			new[] { true,  true,  true,  true,  true,  true,  true  }, // 8
			new[] { true,  true,  true,  true,  false, true,  true  }, // 9
		};

		private Polygon[] _polygons = null!;

		public SevenSegmentDigit()
		{
			InitializeComponent();
			_polygons = new[] { SegA, SegB, SegC, SegD, SegE, SegF, SegG };
			UpdateSegments(0);
		}

		private static void OnDigitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			((SevenSegmentDigit)d).UpdateSegments((int)e.NewValue);
		}

		private void UpdateSegments(int digit)
		{
			if (digit < 0 || digit > 9) digit = 0;
			var segs = Segments[digit];
			for (int i = 0; i < 7; i++)
				_polygons[i].Opacity = segs[i] ? 1 : 0.04;
		}
	}
}
