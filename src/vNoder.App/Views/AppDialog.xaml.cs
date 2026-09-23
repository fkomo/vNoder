using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace vNoder.App.Views
{
    /// <summary>What the user chose in an <see cref="AppDialog"/>.</summary>
    public enum DialogChoice
    {
        Cancel,
        Yes,
        No,
        Ok,
    }

    /// <summary>
    /// The app's only dialog. WPF's <c>MessageBox</c> is the system one and always renders in light
    /// chrome, which looks like a different program next to this window; the group name prompt has no
    /// built-in equivalent at all. Both go through here so every dialog matches the app.
    /// </summary>
    public partial class AppDialog : Window
    {
        private DialogChoice _choice = DialogChoice.Cancel;

        private AppDialog()
        {
            InitializeComponent();
        }

        /// <summary>Tells the user something. One button, nothing to decide.</summary>
        public static void Message(Window owner, string title, string message)
        {
            var dialog = Create(owner, title, message);
            dialog.AddButton("OK", DialogChoice.Ok, isDefault: true, isCancel: true);
            dialog.ShowDialog();
        }

        /// <summary>Asks a three-way question, such as whether to save before discarding.</summary>
        public static DialogChoice Confirm(Window owner, string title, string message, string yes, string no)
        {
            var dialog = Create(owner, title, message);
            dialog.AddButton(yes, DialogChoice.Yes, isDefault: true, isCancel: false);
            dialog.AddButton(no, DialogChoice.No, isDefault: false, isCancel: false);
            dialog.AddButton("Cancel", DialogChoice.Cancel, isDefault: false, isCancel: true);
            dialog.ShowDialog();
            return dialog._choice;
        }

        /// <summary>Asks for a single line of text. Returns null when cancelled or left blank.</summary>
        public static string? Prompt(Window owner, string title, string message, string initial)
        {
            var dialog = Create(owner, title, message);
            dialog.ValueBox.Visibility = Visibility.Visible;
            dialog.ValueBox.Text = initial;
            dialog.AddButton("OK", DialogChoice.Ok, isDefault: true, isCancel: false);
            dialog.AddButton("Cancel", DialogChoice.Cancel, isDefault: false, isCancel: true);

            dialog.Loaded += (_, _) =>
            {
                dialog.ValueBox.Focus();
                dialog.ValueBox.SelectAll();
            };

            dialog.ShowDialog();

            if (dialog._choice != DialogChoice.Ok)
                return null;

            var value = dialog.ValueBox.Text.Trim();
            return value.Length == 0 ? null : value;
        }

        private static AppDialog Create(Window owner, string title, string message)
        {
            var dialog = new AppDialog { Owner = owner };
            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;
            return dialog;
        }

        private void AddButton(string caption, DialogChoice choice, bool isDefault, bool isCancel)
        {
            var button = new Button
            {
                Content = caption,
                MinWidth = 82,
                IsDefault = isDefault,
                IsCancel = isCancel,
                Style = (Style)FindResource(isDefault ? "PrimaryButton" : "ToolbarButton"),
                Margin = new Thickness(6, 0, 0, 0),
            };

            button.Click += (_, _) =>
            {
                _choice = choice;
                DialogResult = choice != DialogChoice.Cancel;
            };

            Buttons.Children.Add(button);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
