using System;
using System.Text;
using System.IO;
using System.Diagnostics;

namespace HDs2_SDK.Control
{
	/// <summary>
	/// 简易运行日志：写入日志文件 + Debug 输出，用于联调时确认
	/// Control 是否真的收到下位机数据、解码出什么值。
	/// 日志文件：sdk_run.log（位于程序运行目录），每次以追加方式写入。
	/// </summary>
	public static class SdkLog
	{
		private static readonly object _lock = new();

		/// <summary>日志文件完整路径</summary>
		public static string LogPath =>
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sdk_run.log");

		/// <summary>写入一条运行日志（文件 + Debug 输出）</summary>
		public static void Info(string message)
		{
			string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
			Debug.WriteLine(line);
			try
			{
				lock (_lock)
					File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
			}
			catch
			{
				// 日志写失败（无权限等）不影响业务
			}
		}

		/// <summary>把一帧字节流格式化为 HEX 字符串，便于查看原始数据</summary>
		public static string Hex(byte[] data)
		{
			var sb = new StringBuilder(data.Length * 3);
			for (int i = 0; i < data.Length; i++)
			{
				if (i > 0) sb.Append(' ');
				sb.Append(data[i].ToString("X2"));
			}
			return sb.ToString();
		}
	}
}
