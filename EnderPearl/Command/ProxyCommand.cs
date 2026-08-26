namespace EnderPearl.Command
{
	/// <summary>
	/// A command the proxy itself owns: its trigger word and its description.
	/// </summary>
	public sealed record ProxyCommand
	{
		public string Name { get; }

		public string Description { get; }

		public ProxyCommand(string name, string description)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new ArgumentException("name cannot be blank");
			}
			if (name.StartsWith("/"))
			{
				throw new ArgumentException("name must not include a leading slash");
			}
			if (string.IsNullOrWhiteSpace(description))
			{
				throw new ArgumentException("description cannot be blank");
			}
			Name = name;
			Description = description;
		}
	}
}
