using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Raad.Engine;

namespace Raad.Desktop;

/// <summary>Row shown in the products grid, including the low-stock state.</summary>
public sealed record ProductRow(Product Product)
{
    public string Name => Product.Name;
    public long Quantity => Product.Quantity;
    public long Threshold => Product.Threshold;
    public string DisplayPrice => Product.DisplayPrice;
    public string StatusText => Product.Quantity <= Product.Threshold ? "منخفض" : "متاح";
}

/// <summary>Row shown in the draft grid; quantity and price stay raw text until the invoice is issued.</summary>
public sealed class DraftRow
{
    public required string ProductId { get; init; }
    public required string Name { get; init; }
    public string Quantity { get; set; } = "1";
    public string UnitPrice { get; set; } = "0";
}

/// <summary>Row shown in the payment combo box.</summary>
public sealed record PaymentRow(Payment Payment)
{
    public string Label => $"{Payment.DisplayAmount} — {Payment.DisplayDate} — {Payment.DisplayStatus}";
}

public partial class MainWindow : Window
{
    private const int SearchDebounceMilliseconds = 120;
    private const string WalkInCustomerName = CustomerPick.WalkInName;
    private readonly ObservableCollection<ProductRow> productRows = new();
    private readonly ObservableCollection<Customer> customerRows = new();
    private readonly ObservableCollection<InvoiceEntry> invoiceRows = new();
    private readonly ObservableCollection<DraftRow> draftRows = new();
    private readonly ObservableCollection<PaymentRow> paymentRows = new();
    private Store store = null!;
    private PdfPages? pdf;
    private string? pdfPath;
    private int pageIndex;
    private long searchRevision;
    private bool busy;
    private int productOffset;
    private int customerOffset;
    private bool showingArchivedProducts;
    private bool showingArchivedCustomers;
    private string? selectedInvoiceId;
    private ArchiveInspection? inspectedArchive;

    // ------------------------------------------------------------------ draft state
    // The draft customer and the invoice filter customer are picked with the customer picker, so
    // they never depend on the customers that happen to be loaded in the customers screen.
    private Customer? draftCustomer;
    private bool draftCustomerTouched;
    private bool draftPaymentTouched;
    private bool replacing;
    private bool dailyBackupRunning;
    private readonly DispatcherTimer dailyBackupTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool suppressPaymentTouch;
    private TextBlock? draftCustomerLabel;
    private Button? draftCustomerButton;
    private Customer? invoiceFilterCustomer;
    private int invoiceOffset;
    private Button? invoicesPreviousButton;
    private Button? invoicesNextButton;
    private TextBlock? invoicesPageLabel;

    // ------------------------------------------------------------------ preview state
    // The preview is bound to one invoice; any change to that invoice invalidates it.
    private string? pdfInvoiceId;
    private string? previewReference;

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
        AppPaths.Ensure();
        store = Store.Open(AppPaths.DatabasePath);
        ProductsGrid.ItemsSource = productRows;
        CustomersGrid.ItemsSource = customerRows;
        InvoicesGrid.ItemsSource = invoiceRows;
        DraftLinesGrid.ItemsSource = draftRows;
        InvoicePaymentsList.ItemsSource = paymentRows;
        VersionText.Text = $"الإصدار {AppInfo.Version} — معاينة تطوير (إنتاج محلي)";
        DataPathText.Text = $"مجلد البيانات: {AppPaths.DataDirectory}";
        BuildDraftCustomerControls();
        BuildInvoiceListControls();
        DraftLinesGrid.CellEditEnding += DraftLinesGrid_CellEditEnding;
        DraftPaymentBox.TextChanged += DraftPaymentBox_TextChanged;
        DraftPaymentBox.LostKeyboardFocus += DraftPaymentBox_LostKeyboardFocus;
        Dispatcher.UnhandledException += Dispatcher_UnhandledException;
        ContentRendered += async (_, _) =>
        {
            await RefreshAll();
            App.WriteStartupEvidence(Path.Combine(AppPaths.LogDirectory, "startup.json"));
            await RunDailyBackup();
            StartDailyBackupSchedule();
        };
        Closing += (_, args) =>
        {
            if (busy)
            {
                args.Cancel = true;
                Say("انتظر اكتمال العملية قبل الإغلاق.");
                return;
            }
            dailyBackupTimer.Stop();
            PersistDraftState();
        };
        if (AccessControl.ReadLock().Enabled) ShowLock();
    }

    // ------------------------------------------------------------------ helpers

    private void Say(string message)
    {
        OperationStatus.Text = message;
    }

    private async Task Work(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (Exception error) { Say("تعذر إتمام العملية: " + error.Message); }
        finally { busy = false; }
    }

    private async Task<T> Run<T>(Func<T> action) => await Task.Run(action);

    private async Task Run(Action action) => await Task.Run(action);

    /// <summary>Creates a button that follows the application theme when the resource exists.</summary>
    private static Button NewButton(string content, string styleKey = "Raad.Button.Secondary")
    {
        var button = new Button { Content = content, MinHeight = 32, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current?.TryFindResource(styleKey) is Style style) button.Style = style;
        return button;
    }

    /// <summary>Removes the characters that Windows does not allow in a file name.</summary>
    private static string SafeFileName(string value) => ArchiveService.SafeFileName(value);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ runtime controls

    /// <summary>
    /// Adds the draft customer picker next to the draft controls. The customer of the draft is
    /// picked with a searchable, paged dialog instead of the combo box that only knows the
    /// customers already loaded by the customers screen.
    /// </summary>
    private void BuildDraftCustomerControls()
    {
        if (DraftPaymentBox.Parent is not Panel panel) return;
        draftCustomerButton = NewButton("اختيار عميل");
        draftCustomerButton.Click += DraftPickCustomer_Click;
        draftCustomerLabel = new TextBlock { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        draftCustomerLabel.SetResourceReference(TextBlock.ForegroundProperty, "Raad.Text.Secondary");
        var combo = FindName("DraftCustomerCombo") as ComboBox;
        var index = combo is null ? -1 : panel.Children.IndexOf(combo);
        var at = index >= 0 ? index + 1 : panel.Children.Count;
        panel.Children.Insert(at, draftCustomerButton);
        panel.Children.Insert(at + 1, draftCustomerLabel);
        if (combo is not null)
        {
            combo.IsEnabled = false;
            combo.ToolTip = "استخدم زر «اختيار عميل» لاختيار عميل الفاتورة.";
        }
        UpdateDraftCustomerDisplay();
    }

    /// <summary>Adds paging and customer filtering to the invoice list.</summary>
    private void BuildInvoiceListControls()
    {
        if (InvoiceSearchBox.Parent is not Panel panel) return;
        var filter = NewButton("تصفية بعميل");
        filter.Click += PickInvoiceFilterCustomer_Click;
        var clear = NewButton("إلغاء التصفية");
        clear.Click += ClearInvoiceFilter_Click;
        invoicesPreviousButton = NewButton("السابق");
        invoicesPreviousButton.Click += async (_, _) =>
        {
            invoiceOffset = Math.Max(0, invoiceOffset - Store.PageSize);
            await RefreshInvoices();
        };
        invoicesNextButton = NewButton("التالي");
        invoicesNextButton.Click += async (_, _) =>
        {
            invoiceOffset += Store.PageSize;
            await RefreshInvoices();
        };
        invoicesPageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        invoicesPageLabel.SetResourceReference(TextBlock.ForegroundProperty, "Raad.Text.Muted");
        panel.Children.Add(filter);
        panel.Children.Add(clear);
        panel.Children.Add(invoicesPreviousButton);
        panel.Children.Add(invoicesPageLabel);
        panel.Children.Add(invoicesNextButton);
        var combo = FindName("InvoiceCustomerCombo") as ComboBox;
        if (combo is not null)
        {
            combo.IsEnabled = false;
            combo.ToolTip = "استخدم زر «تصفية بعميل» لاختيار عميل التصفية.";
        }
        UpdateInvoiceFilterDisplay();
    }

    // ------------------------------------------------------------------ navigation

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        TodayPanel.Visibility = tag == "Today" ? Visibility.Visible : Visibility.Collapsed;
        InventoryPanel.Visibility = tag == "Inventory" ? Visibility.Visible : Visibility.Collapsed;
        InvoicesPanel.Visibility = tag == "Invoices" ? Visibility.Visible : Visibility.Collapsed;
        CustomersPanel.Visibility = tag == "Customers" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        _ = RefreshSection(tag);
    }

    private async Task RefreshSection(string tag)
    {
        switch (tag)
        {
            case "Today": await RefreshToday(); break;
            case "Inventory": await RefreshProducts(); break;
            case "Invoices": await RefreshInvoices(); await RefreshDraftProducts(); break;
            case "Customers": await RefreshCustomers(); break;
            case "Settings": await RefreshSettings(); break;
        }
    }

    private async Task RefreshAll()
    {
        await RefreshToday();
        await RefreshProducts();
        await RefreshCustomers();
        await RefreshInvoices();
        await RefreshDraft();
    }

    // ------------------------------------------------------------------ today

    private async Task RefreshToday()
    {
        var totals = await Run(() => store.Totals());
        TodaySalesValue.Text = ExactInput.FormatMoney(totals.Sales);
        TodayCollectionsValue.Text = ExactInput.FormatMoney(totals.Collections);
        OutstandingValue.Text = ExactInput.FormatMoney(totals.Outstanding);
        UnidentifiedValue.Text = ExactInput.FormatMoney(totals.Unidentified);
        UnidentifiedPanelValue.Text = ExactInput.FormatMoney(totals.Unidentified) + " EGP";
        OpenInvoicesValue.Text = $"عدد الفواتير المفتوحة: {totals.OpenInvoices}";
        TodayNotes.Text = "مبيعات اليوم هي إجمالي الفواتير الصادرة اليوم، والتحصيلات هي الدفعات الفعالة المستلمة اليوم. " +
            "المديونيات ترجع لكل التواريخ، والرصيد غير المرتبط بعميل يحتاج ربطًا يدويًا من شاشة الفواتير.";
    }

    // ------------------------------------------------------------------ products

    private async void ProductSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var revision = ++searchRevision;
        await Task.Delay(SearchDebounceMilliseconds);
        if (revision != searchRevision) return;
        productOffset = 0;
        await RefreshProducts();
    }

    private async Task RefreshProducts()
    {
        var query = ProductSearchBox.Text;
        var rows = await Run(() => store.Products(query, showingArchivedProducts, productOffset));
        productRows.Clear();
        foreach (var product in rows) productRows.Add(new ProductRow(product));
        ProductsPageLabel.Text = $"الصفحة {productOffset / Store.PageSize + 1} — {productRows.Count} صنف";
    }

    private async void ProductsPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (productOffset == 0) return;
        productOffset = Math.Max(0, productOffset - Store.PageSize);
        await RefreshProducts();
    }

    private async void ProductsNext_Click(object sender, RoutedEventArgs e)
    {
        if (productRows.Count < Store.PageSize) return;
        productOffset += Store.PageSize;
        await RefreshProducts();
    }

    private async void ShowArchivedProducts_Changed(object sender, RoutedEventArgs e)
    {
        showingArchivedProducts = ShowArchivedProductsCheck.IsChecked == true;
        productOffset = 0;
        await RefreshProducts();
    }

    private async void ShowArchivedCustomers_Changed(object sender, RoutedEventArgs e)
    {
        showingArchivedCustomers = ShowArchivedCustomersCheck.IsChecked == true;
        customerOffset = 0;
        await RefreshCustomers();
    }

    private void ProductsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) return;
        _ = Work(async () =>
        {
            var movements = await Run(() => store.Movements(row.Product.Id, 0));
            MovementsList.ItemsSource = movements.Select(m => $"{m.DisplayDate} — {m.DisplayKind} — {m.Change:+#;-#} — {m.Details}");
        });
    }

    private async void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var dialog = InputDialog.Show(this, "صنف جديد",
            ("الاسم", null), ("السعر", "0"), ("حد انخفاض المخزون", "0"), ("الكمية الافتتاحية", "0"));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.SaveProduct(null, dialog.Value("الاسم"), dialog.Value("السعر"),
                dialog.Value("حد انخفاض المخزون"), dialog.Value("الكمية الافتتاحية")));
            Say("تمت إضافة الصنف.");
            await RefreshProducts();
        });
    }

    private async void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) { Say("اختر صنفًا أولًا."); return; }
        var dialog = InputDialog.Show(this, "تعديل الصنف",
            ("الاسم", row.Product.Name), ("السعر", ExactInput.FormatPlainMoney(row.Product.Price)),
            ("حد انخفاض المخزون", row.Product.Threshold.ToString()));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.SaveProduct(row.Product.Id, dialog.Value("الاسم"), dialog.Value("السعر"),
                dialog.Value("حد انخفاض المخزون"), "0"));
            Say("تم تحديث الصنف.");
            await RefreshProducts();
        });
    }

    private async void AddStock_Click(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) { Say("اختر صنفًا أولًا."); return; }
        var dialog = InputDialog.Show(this, "إضافة مخزون", ("الكمية المضافة", "1"));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.AdjustStock(row.Product.Id, dialog.Value("الكمية المضافة"), null));
            Say("تمت إضافة الكمية.");
            await RefreshProducts();
        });
    }

    private async void CorrectStock_Click(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) { Say("اختر صنفًا أولًا."); return; }
        var dialog = InputDialog.Show(this, "تصحيح كمية الجرد",
            ("الكمية الفعلية", row.Product.Quantity.ToString()), ("سبب التصحيح", null));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.AdjustStock(row.Product.Id, dialog.Value("الكمية الفعلية"), dialog.Value("سبب التصحيح")));
            Say("تم تصحيح المخزون.");
            await RefreshProducts();
        });
    }

    private async void ArchiveProduct_Click(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) { Say("اختر صنفًا أولًا."); return; }
        await Work(async () =>
        {
            await Run(() => store.Archive(row.Product.Id, false, true));
            Say("تمت أرشفة الصنف.");
            await RefreshProducts();
        });
    }

    private async void RestoreProduct_Click(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) { Say("اختر صنفًا أولًا."); return; }
        await Work(async () =>
        {
            await Run(() => store.Archive(row.Product.Id, false, false));
            Say("تمت استعادة الصنف.");
            await RefreshProducts();
        });
    }

    // ------------------------------------------------------------------ customers

    private async void CustomerSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var revision = ++searchRevision;
        await Task.Delay(SearchDebounceMilliseconds);
        if (revision != searchRevision) return;
        customerOffset = 0;
        await RefreshCustomers();
    }

    private async Task RefreshCustomers()
    {
        var query = CustomerSearchBox.Text;
        var rows = await Run(() => store.Customers(query, showingArchivedCustomers, customerOffset));
        customerRows.Clear();
        foreach (var customer in rows) customerRows.Add(customer);
        CustomersPageLabel.Text = $"الصفحة {customerOffset / Store.PageSize + 1} — {customerRows.Count} عميل";
    }

    private async void CustomersPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (customerOffset == 0) return;
        customerOffset = Math.Max(0, customerOffset - Store.PageSize);
        await RefreshCustomers();
    }

    private async void CustomersNext_Click(object sender, RoutedEventArgs e)
    {
        if (customerRows.Count < Store.PageSize) return;
        customerOffset += Store.PageSize;
        await RefreshCustomers();
    }

    private void CustomersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => Say("");

    private async void NewCustomer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = InputDialog.Show(this, "عميل جديد", ("الاسم", null), ("الهاتف (اختياري)", ""));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.SaveCustomer(null, dialog.Value("الاسم"), dialog.Value("الهاتف (اختياري)")));
            Say("تمت إضافة العميل.");
            await RefreshCustomers();
        });
    }

    private async void EditCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not Customer customer) { Say("اختر عميلًا أولًا."); return; }
        var dialog = InputDialog.Show(this, "تعديل العميل", ("الاسم", customer.Name), ("الهاتف (اختياري)", customer.Phone));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.SaveCustomer(customer.Id, dialog.Value("الاسم"), dialog.Value("الهاتف (اختياري)")));
            Say("تم تحديث العميل.");
            await RefreshCustomers();
        });
    }

    private async void ArchiveCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not Customer customer) { Say("اختر عميلًا أولًا."); return; }
        await Work(async () =>
        {
            await Run(() => store.Archive(customer.Id, true, true));
            Say("تمت أرشفة العميل.");
            await RefreshCustomers();
        });
    }

    private async void RestoreCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not Customer customer) { Say("اختر عميلًا أولًا."); return; }
        await Work(async () =>
        {
            await Run(() => store.Archive(customer.Id, true, false));
            Say("تمت استعادة العميل.");
            await RefreshCustomers();
        });
    }

    // ------------------------------------------------------------------ draft

    private async void DraftProductSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var revision = ++searchRevision;
        await Task.Delay(SearchDebounceMilliseconds);
        if (revision != searchRevision) return;
        var query = DraftProductSearchBox.Text;
        var rows = await Run(() => store.Products(query, false, 0));
        DraftProductCombo.ItemsSource = rows;
        if (rows.Count > 0) DraftProductCombo.SelectedIndex = 0;
    }

    private async void DraftAddProduct_Click(object sender, RoutedEventArgs e)
    {
        if (DraftProductCombo.SelectedItem is not Product product) { Say("اختر صنفًا أولًا."); return; }
        await Work(async () =>
        {
            PersistDraftPayment();
            await Run(() => store.AddDraftLine(product.Id));
            Say("تمت إضافة الصنف إلى المسودة.");
            await RefreshDraft();
        });
    }

    private async void DraftRemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not DraftRow row) return;
        await Work(async () =>
        {
            await Run(() => store.RemoveDraftLine(row.ProductId));
            await RefreshDraft();
        });
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await SaveDraftAsync();

    private async Task SaveDraftAsync()
    {
        // The values are read on the UI thread; the engine call only receives plain data.
        var lines = SnapshotDraftLines();
        var payment = DraftPaymentBox.Text;
        var customerId = draftCustomer?.Id;
        await Work(async () =>
        {
            await Run(() => store.SaveDraft(customerId, payment, lines));
            draftPaymentTouched = true;
            Say("تم حفظ المسودة.");
        });
    }

    private async void DiscardDraft_Click(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        await Run(() => store.DiscardDraft());
        draftRows.Clear();
        SetPaymentBoxText(string.Empty);
        draftPaymentTouched = false;
        draftCustomer = null;
        draftCustomerTouched = false;
        UpdateDraftCustomerDisplay();
        await RefreshDraft();
        Say("تم مسح المسودة.");
    });

    /// <summary>
    /// Reloads the draft from the store without wiping what the operator typed: the rows that
    /// still exist keep their raw values, and the payment box is only filled when it is empty.
    /// </summary>
    private async Task RefreshDraft()
    {
        var draft = await Run(() => store.LoadDraft());
        if (draft is null)
        {
            draftRows.Clear();
            UpdateDraftTotal();
            return;
        }
        var stored = draft.Lines.Select(line => line.ProductId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in draftRows.Where(row => !stored.Contains(row.ProductId)).ToList())
            draftRows.Remove(row);
        foreach (var line in draft.Lines)
        {
            if (draftRows.Any(row => row.ProductId == line.ProductId)) continue;
            draftRows.Add(new DraftRow { ProductId = line.ProductId, Name = line.Name, Quantity = line.Quantity, UnitPrice = line.UnitPrice });
        }
        if (!draftCustomerTouched && draft.Customer is not null)
        {
            draftCustomer = draft.Customer;
            UpdateDraftCustomerDisplay();
        }
        if (!draftPaymentTouched && DraftPaymentBox.Text.Length == 0 && draft.InitialPayment.Length > 0)
            SetPaymentBoxText(draft.InitialPayment);
        UpdateDraftTotal();
    }

    private void UpdateDraftTotal()
    {
        long total = 0;
        foreach (var row in draftRows)
        {
            if (!long.TryParse(row.Quantity, out var quantity) || quantity <= 0) continue;
            if (!decimal.TryParse(row.UnitPrice, out var price)) continue;
            total += (long)(price * 100) * quantity;
        }
        DraftTotalLabel.Text = "إجمالي المسودة (تقديري): " + ExactInput.FormatMoney(total) + " EGP";
    }

    // ------------------------------------------------------------------ draft autosave

    /// <summary>Persists the edited raw quantity and price of a draft row as soon as the cell is left.</summary>
    private void DraftLinesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (replacing) return;
        if (e.Row?.Item is not DraftRow row) return;
        // The editor holds the raw text the operator typed, including Arabic digits.
        var raw = (e.EditingElement as TextBox)?.Text;
        if (raw is not null)
        {
            var path = ((e.Column as DataGridTextColumn)?.Binding as System.Windows.Data.Binding)?.Path?.Path;
            if (path == nameof(DraftRow.Quantity)) row.Quantity = raw;
            else if (path == nameof(DraftRow.UnitPrice)) row.UnitPrice = raw;
        }
        UpdateDraftTotal();
        try { store.SetDraftLine(row.ProductId, row.Quantity, row.UnitPrice); }
        catch (Exception error) { Say("تعذر حفظ السطر في المسودة: " + error.Message); }
    }
    private void DraftPaymentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!suppressPaymentTouch) draftPaymentTouched = true;
    }

    private void DraftPaymentBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        draftPaymentTouched = true;
        PersistDraftPayment();
    }

    private void PersistDraftPayment()
    {
        try { store.SetDraftPayment(DraftPaymentBox.Text); } catch { /* no open draft yet */ }
    }

    /// <summary>Keeps the unsaved draft on disk when the window closes.</summary>
    private void PersistDraftState()
    {
        try { DraftLinesGrid.CommitEdit(); } catch (InvalidOperationException) { }
        PersistDraftPayment();
        foreach (var row in draftRows)
        {
            try { store.SetDraftLine(row.ProductId, row.Quantity, row.UnitPrice); } catch { /* the draft may be gone */ }
        }
    }

    private void SetPaymentBoxText(string value)
    {
        suppressPaymentTouch = true;
        try { DraftPaymentBox.Text = value; }
        finally { suppressPaymentTouch = false; }
    }

    /// <summary>Reads the raw draft values on the UI thread so nothing touches WPF off the dispatcher.</summary>
    private List<DraftLine> SnapshotDraftLines() => draftRows
        .Select(row => new DraftLine(row.ProductId, row.Name, row.Quantity, row.UnitPrice))
        .ToList();

    // ------------------------------------------------------------------ customer picking

    private async void DraftPickCustomer_Click(object sender, RoutedEventArgs e)
    {
        var pick = CustomerPicker.Show(this, store, "اختيار عميل الفاتورة");
        if (pick is null) return;
        draftCustomer = pick.Customer;
        draftCustomerTouched = true;
        UpdateDraftCustomerDisplay();
        try
        {
            await Run(() => store.SetDraftCustomer(pick.Customer?.Id));
            Say(pick.IsWalkIn ? "تم اختيار عميل نقدي للمسودة." : $"تم اختيار العميل {pick.CustomerName} للمسودة.");
        }
        catch (Exception error) { Say("تعذر حفظ العميل في المسودة: " + error.Message); }
    }

    private void UpdateDraftCustomerDisplay()
    {
        var name = draftCustomer?.Name ?? WalkInCustomerName;
        if (draftCustomerLabel is not null) draftCustomerLabel.Text = "العميل: " + name;
        if (draftCustomerButton is not null) draftCustomerButton.ToolTip = name;
        var combo = FindName("DraftCustomerCombo") as ComboBox;
        if (combo is null) return;
        combo.Items.Clear();
        if (draftCustomer is not null) combo.Items.Add(draftCustomer);
    }

    private async void PickInvoiceFilterCustomer_Click(object sender, RoutedEventArgs e)
    {
        var pick = CustomerPicker.Show(this, store, "تصفية الفواتير حسب العميل");
        if (pick is null) return;
        invoiceFilterCustomer = pick.Customer;
        invoiceOffset = 0;
        UpdateInvoiceFilterDisplay();
        await RefreshInvoices();
    }

    private async void ClearInvoiceFilter_Click(object sender, RoutedEventArgs e)
    {
        invoiceFilterCustomer = null;
        invoiceOffset = 0;
        UpdateInvoiceFilterDisplay();
        await RefreshInvoices();
    }

    private void UpdateInvoiceFilterDisplay()
    {
        var combo = FindName("InvoiceCustomerCombo") as ComboBox;
        if (combo is null) return;
        combo.Items.Clear();
        if (invoiceFilterCustomer is not null) combo.Items.Add(invoiceFilterCustomer);
    }

    /// <summary>
    /// Issues the draft with one guarded engine call: the store saves and issues the same draft
    /// id, so a failed save can never issue stale rows.
    /// </summary>
    private async void IssueInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true;
        try
        {
            // 1. Commit the cell that is being edited.
            try { DraftLinesGrid.CommitEdit(); } catch (InvalidOperationException) { }

            // 2. Snapshot every value on the UI thread.
            var lines = SnapshotDraftLines();
            var payment = DraftPaymentBox.Text;
            var customerId = draftCustomer?.Id;
            var draft = await Run(() => store.LoadDraft());
            var draftId = draft?.Id;
            if (draftId is null)
            {
                Say("لا توجد مسودة مفتوحة. أضف صنفًا واحدًا على الأقل قبل الإصدار.");
                return;
            }

            // 3. Exactly one guarded engine call.
            IssuedInvoice? issued = null;
            string? failure = null;
            try { issued = await Run(() => store.SaveAndIssue(customerId, payment, lines)); }
            catch (Exception error) { failure = error.Message; }
            if (issued is null)
            {
                // 4b. The inputs stay untouched and nothing is issued.
                Say("تعذر إصدار الفاتورة: " + (failure ?? "لم يُرجع المحرك فاتورة صادرة."));
                return;
            }

            // 4a. Success: the draft is done, so the inputs can be cleared.
            draftRows.Clear();
            SetPaymentBoxText(string.Empty);
            draftPaymentTouched = false;
            draftCustomer = null;
            draftCustomerTouched = false;
            UpdateDraftCustomerDisplay();
            InvalidatePreview();
            // The issued invoice stays visible even when an old search or filter is active.
            invoiceFilterCustomer = null;
            invoiceOffset = 0;
            UpdateInvoiceFilterDisplay();
            await RefreshInvoices();
            await RefreshToday();
            await RefreshProducts();
            await RefreshDraft();
            await SelectInvoice(issued.Id);
            Say($"تم إصدار الفاتورة {issued.Reference}.");
        }
        finally { busy = false; }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // While the privacy lock is on only the application's own shortcuts are suppressed, so the
        // operator can still type the password and use the keyboard.
        if (locked && Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.S || e.Key == Key.P))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = SaveDraftAsync();
            e.Handled = true;
        }
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var document = pdf;
            if (document is null || pdfInvoiceId is null || pdfInvoiceId != selectedInvoiceId)
            {
                Say("لا توجد معاينة جاهزة للطباعة. أعد تجهيز المعاينة.");
                e.Handled = true;
                return;
            }
            _ = Work(async () => { Say(await document.Print() ? "تم تسليم المهمة إلى الطابعة." : "أُلغيت الطباعة."); });
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ startup robustness

    /// <summary>
    /// Shows an Arabic message instead of letting an unexpected error kill the window, and keeps
    /// the details in the application log for support.
    /// </summary>
    private void Dispatcher_UnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandled(e.Exception);
        try
        {
            MessageBox.Show(this,
                "حدث خطأ غير متوقع أثناء تنفيذ العملية.\n\n" + e.Exception.Message +
                "\n\nتم تسجيل التفاصيل في ملف سجل الأخطاء.",
                "رعد — خطأ غير متوقع", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* the window may already be closing */ }
        e.Handled = true;
    }

    private static void LogUnhandled(Exception error)
    {
        try
        {
            var directory = AppPaths.LogDirectory;
            Directory.CreateDirectory(directory);
            var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "error.log"), entry, Encoding.UTF8);
        }
        catch { /* logging must never throw */ }
    }

    // ------------------------------------------------------------------ invoices

    private async void InvoiceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var revision = ++searchRevision;
        await Task.Delay(SearchDebounceMilliseconds);
        if (revision != searchRevision) return;
        invoiceOffset = 0;
        await RefreshInvoices();
    }

    private async void FilterInvoices_Click(object sender, RoutedEventArgs e) => await RefreshInvoices();

    private async Task RefreshInvoices()
    {
        var query = InvoiceSearchBox.Text;
        var customerId = invoiceFilterCustomer?.Id;
        var rows = await Run(() => store.Invoices(query, customerId, invoiceOffset));
        invoiceRows.Clear();
        foreach (var invoice in rows) invoiceRows.Add(invoice);
        if (invoicesPageLabel is not null)
            invoicesPageLabel.Text = $"الصفحة {invoiceOffset / Store.PageSize + 1} — {invoiceRows.Count} فاتورة";
        if (invoicesPreviousButton is not null) invoicesPreviousButton.IsEnabled = invoiceOffset > 0;
        if (invoicesNextButton is not null) invoicesNextButton.IsEnabled = invoiceRows.Count >= Store.PageSize;
        RestoreInvoiceSelection();
    }

    /// <summary>Keeps the selected invoice selected across a refresh, or clears it when it left the page.</summary>
    private void RestoreInvoiceSelection()
    {
        if (selectedInvoiceId is null) return;
        var entry = invoiceRows.FirstOrDefault(row => row.Id == selectedInvoiceId);
        if (entry is null)
        {
            selectedInvoiceId = null;
            paymentRows.Clear();
            InvoicePaymentCombo.ItemsSource = paymentRows;
            InvoiceDetailLabel.Text = "";
            InvalidatePreview();
            return;
        }
        if (!ReferenceEquals(InvoicesGrid.SelectedItem, entry)) InvoicesGrid.SelectedItem = entry;
    }

    private async void InvoicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InvoicesGrid.SelectedItem is not InvoiceEntry entry) return;
        if (selectedInvoiceId == entry.Id) return;
        selectedInvoiceId = entry.Id;
        // The preview belonged to the previous invoice, so it can no longer be printed or exported.
        InvalidatePreview();
        await LoadInvoiceDetail(entry.Id);
    }

    /// <summary>Selects an invoice row and loads its details, for example right after issuing.</summary>
    private async Task SelectInvoice(string invoiceId)
    {
        var row = invoiceRows.FirstOrDefault(entry => entry.Id == invoiceId);
        if (row is not null && !ReferenceEquals(InvoicesGrid.SelectedItem, row)) InvoicesGrid.SelectedItem = row;
        selectedInvoiceId = invoiceId;
        await LoadInvoiceDetail(invoiceId);
    }

    private async Task LoadInvoiceDetail(string invoiceId)
    {
        var detail = await Run(() => store.InvoiceDetailById(invoiceId));
        if (detail is null) return;
        InvoiceDetailLabel.Text = $"{detail.Reference} — {detail.Customer} — الإجمالي {detail.DisplayTotal} — المدفوع {detail.DisplayPaid} — المتبقي {detail.DisplayBalance} — {detail.DisplayStatus}";
        // The sold items are shown with their exact identifiers so the operator can verify the sale.
        InvoiceLinesList.ItemsSource = detail.Lines.Select(line =>
            $"{line.Quantity} × {line.Name} @ {ExactInput.FormatMoney(line.UnitPrice)} = {ExactInput.FormatMoney(line.Total)} — {line.ProductId}").ToList();
        paymentRows.Clear();
        foreach (var payment in detail.Payments) paymentRows.Add(new PaymentRow(payment));
        InvoicePaymentCombo.ItemsSource = paymentRows;
        if (paymentRows.Count > 0) InvoicePaymentCombo.SelectedIndex = 0;
    }

    private async void AddPayment_Click(object sender, RoutedEventArgs e)
    {
        var invoiceId = selectedInvoiceId;
        if (invoiceId is null) { Say("اختر فاتورة أولًا."); return; }
        var dialog = InputDialog.Show(this, "تحصيل دفعة", ("المبلغ", "0"));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.AddPayment(invoiceId, dialog.Value("المبلغ")));
            InvalidatePreview();
            Say("تم تسجيل الدفعة.");
            await RefreshInvoices();
            await RefreshToday();
            await RefreshSelectedInvoice();
        });
    }

    private async void ReversePayment_Click(object sender, RoutedEventArgs e)
    {
        var invoiceId = selectedInvoiceId;
        if (invoiceId is null) { Say("اختر فاتورة أولًا."); return; }
        if (InvoicePaymentCombo.SelectedItem is not PaymentRow payment) { Say("اختر الدفعة أولًا."); return; }
        var dialog = InputDialog.Show(this, "تصحيح دفعة",
            ("سبب العكس", null), ("المبلغ التصحيحي (اتركه فارغًا للإلغاء فقط)", ""));
        if (dialog is null) return;
        var replacement = dialog.Value("المبلغ التصحيحي (اتركه فارغًا للإلغاء فقط)");
        await Work(async () =>
        {
            await Run(() => store.ReversePayment(payment.Payment.Id, dialog.Value("سبب العكس"),
                string.IsNullOrWhiteSpace(replacement) ? null : replacement));
            InvalidatePreview();
            Say("تم عكس الدفعة.");
            await RefreshInvoices();
            await RefreshToday();
            await RefreshSelectedInvoice();
        });
    }

    private async void CancelInvoice_Click(object sender, RoutedEventArgs e)
    {
        var invoiceId = selectedInvoiceId;
        if (invoiceId is null) { Say("اختر فاتورة أولًا."); return; }
        var dialog = InputDialog.Show(this, "إلغاء فاتورة", ("سبب الإلغاء", null));
        if (dialog is null) return;
        await Work(async () =>
        {
            await Run(() => store.CancelInvoice(invoiceId, dialog.Value("سبب الإلغاء")));
            InvalidatePreview();
            Say("تم إلغاء الفاتورة ورد المخزون مرة واحدة.");
            await RefreshInvoices();
            await RefreshToday();
            await RefreshProducts();
            await RefreshSelectedInvoice();
        });
    }

    private async void LinkCustomer_Click(object sender, RoutedEventArgs e)
    {
        var invoiceId = selectedInvoiceId;
        if (invoiceId is null) { Say("اختر فاتورة أولًا."); return; }
        var pick = CustomerPicker.Show(this, store, "ربط الفاتورة بعميل");
        if (pick is null) return;
        if (pick.Customer is null) { Say("اختر عميلًا لربط الفاتورة به."); return; }
        var customerId = pick.CustomerId!;
        await Work(async () =>
        {
            await Run(() => store.LinkCustomer(invoiceId, customerId));
            InvalidatePreview();
            Say($"تم ربط الفاتورة بالعميل {pick.CustomerName}.");
            await RefreshInvoices();
            await RefreshToday();
            await RefreshCustomers();
            await RefreshSelectedInvoice();
        });
    }

    private async Task RefreshSelectedInvoice()
    {
        if (selectedInvoiceId is null) return;
        await LoadInvoiceDetail(selectedInvoiceId);
    }

    /// <summary>Prints the invoice that is currently shown in the preview pane.</summary>
    private async void PrintInvoice_Click(object sender, RoutedEventArgs e)
    {
        var document = pdf;
        if (document is null || pdfInvoiceId is null || pdfInvoiceId != selectedInvoiceId)
        {
            Say("لا توجد معاينة جاهزة للطباعة. أعد تجهيز المعاينة.");
            return;
        }
        await Work(async () => { Say(await document.Print() ? "تم تسليم المهمة إلى الطابعة." : "أُلغيت الطباعة."); });
    }

    /// <summary>Fills the draft product selector with the available products.</summary>
    private async Task RefreshDraftProducts()
    {
        var query = DraftProductSearchBox.Text;
        var rows = await Run(() => store.Products(query, false, 0));
        DraftProductCombo.ItemsSource = rows;
        if (rows.Count > 0 && DraftProductCombo.SelectedItem is null) DraftProductCombo.SelectedIndex = 0;
    }

    private async void PreviewInvoice_Click(object sender, RoutedEventArgs e)
    {
        var invoiceId = selectedInvoiceId;
        if (invoiceId is null) { Say("اختر فاتورة أولًا."); return; }
        await Work(async () =>
        {
            var snapshot = await Run(() => store.Snapshot(invoiceId));
            if (snapshot is null) throw new InvalidOperationException("الفاتورة غير موجودة.");
            var name = SafeFileName(snapshot.Reference);
            var directory = Path.Combine(AppPaths.ExportDirectory, name);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, name + ".pdf");
            await Task.Run(() => InvoiceDocument.Save(snapshot, file, App.Assets));
            var document = await PdfPages.Open(file);
            pdf?.Dispose();
            pdf = document;
            pdfPath = file;
            pdfInvoiceId = invoiceId;
            previewReference = snapshot.Reference;
            pageIndex = 0;
            await ShowPage(0);
            Say($"تم تجهيز معاينة {snapshot.Reference}.");
        });
    }

    /// <summary>Drops the prepared preview so nothing stale can be printed or exported.</summary>
    private void InvalidatePreview()
    {
        pdf?.Dispose();
        pdf = null;
        pdfPath = null;
        pdfInvoiceId = null;
        previewReference = null;
        pageIndex = 0;
        PreviewImage.Source = null;
        PreviewPageLabel.Text = "";
    }

    private async Task ShowPage(int index)
    {
        if (pdf is null || index < 0 || index >= pdf.Count) return;
        pageIndex = index;
        PreviewImage.Source = PdfPages.Bitmap(await pdf.Render(index, 1000));
        PreviewPageLabel.Text = $"{index + 1} / {pdf.Count}";
    }

    private async void PreviewPrevious_Click(object sender, RoutedEventArgs e) => await Work(() => ShowPage(pageIndex - 1));
    private async void PreviewNext_Click(object sender, RoutedEventArgs e) => await Work(() => ShowPage(pageIndex + 1));

    private async void ExportInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (pdf is null || pdfPath is null || pdfInvoiceId is null) { Say("جهّز معاينة أولًا."); return; }
        if (pdfInvoiceId != selectedInvoiceId)
        {
            Say("المعاينة تخص فاتورة أخرى. أعد تجهيز المعاينة أولًا.");
            InvalidatePreview();
            return;
        }
        var document = pdf;
        var source = pdfPath;
        var name = SafeFileName(previewReference ?? Path.GetFileNameWithoutExtension(source));
        await Work(async () =>
        {
            var dialog = new SaveFileDialog { FileName = name + ".pdf", Filter = "PDF|*.pdf|PNG pages|*.png|JPG pages|*.jpg", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            if (dialog.FilterIndex == 1)
            {
                PublishFile(source, dialog.FileName);
            }
            else
            {
                // The images are rendered into a private staging folder and only then published,
                // so a failed export never removes or damages what the operator already has.
                var staging = Path.Combine(Path.GetTempPath(), "RaadExport", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    await document.ExportImages(staging, name, dialog.FilterIndex == 3);
                    var destination = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName));
                    PublishDirectory(staging, destination);
                }
                finally { TryDeleteDirectory(staging); }
            }
            Say("تم حفظ المستند.");
        });
    }

    /// <summary>Copies one file over the destination without touching anything else there.</summary>
    private static void PublishFile(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = destination + ".raad-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, true);
            File.Move(temporary, destination, true);
        }
        catch { TryDeleteFile(temporary); throw; }
    }

    /// <summary>
    /// Copies the staged files over the destination folder. The folder itself is never deleted,
    /// and the previous files stay in place until every new file is ready.
    /// </summary>
    private static void PublishDirectory(string staging, string destination)
    {
        Directory.CreateDirectory(destination);
        var pending = new List<(string Temporary, string Final)>();
        try
        {
            foreach (var file in Directory.GetFiles(staging))
            {
                var final = Path.Combine(destination, Path.GetFileName(file));
                var temporary = final + ".raad-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(file, temporary, true);
                pending.Add((temporary, final));
            }
            foreach (var (temporary, final) in pending) File.Move(temporary, final, true);
        }
        catch
        {
            foreach (var (temporary, _) in pending) TryDeleteFile(temporary);
            throw;
        }
    }

    // ------------------------------------------------------------------ settings

    private async Task RefreshSettings()
    {
        var activation = AccessControl.ReadActivation();
        var lockState = AccessControl.ReadLock();
        ActivationBadge.Text = activation.IsLicensedProduction ? "مفعّل" : "معاينة — غير مفعّل";
        AccessStatus.Text = $"التفعيل: {activation.DisplayKind}. القفل: {(lockState.Enabled ? "مفعّل" : "معطل")}.";
    }

    private async void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        await Work(async () =>
        {
            var dialog = new SaveFileDialog { FileName = $"raad-backup-{DateTime.Now:yyyyMMdd-HHmmss}", Filter = "Raad backup|*.raadbackup" };
            if (dialog.ShowDialog(this) != true) { Say("تم إلغاء النسخة."); return; }
            // The engine writes the archive at the exact path the operator chose.
            var result = await Run(() => BackupService.CreateManual(store.Database, dialog.FileName));
            BackupStatus.Text = $"تم إنشاء نسخة احتياطية: {result.Path} ({result.Bytes} بايت).";
        });
    }

    /// <summary>
    /// Creates the daily automatic backup when it is due. The call is non-reentrant: it never
    /// overlaps a running operation (backup, restore or import) and it keeps the last successful
    /// status when the backup of the day was already made.
    /// </summary>
    private async Task RunDailyBackup()
    {
        if (busy || dailyBackupRunning) return;
        dailyBackupRunning = true;
        try
        {
            var result = await Run(() => BackupService.EnsureDaily(store.Database, AppPaths.AutomaticBackupDirectory));
            if (result is not null) BackupStatus.Text = $"نسخة يومية تلقائية: {result.Path}";
        }
        catch (Exception error) { BackupStatus.Text = "تعذر إنشاء النسخة اليومية التلقائية: " + error.Message; }
        finally { dailyBackupRunning = false; }
    }

    /// <summary>Starts the retry and day-rollover trigger for the daily automatic backup.</summary>
    private void StartDailyBackupSchedule()
    {
        dailyBackupTimer.Tick += async (_, _) => await RunDailyBackup();
        dailyBackupTimer.Start();
        // Focus on the window is the second trigger, so a failed backup is retried on the next visit.
        Activated += async (_, _) => await RunDailyBackup();
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        await Work(async () =>
        {
            var dialog = new OpenFileDialog { Filter = "Raad backup|*.raadbackup" };
            if (dialog.ShowDialog(this) != true) { Say("تم إلغاء الاستعادة."); return; }
            // The archive is inspected once and the same inspected generation is used for the
            // confirmation and for the replacement.
            var inspection = await Run(() => ArchiveService.Inspect(dialog.FileName, ArchiveKind.WindowsBackup, AppPaths.StagingDirectory));
            try
            {
                if (!ConfirmDialog.Show(this, "تأكيد الاستعادة",
                    $"النسخة من الإصدار {inspection.AppVersion} بتاريخ {inspection.CreatedAt:yyyy-MM-dd}." + Environment.NewLine +
                    DescribeArchive(inspection) + Environment.NewLine +
                    "الاستعادة تستبدل بيانات ويندوز الحالية بالكامل بعد أخذ نسخة أمان.")) return;
                var report = await Run(() => ArchiveService.Apply(store.Database, inspection, AppPaths.SafetyBackupDirectory));
                store = Store.Open(AppPaths.DatabasePath);
                ResetAfterReplacement();
                BackupStatus.Text = $"تمت الاستعادة. نسخة الأمان: {report.SafetyArchive}";
                await RefreshAll();
            }
            finally { ArchiveService.ReleaseStaging(inspection); }
        });
    }

    /// <summary>
    /// Clears everything that belonged to the previous data set before the new one is loaded, so a
    /// stale draft, filter or preview cannot override the replaced data.
    /// </summary>
    private void ResetAfterReplacement()
    {
        replacing = true;
        try
        {
            draftRows.Clear();
            SetPaymentBoxText(string.Empty);
            draftPaymentTouched = false;
            draftCustomer = null;
            draftCustomerTouched = false;
            UpdateDraftCustomerDisplay();
            InvalidatePreview();
            selectedInvoiceId = null;
            invoiceFilterCustomer = null;
            invoiceOffset = 0;
            UpdateInvoiceFilterDisplay();
            productOffset = 0;
            customerOffset = 0;
            productRows.Clear();
            customerRows.Clear();
            invoiceRows.Clear();
            paymentRows.Clear();
            InvoiceDetailLabel.Text = string.Empty;
            InvoiceLinesList.ItemsSource = null;
        }
        finally { replacing = false; }
    }

    private async void InspectTransfer_Click(object sender, RoutedEventArgs e)
    {
        await Work(async () =>
        {
            var dialog = new OpenFileDialog { Filter = "Raad transfer|*.raadbackup;*.zip" };
            if (dialog.ShowDialog(this) != true) { Say("تم إلغاء الفحص."); return; }
            var inspection = await Run(() => ArchiveService.Inspect(dialog.FileName, ArchiveKind.AndroidTransfer, AppPaths.StagingDirectory));
            // A superseded inspection releases its private staging generation.
            if (inspectedArchive is not null) ArchiveService.ReleaseStaging(inspectedArchive);
            inspectedArchive = inspection;
            TransferStatus.Text = "مصدر صالح: " + DescribeArchive(inspection) +
                " لم تُستبدل البيانات بعد — أكّد الاستبدال.";
        });
    }

    private async void ApplyTransfer_Click(object sender, RoutedEventArgs e)
    {
        var archive = inspectedArchive;
        if (archive is null) { Say("افحص ملف النقل أولًا."); return; }
        await Work(async () =>
        {
            try
            {
                if (!ConfirmDialog.Show(this, "تأكيد الاستبدال",
                    "سيستبدل التطبيق بيانات ويندوز الحالية ببيانات رعد لأندرويد. تُؤخذ نسخة أمان قبل التبديل." + Environment.NewLine +
                    DescribeArchive(archive))) return;
                var report = await Run(() => ArchiveService.Apply(store.Database, archive, AppPaths.SafetyBackupDirectory));
                store = Store.Open(AppPaths.DatabasePath);
                inspectedArchive = null;
                ResetAfterReplacement();
                TransferStatus.Text = $"تم استبدال البيانات. نسخة الأمان: {report.SafetyArchive}";
                await RefreshAll();
            }
            finally { ArchiveService.ReleaseStaging(archive); }
        });
    }

    /// <summary>Arabic summary of an inspected archive: counts, money in the archive currency, stock.</summary>
    private static string DescribeArchive(ArchiveInspection inspection)
    {
        var divisor = (decimal)Math.Pow(10, Math.Max(0, inspection.MoneyMinorUnits));
        var currency = string.IsNullOrWhiteSpace(inspection.Currency) ? "EGP" : inspection.Currency;
        string Money(long minor) => (minor / divisor).ToString("N2", CultureInfo.InvariantCulture) + " " + currency;
        var text = $"الأصناف: {inspection.Products} — العملاء: {inspection.Customers} — الفواتير: {inspection.Invoices}. " +
            $"إجمالي المبيعات: {Money(inspection.Sales)} — المحصّل: {Money(inspection.Paid)} — " +
            $"المتبقي: {Money(inspection.Outstanding)} — إجمالي المخزون: {inspection.StockTotal}.";
        if (inspection.LegacyDeletionTraces > 0)
            text += " تنبيه: يوجد آثار حذف فواتير قديمة في المصدر.";
        return text;
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = InputDialog.Show(this, "تفعيل (إعداد مالك للمعاينة)", ("كلمة المرور", ""));
        if (dialog is null) return;
        try
        {
            var activation = AccessControl.OwnerSetup(dialog.Value("كلمة المرور"));
            ActivationBadge.Text = activation.DisplayKind;
            AccessStatus.Text = $"التفعيل: {activation.DisplayKind}. هذا ليس ترخيص منتج في المعاينة.";
            Say("تم التفعيل المحلي.");
        }
        catch (Exception error) { Say("تعذر التفعيل: " + error.Message); }
    }

    private void EnableLock_Click(object sender, RoutedEventArgs e)
    {
        var dialog = InputDialog.Show(this, "تفعيل قفل الخصوصية",
            ("!كلمة مرور القفل", ""), ("!تأكيد كلمة المرور", ""));
        if (dialog is null) return;
        var password = dialog.Value("!كلمة مرور القفل");
        if (password != dialog.Value("!تأكيد كلمة المرور")) { Say("كلمتا المرور غير متطابقتين."); return; }
        try
        {
            var recovery = AccessControl.EnableLock(password);
            Say("تم تفعيل القفل. رمز الاسترداد: " + recovery + " — احتفظ به الآن.");
            _ = RefreshSettings();
        }
        catch (Exception error) { Say("تعذر تفعيل القفل: " + error.Message); }
    }

    private void DisableLock_Click(object sender, RoutedEventArgs e)
    {
        var state = AccessControl.ReadLock();
        if (!state.Enabled) { Say("قفل الخصوصية معطّل بالفعل."); return; }
        var dialog = InputDialog.Show(this, "تعطيل قفل الخصوصية", ("!كلمة مرور القفل الحالية", ""));
        if (dialog is null) return;
        if (!AccessControl.Unlock(dialog.Value("!كلمة مرور القفل الحالية")))
        {
            Say("كلمة المرور غير صحيحة. القفل لم يتغير.");
            return;
        }
        AccessControl.DisableLock();
        Say("تم تعطيل قفل الخصوصية.");
        _ = RefreshSettings();
    }

    /// <summary>
    /// Documented local reset for the owner of the Windows account when both the password and the
    /// recovery code are lost. It clears the lock state only and never touches the business data.
    /// </summary>
    private void ResetLockLocally_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Show(this, "إعادة ضبط القفل محليًا",
            "إعادة الضبط المحلية موثّقة لصاحب حساب ويندوز: تُحذف حالة القفل فقط، وتبقى بيانات العمل كما هي. " +
            "هل تريد المتابعة؟")) return;
        AccessControl.SupportReset();
        Say("تمت إعادة ضبط القفل. بيانات العمل لم تتغير.");
        _ = RefreshSettings();
    }

    private void LockButton_Click(object sender, RoutedEventArgs e) => ShowLock();

    private bool locked;

    private void ShowLock()
    {
        locked = true;
        LockOverlay.Visibility = Visibility.Visible;
        // The business content is disabled while the lock is on, so nothing behind the overlay can
        // be reached with the keyboard or the mouse.
        AppContent.IsEnabled = false;
        LockPasswordBox.Focus();
    }

    private void ReleaseLock()
    {
        locked = false;
        LockOverlay.Visibility = Visibility.Collapsed;
        AppContent.IsEnabled = true;
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (AccessControl.Unlock(LockPasswordBox.Password))
        {
            ReleaseLock();
            LockPasswordBox.Clear();
            Say("تم فتح القفل.");
        }
        else Say("كلمة المرور غير صحيحة.");
    }

    private void UnlockRecovery_Click(object sender, RoutedEventArgs e)
    {
        var dialog = InputDialog.Show(this, "استرداد القفل", ("!رمز الاسترداد", ""));
        if (dialog is null) return;
        if (AccessControl.UnlockWithRecovery(dialog.Value("!رمز الاسترداد")))
        {
            ReleaseLock();
            Say("تم فتح القفل برمز الاسترداد.");
        }
        else Say("رمز الاسترداد غير صحيح.");
    }
}
