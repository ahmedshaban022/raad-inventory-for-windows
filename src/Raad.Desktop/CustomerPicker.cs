using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Raad.Engine;

namespace Raad.Desktop;

/// <summary>
/// Outcome of a customer pick. A null customer means a walk-in (cash) sale without a customer.
/// </summary>
public sealed record CustomerPick(Customer? Customer)
{
    public const string WalkInName = "عميل نقدي (بدون عميل)";
    public string? CustomerId => Customer?.Id;
    public string CustomerName => Customer?.Name ?? WalkInName;
    public bool IsWalkIn => Customer is null;
    public static CustomerPick WalkIn { get; } = new((Customer?)null);
}

/// <summary>
/// Modal customer search built in code (no XAML part). The result pages are read straight from the
/// store, so the picker never depends on the customers already loaded by the customers screen.
/// </summary>
public sealed class CustomerPicker : Window
{
    private const int PageSize = 20;
    private readonly Store store;
    private readonly TextBox searchBox;
    private readonly ListBox results;
    private readonly TextBlock pageLabel;
    private readonly TextBlock statusLabel;
    private readonly Button previousButton;
    private readonly Button nextButton;
    private int offset;
    private int searchRevision;
    private int loadRevision;

    /// <summary>Id of the picked customer, or null for a walk-in sale.</summary>
    public string? SelectedCustomerId { get; private set; }

    /// <summary>Display name of the picked customer, or the walk-in label.</summary>
    public string SelectedCustomerName { get; private set; } = CustomerPick.WalkInName;

    public CustomerPicker(Store store, string title, string? initialQuery = null)
    {
        this.store = store;
        Title = title;
        Width = 520;
        Height = 580;
        MinWidth = 420;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        ShowInTaskbar = false;
        FontFamily = InputDialog.Cairo;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

        searchBox = new TextBox { Text = initialQuery ?? string.Empty, MinHeight = 34, Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(searchBox, "بحث العميل");
        searchBox.TextChanged += SearchBox_TextChanged;
        panel.Children.Add(new TextBlock { Text = "ابحث بالاسم أو الهاتف", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(searchBox);

        results = new ListBox { Height = 330, FontSize = 13, DisplayMemberPath = nameof(CustomerRow.Label) };
        results.MouseDoubleClick += Results_MouseDoubleClick;
        panel.Children.Add(results);

        statusLabel = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        statusLabel.SetResourceReference(TextBlock.ForegroundProperty, "Raad.Text.Muted");
        panel.Children.Add(statusLabel);

        var pager = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        previousButton = NewButton("السابق");
        previousButton.Click += async (_, _) => { offset = Math.Max(0, offset - PageSize); await LoadAsync(); };
        nextButton = NewButton("التالي");
        nextButton.Click += async (_, _) => { offset += PageSize; await LoadAsync(); };
        pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        pager.Children.Add(previousButton);
        pager.Children.Add(pageLabel);
        pager.Children.Add(nextButton);
        panel.Children.Add(pager);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var choose = NewButton("اختيار العميل");
        choose.Click += (_, _) => ChooseSelected();
        choose.IsDefault = true;
        var walkIn = NewButton(CustomerPick.WalkInName);
        walkIn.Click += (_, _) =>
        {
            SelectedCustomerId = null;
            SelectedCustomerName = CustomerPick.WalkInName;
            DialogResult = true;
        };
        var cancel = NewButton("إلغاء");
        cancel.Click += (_, _) => { DialogResult = false; };
        cancel.IsCancel = true;
        actions.Children.Add(choose);
        actions.Children.Add(walkIn);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);

        Content = panel;
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Shows the picker and returns the choice, or null when the operator cancelled.</summary>
    public static CustomerPick? Show(Window owner, Store store, string title, string? initialQuery = null)
    {
        var picker = new CustomerPicker(store, title, initialQuery);
        picker.Owner = owner;
        return picker.ShowDialog() == true ? new CustomerPick(picker.PickedCustomer) : null;
    }

    private Customer? PickedCustomer { get; set; }

    private static Button NewButton(string content)
    {
        var button = new Button { Content = content, MinHeight = 32, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current?.TryFindResource("Raad.Button.Secondary") is Style style) button.Style = style;
        return button;
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var current = ++searchRevision;
        await Task.Delay(200);
        if (current != searchRevision) return;
        offset = 0;
        await LoadAsync();
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ChooseSelected();

    private async Task LoadAsync()
    {
        var query = searchBox.Text;
        var current = ++loadRevision;
        IReadOnlyList<Customer> rows;
        try { rows = await Task.Run(() => store.Customers(query, false, offset)); }
        catch (Exception error)
        {
            statusLabel.Text = "تعذر تحميل العملاء: " + error.Message;
            return;
        }
        if (current != loadRevision) return;
        var page = rows.Take(PageSize).ToList();
        results.ItemsSource = page.Select(customer => new CustomerRow(customer)).ToList();
        previousButton.IsEnabled = offset > 0;
        nextButton.IsEnabled = rows.Count > PageSize;
        pageLabel.Text = $"الصفحة {offset / PageSize + 1} — {page.Count} عميل";
        statusLabel.Text = page.Count == 0 ? "لا يوجد عملاء مطابقون للبحث." : string.Empty;
    }

    private void ChooseSelected()
    {
        if (results.SelectedItem is not CustomerRow row) { statusLabel.Text = "اختر عميلًا أولًا."; return; }
        SelectedCustomerId = row.Id;
        SelectedCustomerName = row.Name;
        PickedCustomer = row.Customer;
        DialogResult = true;
    }

    private sealed record CustomerRow(Customer Customer)
    {
        public string Id => Customer.Id;
        public string Name => Customer.Name;
        public string Label => Customer.Phone.Length == 0 ? Customer.Name : $"{Customer.Name} — {Customer.Phone}";
    }
}
