namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Which side of a Steam link a <see cref="SteamLinkProbe"/> plays.
    /// </summary>
    public enum SteamLinkProbeRole : byte
    {
        /// <summary>
        /// Starts a server with its own client, counts the probes that arrive and echoes each back.
        /// </summary>
        Host = 0,
        /// <summary>
        /// Connects to the host's Steam ID and sends the probes.
        /// </summary>
        Client = 1,
    }
}
