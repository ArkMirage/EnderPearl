using System;
using System.IO;

namespace EnderPearl.Logging
{
	/// <summary>
	/// The proxy's only logging mechanism. Every line lands on the terminal with a coloured level
	/// tag and, once <see cref="Install"/> has run, as a plain-text copy in <c>logs/latest.log</c>
	/// beside the config - colours are stripped there so editors and grep see clean text.
	///
	/// <p>Colours are on by default and can be forced off with <c>ENDERPEARL_LOG_COLOR=off</c> (or
	/// the common <c>NO_COLOR</c> convention); they are disabled automatically when stdout is
	/// redirected.</p>
	///
	/// <p><c>Debug</c> compiles away in Release builds - anything that must survive into a
	/// production log belongs at Info or above.</p>
	/// </summary>
	public static class Logger
	{
		private static readonly object WriteLock = new();
		private static readonly bool ColorEnabled = ResolveColorEnabled();
		private static StreamWriter? file;

		/// <summary>
		/// Opens <c>logs/latest.log</c> beside the config as the mirror of everything logged.
		/// Call once during startup before any other work; before it runs, lines go to the
		/// terminal only.
		/// </summary>
		public static void Install(string configPath)
		{
			string? parent = Path.GetDirectoryName(Path.GetFullPath(configPath));
			string directory = Path.Combine(string.IsNullOrEmpty(parent) ? "." : parent, "logs");
			Directory.CreateDirectory(directory);
			string logPath = Path.Combine(directory, "latest.log");

			FileStream stream = new(
				logPath,
				FileMode.Create,
				FileAccess.Write,
				FileShare.Read);
			file = new StreamWriter(stream) { AutoFlush = true };

			string full = Path.GetFullPath(logPath);
			Info($"Writing proxy log to {full}.");
			Info($"Log started at {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}.");
		}

		public static void Info(string message)
		{
			Write("INFO", "36", message);
		}

		public static void Warn(string message)
		{
			Write("WARN", "33", message);
		}

		public static void Error(string message)
		{
			Write("ERROR", "31", message);
		}

		// Call sites are stripped by the compiler in Release builds; keeping the attribute on the
		// method itself (rather than #if around it) keeps Debug call sites type-checked either way.
		[System.Diagnostics.Conditional("DEBUG")]
		public static void Debug(string message)
		{
			Write("DEBUG", "35", message);
		}

		private static void Write(string tag, string colorCode, string message)
		{
			string stamp = DateTime.Now.ToString("HH:mm:ss");
			string plain = "[" + tag + "] " + message;
			string colored = ColorEnabled
				? "\x1b[1m[\x1b[" + colorCode + "m" + tag + "\x1b[0m\x1b[1m]\x1b[0m " + message
				: plain;
			lock (WriteLock)
			{
				try
				{
					Console.Out.WriteLine(stamp + " " + colored);
				}
				catch (Exception)
				{
					// A closed terminal must never turn a log line into a crash.
				}
				if (file == null)
				{
					return;
				}
				try
				{
					file.WriteLine(stamp + " " + plain);
				}
				catch (IOException)
				{
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}

		private static bool ResolveColorEnabled()
		{
			string? setting = Environment.GetEnvironmentVariable("ENDERPEARL_LOG_COLOR")
				?? Environment.GetEnvironmentVariable("NO_COLOR");
			if (!string.IsNullOrEmpty(setting)
				&& setting.Equals("off", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return !Console.IsOutputRedirected;
		}
	}
}
