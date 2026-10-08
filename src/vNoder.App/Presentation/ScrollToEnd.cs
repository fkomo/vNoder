using System.Windows;
using System.Windows.Controls;

namespace vNoder.App.Presentation;

/// <summary>
/// Keeps a text box scrolled to its last line whenever its text changes, as a console window does, so
/// the Standard Output and Standard Error nodes show the newest output during a continuous run. Also on
/// load: a node realized again after being panned out of view would otherwise open at the top.
/// </summary>
public static class ScrollToEnd
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollToEnd), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBox box)
            return;

        box.TextChanged -= Scroll;
        box.Loaded -= Scroll;
        if (e.NewValue is true)
        {
            box.TextChanged += Scroll;
            box.Loaded += Scroll;
        }
    }

    private static void Scroll(object sender, RoutedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
