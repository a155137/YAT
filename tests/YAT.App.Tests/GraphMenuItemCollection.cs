namespace YAT.App.Tests;

// The test classes that build Avalonia menu items with live commands (Copy Image, Reset View, the right-click menu). A
// menu item registers its type's properties the first time one is made, and Avalonia's registry is not safe to fill
// from two threads at once, so these classes run one after another rather than side by side. Every other class still
// runs in parallel.
[CollectionDefinition(Name)]
public sealed class GraphMenuItemCollection
{
    public const string Name = "Graph menu items";
}
