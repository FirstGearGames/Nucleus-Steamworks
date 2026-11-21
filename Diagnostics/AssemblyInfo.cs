using Nucleus.Serializers;

// The Steam transport diagnostics' network types sit in a bundle of their own, below the transport's, so a game's own types never meet them.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 9))]
