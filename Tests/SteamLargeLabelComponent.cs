#if UNITY_INCLUDE_TESTS
using Nucleus.Components;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// A component holding one wide string, so a world of these is far wider than a single tick of a slow link.
    /// </summary>
    public partial class SteamLargeLabelComponent : NetworkComponent
    {
        /// <summary>
        /// The string, many kilobytes wide in the cases that use it.
        /// </summary>
        public readonly NetworkMember<string> Label = new();
    }
}
#endif
