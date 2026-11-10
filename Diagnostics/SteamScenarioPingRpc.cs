using Nucleus.Managers.Rpc;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The reliable remote call a <see cref="SteamScenarioProbe"/> client makes to the server on the player object it controls.
    /// </summary>
    [NetworkType]
    public struct SteamScenarioPingRpc : IRpc
    {
        /// <summary>
        /// The call's number, counting every call the client has made.
        /// </summary>
        public uint Sequence;
    }
}
