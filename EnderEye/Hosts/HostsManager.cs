using System;
using System.Collections.Generic;
using System.IO;

namespace EnderEye.Hosts
{
	/// <summary>
	/// 维护 hosts 中指向本机的域名映射。
	/// </summary>
	public class HostsManager
	{
		private readonly string _hostsPath;
		private bool _cleanupDone;

		public HostsManager()
		{
			_hostsPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.System),
				"drivers", "etc", "hosts");

			Console.WriteLine($"[hosts] File: {_hostsPath}");
		}

		/// <summary>
		/// 确保 hosts 中存在 domain -> 127.0.0.1 的映射，已存在时不改动文件。
		/// </summary>
		public void EnsureLoopbackEntry(string domain)
		{
			Console.WriteLine($"[hosts] Checking entry: 127.0.0.1 {domain}");

			var lines = File.ReadAllLines(_hostsPath);

			foreach (var line in lines)
			{
				if (IsLoopbackEntry(line, domain))
				{
					Console.WriteLine($"[hosts] Entry already exists, file unchanged: 127.0.0.1 {domain}");
					return;
				}
			}

			var updated = new List<string>(lines) { $"127.0.0.1 {domain}" };
			File.WriteAllLines(_hostsPath, updated);
			Console.WriteLine($"[hosts] Entry written: 127.0.0.1 {domain}");
		}

		/// <summary>
		/// 删除 hosts 中 domain -> 127.0.0.1 的映射，程序退出时调用。
		/// </summary>
		public void RemoveLoopbackEntry(string domain)
		{
			if (_cleanupDone)
			{
				return;
			}

			_cleanupDone = true;
			Console.WriteLine($"[hosts] Removing entry: 127.0.0.1 {domain}");

			try
			{
				var lines = File.ReadAllLines(_hostsPath);
				var updated = new List<string>(lines.Length);
				var removed = 0;

				foreach (var line in lines)
				{
					if (IsLoopbackEntry(line, domain))
					{
						removed++;
						continue;
					}

					updated.Add(line);
				}

				if (removed == 0)
				{
					Console.WriteLine($"[hosts] Entry not found, file unchanged: 127.0.0.1 {domain}");
					return;
				}

				File.WriteAllLines(_hostsPath, updated);
				Console.WriteLine($"[hosts] Removed {removed} entry(ies): 127.0.0.1 {domain}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[hosts] Remove failed: {ex.Message}");
			}
		}

		private static bool IsLoopbackEntry(string line, string domain)
		{
			var content = line;
			var commentIndex = content.IndexOf('#');
			if (commentIndex >= 0)
			{
				content = content.Substring(0, commentIndex);
			}

			var parts = content.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			return parts.Length >= 2
				&& parts[0] == "127.0.0.1"
				&& string.Equals(parts[1], domain, StringComparison.OrdinalIgnoreCase);
		}
	}
}