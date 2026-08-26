using System;
using System.Globalization;
using System.IO;
using System.Text;
using EnderPearl.Logging;

namespace EnderPearl.Diagnostics
{
	/// <summary>
	/// An append-only file holding only protocol faults, so they can be found without reading the relay.
	///
	/// <p>The ordinary log is a running commentary on a working proxy; a fault that drops a player is a
	/// handful of lines somewhere inside it, usually noticed only because someone complained. This file
	/// has nothing else in it, so "has anything gone wrong today" is answered by its size.</p>
	///
	/// <p>Every entry is one line and self-contained - timestamp, player, backend, and the decoded
	/// violation including the packet id and the schema member BDS named.</p>
	/// </summary>
	public sealed class ProtocolFaultLog
	{
		private readonly object recordLock = new();
		private readonly string? file;
		private bool unwritable;

		public ProtocolFaultLog(string? file)
		{
			this.file = file;
		}

		/// <summary>A log that discards everything, for a proxy that has the file configured empty.</summary>
		public static ProtocolFaultLog Disabled() => new(null);

		public bool Enabled() => file != null;

		public string? File => file;

		/// <summary>
		/// Appends one fault. Failing to write must never take the connection down with it, so an I/O
		/// problem is reported once and then the log goes quiet rather than reporting it per fault.
		/// </summary>
		public void Record(ProtocolFault fault)
		{
			if (file == null || unwritable)
			{
				return;
			}
			string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
				+ " " + fault.Describe() + Environment.NewLine;
			// Java made record() synchronized: two players faulting at once must append serially, and
			// a transient sharing conflict must not flip the permanent unwritable flag.
			lock (recordLock)
			{
				try
				{
					string? parent = Path.GetDirectoryName(Path.GetFullPath(file));
					if (!string.IsNullOrEmpty(parent))
					{
						Directory.CreateDirectory(parent);
					}
					System.IO.File.AppendAllText(file, line, Encoding.UTF8);
				}
				catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
				{
					unwritable = true;
					Logger.Error($"Cannot write the protocol fault log at {file}, disabling it: {failure.Message}.");
				}
			}
		}
	}
}
