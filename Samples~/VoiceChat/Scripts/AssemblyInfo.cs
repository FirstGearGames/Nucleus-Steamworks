using Nucleus.Serializers;

// The Voice Chat sample declares its voice call, so it sits in a bundle of its own, below the Ban List sample's, so a game's own types
// never meet it.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 13))]
