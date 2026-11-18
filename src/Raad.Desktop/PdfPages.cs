using System.IO;
using System.Printing;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Raad.Desktop;

public sealed class PdfPages(PdfDocument document) : IDisposable
{
    public int Count => checked((int)document.PageCount);
    public static async Task<PdfPages> Open(string path) => new(await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path))));
    public async Task<byte[]> Render(int index, uint width = 1588)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        using var page = document.GetPage((uint)index);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        { DestinationWidth = width, DestinationHeight = (uint)Math.Round(width * page.Size.Height / page.Size.Width), BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) });
        using var reader = stream.AsStreamForRead(); using var memory = new MemoryStream();
        await reader.CopyToAsync(memory); return memory.ToArray();
    }
    public static BitmapSource Bitmap(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
    }
    public async Task ExportImages(string directory, string prefix, bool jpeg)
    {
        Directory.CreateDirectory(directory);
        for (var i = 0; i < Count; i++)
        {
            BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 94 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Bitmap(await Render(i))));
            var file = Path.Combine(directory, $"{prefix}-page-{i + 1:D3}.{(jpeg ? "jpg" : "png")}");
            using var stream = new FileStream(file, FileMode.CreateNew); encoder.Save(stream);
        }
    }
    public async Task<bool> Print()
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = true, MinPage = 1, MaxPage = (uint)Count };
        if (dialog.PrintQueue is { } defaultQueue)
            dialog.PrintTicket = MergeA4Ticket(defaultQueue, dialog.PrintTicket);
        if (dialog.ShowDialog() != true) return false;
        var first = dialog.PageRangeSelection == PageRangeSelection.UserPages ? Math.Max(1, dialog.PageRange.PageFrom) : 1;
        var last = dialog.PageRangeSelection == PageRangeSelection.UserPages ? Math.Min(Count, dialog.PageRange.PageTo) : Count;
        if (first > last) throw new InvalidOperationException("نطاق الصفحات غير صالح.");
        var queue = dialog.PrintQueue ?? throw new InvalidOperationException("اختر طابعة قبل الطباعة.");
        // Changing printers in the dialog can replace the paper defaults. Keep other job options, including copies.
        var (ticket, paper, printable) = PrepareA4Job(queue, dialog.PrintTicket);
        dialog.PrintTicket = ticket;
        // Stage print images on disk, then paginate lazily; do not hold every page bitmap in memory.
        var spool = Path.Combine(Path.GetTempPath(), "RaadPrint", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(spool);
        try
        {
            var pages = new List<string>();
            for (var i = first - 1; i < last; i++)
            {
                var name = Path.Combine(spool, $"{i}.png");
                await File.WriteAllBytesAsync(name, await Render(i, 2480)); pages.Add(name);
            }
            dialog.PrintDocument(new ImagePaginator(pages, paper, printable), "رعد — فاتورة");
            return true;
        }
        finally { Directory.Delete(spool, true); }
    }

    internal static (PrintTicket Ticket, Size Paper, Rect Printable) PrepareA4Job(PrintQueue queue, PrintTicket requested)
    {
        var ticket = MergeA4Ticket(queue, requested);
        if (ticket.PageMediaSize?.PageMediaSizeName != PageMediaSizeName.ISOA4 || ticket.PageOrientation != PageOrientation.Portrait)
            throw new InvalidOperationException("الطابعة المختارة لا تدعم الطباعة الرأسية على ورق A4.");

        var capabilities = queue.GetPrintCapabilities(ticket);
        const double dipPerMillimetre = 96.0 / 25.4;
        var paper = new Size(ticket.PageMediaSize.Width ?? 210 * dipPerMillimetre,
            ticket.PageMediaSize.Height ?? 297 * dipPerMillimetre);
        var paperBounds = new Rect(paper);
        var area = capabilities.PageImageableArea;
        var printable = area is null ? paperBounds : new Rect(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight);
        printable.Intersect(paperBounds);
        if (printable.IsEmpty || printable.Width <= 0 || printable.Height <= 0)
            throw new InvalidOperationException("تعذر تحديد المساحة القابلة للطباعة على ورق A4.");
        return (ticket, paper, printable);
    }

    private static PrintTicket MergeA4Ticket(PrintQueue queue, PrintTicket requested)
    {
        var a4Portrait = new PrintTicket
        {
            PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4),
            PageOrientation = PageOrientation.Portrait
        };
        return queue.MergeAndValidatePrintTicket(requested, a4Portrait).ValidatedPrintTicket;
    }
    public void Dispose() { /* PdfDocument is a projected WinRT reference; PdfPage/streams are disposed per operation. */ }
}

internal sealed class ImagePaginator(IReadOnlyList<string> pages, Size paper, Rect printable) : DocumentPaginator
{
    public override bool IsPageCountValid => true;
    public override int PageCount => pages.Count;
    public override Size PageSize { get; set; } = paper;
    public override IDocumentPaginatorSource? Source => null;
    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= pages.Count) return DocumentPage.Missing;
        var bitmap = PdfPages.Bitmap(File.ReadAllBytes(pages[pageNumber]));
        var visual = new DrawingVisual();
        var bounds = Rect.Intersect(printable, new Rect(PageSize));
        var scale = Math.Min(bounds.Width / bitmap.PixelWidth, bounds.Height / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * scale; var height = bitmap.PixelHeight * scale;
        var content = new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
        using (var dc = visual.RenderOpen()) dc.DrawImage(bitmap, content);
        return new DocumentPage(visual, PageSize, new Rect(PageSize), content);
    }
}
