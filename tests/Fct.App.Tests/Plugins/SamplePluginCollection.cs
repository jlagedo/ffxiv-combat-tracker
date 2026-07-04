using Xunit;

namespace Fct.App.Tests.Plugins;

// Tests that load the staged Fct.SamplePlugin share its private settings directory
// (<AppData.Root>\plugins\com.fct.sample). Run them serially so concurrent
// InitializeAsync settings writes don't collide.
[CollectionDefinition("Sample plugin", DisableParallelization = true)]
public sealed class SamplePluginCollection { }
