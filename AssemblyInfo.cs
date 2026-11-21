using Nucleus.Serializers;

// The Steam transport's network types sit in a bundle of their own, below those the Unity integration, its tests, its demos and the
// Kinematic Character Controller integration reserve, so a game's own types never meet them.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 8))]
