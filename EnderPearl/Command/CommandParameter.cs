namespace EnderPearl.Command;

/// <summary>
/// One argument a command takes: what it accepts, what it is called, and whether it may be left out.
///
/// <para>Declared by the command rather than by the packet builder, so a command states its own shape
/// in one place and both the in-game command tree and the completion the client offers follow from it
/// instead of from a switch on the command's name.</para>
/// </summary>
public sealed class CommandParameter
{
	public required CommandParameterKind Kind { get; init; }

	public required string Name { get; init; }

	/// <summary>
	/// Whether the client accepts the command without it.
	///
	/// <para>A trailing optional argument is what lets one declaration cover several forms —
	/// <c>/perm list</c> as well as <c>/perm info &lt;player&gt;</c> — so the client does not have to
	/// pick between overloads while the line is being typed.</para>
	/// </summary>
	public bool IsOptional { get; init; }
}