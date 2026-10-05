namespace EnderPearl.Command;

/// <summary>
/// Where a command can be run from.
///
/// <para>One command can serve both places — <c>/glist</c> is the same listing whether it was typed at
/// the terminal or in chat — while others only make sense in one: <c>/stop</c> needs no player, and
/// <c>/server</c> needs a session to move. A scope is also what keeps a name out of the other place
/// entirely, so a console-only name is never mistaken for a command the proxy owns in chat.</para>
/// </summary>
[Flags]
public enum CommandScope
{
	/// <summary>The proxy's own terminal.</summary>
	Console = 1,

	/// <summary>A player's chat, relayed through a backend.</summary>
	Game = 2,

	Both = Console | Game
}