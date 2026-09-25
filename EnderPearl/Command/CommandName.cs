namespace EnderPearl.Command
{
	/// <summary>
	/// One word that runs a command, and where that word is understood.
	///
	/// <para>A command answers to several words, and they need not all be understood in the same place:
	/// <c>/alert</c> is a word for chat and for the terminal, while <c>/say</c> is the terminal's shorthand
	/// for it that chat must not claim — Minecraft has a <c>/say</c> of its own, and answering it here would
	/// take that command away from every backend. Declaring <see cref="Scopes"/> narrows one word without
	/// touching the command, so the ordinary case is still a bare name.</para>
	/// </summary>
	public sealed class CommandName
	{
		private string name = "";

		/// <summary>
		/// The word itself, normalised to lower case so lookup is case-insensitive.
		///
		/// <para>Validated on the way in rather than at lookup time: a blank name would otherwise simply
		/// never match anything, which reads as the command being broken.</para>
		/// </summary>
		public required string Name
		{
			get => name;
			init
			{
				if (string.IsNullOrWhiteSpace(value))
				{
					throw new ArgumentException("a command name cannot be blank");
				}
				name = value.Trim().ToLowerInvariant();
			}
		}

		/// <summary>
		/// Where this word answers, when that differs from the command's own scope; null inherits it. Only
		/// ever narrows — a word cannot be understood in a place its command does not reach.
		/// </summary>
		public CommandScope? Scopes { get; init; }

		/// <summary>Where this word answers, given the scope of the command it belongs to.</summary>
		public CommandScope Effective(CommandScope commandScopes)
		{
			return Scopes ?? commandScopes;
		}
	}
}