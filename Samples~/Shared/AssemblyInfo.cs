using Nucleus.Serializers;

// The shared sample assembly declares the name tag's component, so it sits in a bundle of its own, below the Steam transport's tests', so a
// game's own types never meet it.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 11))]
