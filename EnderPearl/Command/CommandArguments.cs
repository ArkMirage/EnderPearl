namespace EnderPearl.Command
{
	/// <summary>
	/// Splits the arguments off a command line the client sent.
	///
	/// <para>Quote-aware because Xbox gamertags may contain spaces, and the client quotes a
	/// <c>CommandParam.STRING</c> argument that does: <c>/send "Some Player" lobby</c> has to arrive as
	/// two arguments, not three.</para>
	/// </summary>
	public static class CommandArguments
	{
		/// <summary>The arguments after the command name, with surrounding quotes removed.</summary>
		public static List<string> Split(string commandLine)
		{
			string arguments = Remainder(commandLine);
			List<string> tokens = new();
			System.Text.StringBuilder current = new();
			bool quoted = false;
			bool started = false;
			for (int index = 0; index < arguments.Length; index++)
			{
				char character = arguments[index];
				if (character == '"')
				{
					quoted = !quoted;
					// A quoted empty string is still an argument, so remember that one began.
					started = true;
				}
				else if (!quoted && char.IsWhiteSpace(character))
				{
					if (started)
					{
						tokens.Add(current.ToString());
						current.Clear();
						started = false;
					}
				}
				else
				{
					current.Append(character);
					started = true;
				}
			}
			if (started)
			{
				tokens.Add(current.ToString());
			}
			return tokens;
		}

		/// <summary>Everything after the command name, verbatim — for free-text arguments such as an alert.</summary>
		public static string Remainder(string? commandLine)
		{
			if (commandLine == null)
			{
				return "";
			}
			string trimmed = commandLine.Trim();
			if (trimmed.StartsWith("/"))
			{
				trimmed = trimmed.Substring(1);
			}
			int space = IndexOfWhitespace(trimmed);
			if (space < 0)
			{
				return "";
			}
			return trimmed.Substring(space + 1).Trim();
		}

		private static int IndexOfWhitespace(string value)
		{
			for (int index = 0; index < value.Length; index++)
			{
				if (char.IsWhiteSpace(value[index]))
				{
					return index;
				}
			}
			return -1;
		}
	}
}
