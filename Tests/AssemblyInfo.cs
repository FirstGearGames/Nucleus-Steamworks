#if UNITY_INCLUDE_TESTS
using Nucleus.Serializers;

// The Steam transport tests' network types sit in a bundle of their own, below the diagnostics', so a game's own types never meet them.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 10))]
#endif
