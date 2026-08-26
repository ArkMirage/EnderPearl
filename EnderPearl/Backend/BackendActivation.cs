namespace EnderPearl.Backend
{
	/// <summary>
	/// What the caller of a backend connect wants to hear about as the handshake progresses.
	/// </summary>
	public interface BackendActivation
	{
		/// <summary>The encryption handshake completed and the relay is installed.</summary>
		void OnReady(BackendSession backend);

		/// <summary>The target's StartGame has arrived.</summary>
		void OnStartGame(BackendSession backend);

		void OnFailure(BackendSession? backend, Exception exception);
	}
}
