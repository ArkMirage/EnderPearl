namespace EnderPearl.Command;

/// <summary>
/// What an argument accepts, which is what decides how the client completes it.
///
/// <para>The kinds exist so that a command declares its arguments in command terms — "the first one is
/// a backend name" — while the wire form stays protocol work. The in-game command tree builder is the
/// only place that knows an enum or a soft enum is what makes a backend name completable, and a plugin
/// registering a command never has to.</para>
/// </summary>
public enum CommandParameterKind
{
	/// <summary>A backend name, completed from the servers the player may reach.</summary>
	BackendName,

	/// <summary>A player on the network, completed from the live roster.</summary>
	Player,

	/// <summary>A permission verb: <c>set</c>, <c>unset</c>, <c>info</c>, <c>list</c> or <c>permission</c>.</summary>
	PermissionAction,

	/// <summary>A permission node, completed from the nodes that exist.</summary>
	PermissionNode,

	/// <summary>Free text, accepted as typed.</summary>
	FreeText
}