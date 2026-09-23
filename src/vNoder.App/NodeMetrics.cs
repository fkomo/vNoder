namespace vNoder.App;

/// <summary>
/// Node geometry shared between the XAML that draws a node and the view-model that computes port
/// anchor points. Wire endpoints are derived from these constants rather than measured from the
/// visual tree, so an anchor is exact the moment a node moves — before layout has run.
/// Change a value here and the matching value in the node template in MainWindow.xaml together.
/// </summary>
public static class NodeMetrics
{
    public const double Width = 190;
    public const double BorderThickness = 1;
    public const double HeaderHeight = 26;
    public const double PortsTopPadding = 6;
    public const double PortRowHeight = 22;
    public const double PortDotSize = 11;

    /// <summary>Smallest width a node can be dragged to, keeping inline fields usable.</summary>
    public const double MinWidth = 160;

    /// <summary>
    /// Body height a resizable node starts at. Fixed rather than Auto on purpose: a display node must
    /// not change size when a large value arrives.
    /// </summary>
    public const double DefaultContentHeight = 76;

    /// <summary>
    /// Floor for a resizable node's body. A node whose editor template declares a larger
    /// <c>MinHeight</c> uses that instead — see <see cref="ViewModels.NodeViewModel.MinContentHeight"/>.
    /// </summary>
    public const double MinContentHeight = 24;

    // Group box metrics.
    public const double GroupHeaderHeight = 26;
    public const double GroupPadding = 18;
    public const double GroupCollapsedWidth = 220;

    /// <summary>Vertical centre of the port row at <paramref name="index"/>, in node-local space.</summary>
    public static double PortCentreY(int index) =>
        BorderThickness + HeaderHeight + PortsTopPadding + (index * PortRowHeight) + (PortRowHeight / 2);
}
