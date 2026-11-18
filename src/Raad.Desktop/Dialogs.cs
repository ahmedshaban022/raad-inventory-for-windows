using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Raad.Desktop;

/// <summary>
/// Plain, immutable snapshot of the values a dialog collected. It is taken on the UI thread when
/// the dialog closes, so it can be read safely from a worker thread afterwards.
/// </summary>
public sealed class DialogValues
{
    private readonly IReadOnlyDictionary<string, string> values;

    internal DialogValues(IReadOnlyDictionary<string, string> values) => this.values = values;

    public string Value(string label) => values.TryGetValue(label, out var value) ? value : string.Empty;
}

/// <summary>
/// Small modal prompt used by the desktop shell for the fields that are not part of a screen.
/// The dialog owns its owner, so it can be opened from a visible main window, and it supports
/// masked password entry.
/// </summary>
public sealed class InputDialog : Window
{
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly Dictionary<string, PasswordBox> passwords = new();

    /// <summary>Creates a dialog with labelled fields. A field whose name starts with "!" is masked.</summary>
    public InputDialog(string title, params (string Label, string? Value)[] inputs)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = Cairo;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var (label, value) in inputs)
        {
            var text = new TextBlock { Text = label.TrimStart('!'), Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(text);
            if (label.StartsWith('!'))
            {
                var box = new PasswordBox { MinHeight = 34, Padding = new Thickness(8), FontFamily = Cairo };
                box.Password = value ?? string.Empty;
                AutomationProperties.SetName(box, label.TrimStart('!'));
                panel.Children.Add(box);
                passwords[label] = box;
            }
            else
            {
                var box = new TextBox { Text = value ?? string.Empty, MinHeight = 34, FontFamily = Cairo };
                AutomationProperties.SetName(box, label);
                panel.Children.Add(box);
                fields[label] = box;
            }
            panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var ok = new Button { Content = "حفظ", Style = (Style)Application.Current.FindResource("Raad.Button.Primary"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "إلغاء", Style = (Style)Application.Current.FindResource("Raad.Button.Secondary") };
        ok.Click += (_, _) => { DialogResult = true; };
        cancel.Click += (_, _) => { DialogResult = false; };
        ok.IsDefault = true;
        cancel.IsCancel = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { if (fields.Count > 0) fields.Values.First().Focus(); else if (passwords.Count > 0) passwords.Values.First().Focus(); };
    }

    /// <summary>Font used by the Raad dialogs and the customer picker.</summary>
    public static FontFamily Cairo { get; } = new("/Raad.Inventory.Feasibility;component/Assets/#Cairo");

    public string Value(string label) =>
        fields.TryGetValue(label, out var box) ? box.Text :
        passwords.TryGetValue(label, out var secret) ? secret.Password : string.Empty;

    /// <summary>True when the field is rendered as a masked password box.</summary>
    public bool IsPasswordField(string label) => passwords.ContainsKey(label);

    /// <summary>True when a label denotes a masked password field.</summary>
    public static bool IsPasswordLabel(string label) => label.StartsWith('!');

    /// <summary>
    /// Opens the dialog on top of the given owner and returns a plain snapshot of the collected
    /// values, or null when the operator cancels. The snapshot is taken on the UI thread.
    /// </summary>
    public static DialogValues? Show(Window owner, string title, params (string Label, string? Value)[] inputs)
    {
        var dialog = new InputDialog(title, inputs);
        dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return null;
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, _) in inputs)
            snapshot[label] = dialog.Value(label);
        return new DialogValues(snapshot);
    }
}

public static class ConfirmDialog
{
    /// <summary>Asks for a confirmation without hiding the owner window.</summary>
    public static bool Show(Window owner, string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = InputDialog.Cairo
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var ok = new Button { Content = "تأكيد", Style = (Style)Application.Current.FindResource("Raad.Button.Primary"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "إلغاء", Style = (Style)Application.Current.FindResource("Raad.Button.Secondary") };
        ok.Click += (_, _) => { window.DialogResult = true; };
        cancel.Click += (_, _) => { window.DialogResult = false; };
        ok.IsDefault = true;
        cancel.IsCancel = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Owner = owner;
        return window.ShowDialog() == true;
    }
}
