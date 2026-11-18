using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Raad.Engine;

namespace Raad.Desktop;

public partial class MainWindow : Window
{
    private const double SideBySideWidth = 780;
    private readonly string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaadInventory", "Feasibility");
    private readonly InventoryDatabase? database;
    private PdfPages? pdf;
    private string? pdfPath;
    private int pageIndex;
    private long searchRevision;
    private bool busy;
    public MainWindow(string? databasePath)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
        Directory.CreateDirectory(workspace);
        var demo = databasePath ?? Path.Combine(App.Assets, "demo.db");
        if (File.Exists(demo)) database = new(demo);
        UpdatePageState();
        ContentRendered += async (_, _) =>
        {
            await SearchProducts();
            App.WriteStartupEvidence(Path.Combine(workspace, "startup.json"));
        };
        Closing += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            Announce(OperationStatus, "انتظر اكتمال العملية قبل الإغلاق.");
        };
    }

    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var stacked = e.NewSize.Width < SideBySideWidth;
        Grid.SetColumn(InvoicePanel, stacked ? 0 : 2);
        Grid.SetRow(InvoicePanel, stacked ? 2 : 0);
        InvoiceColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PanelColumnGap.Width = new GridLength(stacked ? 0 : 16);
        PanelRowGap.Height = new GridLength(stacked ? 16 : 0);
    }

    private static void Announce(TextBlock target, string message)
    {
        if (target.Text == message) return;
        target.Text = message;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(target)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdatePageState()
    {
        var loaded = pdf is not null;
        PrintButton.IsEnabled = loaded && !busy;
        ExportButton.IsEnabled = loaded && !busy;
        ZoomButton.IsEnabled = loaded && !busy;
        PreviousButton.IsEnabled = loaded && !busy && pageIndex > 0;
        NextButton.IsEnabled = loaded && !busy && pdf is not null && pageIndex + 1 < pdf.Count;
        GenerateButton.IsEnabled = !busy;
        InspectButton.IsEnabled = !busy;
        LineCount.IsEnabled = !busy;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (PrintButton.IsEnabled) PrintInvoice(this, new RoutedEventArgs());
            else if (!busy) Announce(OperationStatus, "جهّز فاتورة أولًا لطباعتها.");
            e.Handled = true;
        }
    }
    private async Task Work(Func<Task> action)
    {
        if (busy) return;
        var priorFocus = Keyboard.FocusedElement as Control;
        busy = true;
        UpdatePageState();
        try { await action(); }
        catch (Exception error) { Announce(OperationStatus, "تعذر إتمام العملية: " + error.Message); }
        finally
        {
            busy = false;
            UpdatePageState();
            if (IsActive && Keyboard.FocusedElement is null or Window && priorFocus is { IsEnabled: true })
                priorFocus.Focus();
        }
    }
    private async void SearchChanged(object sender, TextChangedEventArgs e) => await SearchProducts();
    private async Task SearchProducts()
    {
        var revision = ++searchRevision; var query = SearchBox?.Text ?? "";
        await Task.Delay(150);
        if (revision != searchRevision) return;
        if (database is null)
        {
            Announce(SearchStatus, "لا توجد قاعدة بيانات تجريبية في هذا التثبيت.");
            EmptyState.Text = "لا توجد أصناف للعرض.";
            EmptyState.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            Announce(SearchStatus, "جارٍ البحث…");
            var rows = await Task.Run(() => database.Search(query));
            if (revision != searchRevision) return;
            ProductsGrid.ItemsSource = rows;
            EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (rows.Count == 0)
                EmptyState.Text = string.IsNullOrWhiteSpace(query)
                    ? "لا توجد أصناف في قاعدة البيانات التجريبية."
                    : "لا توجد أصناف مطابقة للبحث.";
            Announce(SearchStatus, rows.Count == 0 ? "لا توجد نتائج." : $"عدد الأصناف المعروضة: {rows.Count}");
        }
        catch (Exception)
        {
            if (revision != searchRevision) return;
            ProductsGrid.ItemsSource = null;
            Announce(SearchStatus, "تعذر تحميل الأصناف. عدّل البحث وحاول مرة أخرى.");
            EmptyState.Text = "تعذر تحميل الأصناف. عدّل البحث وحاول مرة أخرى.";
            EmptyState.Visibility = Visibility.Visible;
        }
    }
    private async void GenerateInvoice(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        var count = int.Parse(((ComboBoxItem)LineCount.SelectedItem).Content.ToString()!);
        Announce(OperationStatus, "جارٍ تجهيز الفاتورة…");
        var output = Path.Combine(workspace, $"invoice-{count}-{Guid.NewGuid():N}.pdf");
        await Task.Run(() => InvoiceDocument.Save(InvoiceSnapshot.Fixture(count), output, App.Assets));
        var replacement = await PdfPages.Open(output);
        BitmapSource firstPage;
        try { firstPage = await RenderPreview(replacement, 0); }
        catch { replacement.Dispose(); throw; }

        pdf?.Dispose();
        pdf = replacement;
        pdfPath = output;
        SetZoom(false);
        ShowPage(firstPage, 0, replacement.Count);
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        Announce(OperationStatus, $"تم تجهيز فاتورة اختبار من {count} بندًا في {replacement.Count} صفحة.");
    });

    private static async Task<BitmapSource> RenderPreview(PdfPages document, int index) =>
        PdfPages.Bitmap(await document.Render(index, 1000));

    private void ShowPage(BitmapSource image, int index, int count)
    {
        pageIndex = index;
        PreviewImage.Source = image;
        PreviewScroller.ScrollToTop();
        PreviewScroller.ScrollToLeftEnd();
        Announce(PageLabel, $"{index + 1} / {count}");
    }

    private void ToggleZoom(object sender, RoutedEventArgs e) =>
        SetZoom(PreviewImage.Stretch == Stretch.Uniform);

    private void SetZoom(bool zoomed)
    {
        var scrollMode = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PreviewImage.Stretch = zoomed ? Stretch.None : Stretch.Uniform;
        PreviewScroller.HorizontalScrollBarVisibility = scrollMode;
        PreviewScroller.VerticalScrollBarVisibility = scrollMode;
        PreviewScroller.Focusable = zoomed;
        ZoomButton.Content = zoomed ? "ملاءمة الصفحة" : "تكبير";
        PreviewScroller.ScrollToTop();
        PreviewScroller.ScrollToLeftEnd();
    }

    private Task MovePage(int offset) => Work(async () =>
    {
        var index = pageIndex + offset;
        if (pdf is not { } current || index < 0 || index >= current.Count) return;
        ShowPage(await RenderPreview(current, index), index, current.Count);
    });

    private async void PreviousPage(object sender, RoutedEventArgs e) => await MovePage(-1);
    private async void NextPage(object sender, RoutedEventArgs e) => await MovePage(1);
    private async void PrintInvoice(object sender, RoutedEventArgs e) => await Work(async () =>
    { if (pdf is not null) Announce(OperationStatus, await pdf.Print() ? "تم تسليم المهمة إلى الطابعة." : "أُلغيت الطباعة."); });
    private async void ExportInvoice(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        if (pdf is null || pdfPath is null) return;
        var dialog = new SaveFileDialog { FileName = "Raad-test-invoice.pdf", Filter = "PDF|*.pdf|PNG pages|*.png|JPG pages|*.jpg", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FilterIndex == 1) File.Copy(pdfPath, dialog.FileName, true);
        else
        {
            var outputDirectory = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "-" + Guid.NewGuid().ToString("N")[..8]);
            await pdf.ExportImages(outputDirectory, "Raad-test", dialog.FilterIndex == 3);
        }
        Announce(OperationStatus, "تم حفظ المستند.");
    });
    private async void InspectTransfer(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "Raad transfer|*.raadbackup;*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var inspection = await Task.Run(() => TransferService.Inspect(dialog.FileName, Path.Combine(workspace, "staged")));
        Announce(OperationStatus, $"اكتمل فحص الملف: {inspection.Summary["products"]} صنف، {inspection.Summary["invoices"]} فاتورة. لم تُستورد البيانات.");
    });
}
