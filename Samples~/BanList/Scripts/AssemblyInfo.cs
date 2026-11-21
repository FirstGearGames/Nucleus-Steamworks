using Nucleus.Serializers;

// The Ban List sample declares its ban notice, so it sits in a bundle of its own, below the shared sample's, so a game's own types never
// meet it.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 12))]
