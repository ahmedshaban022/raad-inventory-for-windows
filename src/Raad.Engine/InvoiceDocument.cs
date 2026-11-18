using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Raad.Engine;

public static class InvoiceDocument
{
    private static readonly object Gate = new();
    private static bool initialized;
    public static void Initialize(string assets)
    {
        lock (Gate)
        {
            if (initialized) return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = false;
            using var font = File.OpenRead(Path.Combine(assets, "Cairo.ttf"));
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);
            initialized = true;
        }
    }
    public static void Save(InvoiceSnapshot invoice, string destination, string assets)
    {
        Initialize(assets);
        if (invoice.Lines.Sum(x => x.Total) != invoice.Total || invoice.Paid < 0 || invoice.Paid > invoice.Total)
            throw new InvalidDataException("لقطة الفاتورة غير متسقة.");
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4); page.Margin(16, Unit.Millimetre);
            page.ContentFromRightToLeft(); page.DefaultTextStyle(x => x.FontFamily("Cairo").FontSize(10));
            page.Header().PaddingBottom(12).BorderBottom(1).BorderColor("#D1DAD3").Row(row =>
            {
                row.ConstantItem(66).Height(42).Image(Path.Combine(assets, "raad-motorcycle.png")).FitArea();
                row.RelativeItem().PaddingHorizontal(10).Column(column =>
                {
                    column.Item().Text("رعد").FontSize(19).Bold().FontColor("#155C3B");
                    column.Item().Text("فاتورة بيع").FontSize(13).Bold();
                    column.Item().Text(invoice.Customer);
                });
                row.ConstantItem(185).Column(column =>
                {
                    column.Item().ContentFromLeftToRight().Text(invoice.Reference).FontSize(7);
                    column.Item().Text($"تاريخ البيع: \u2066{invoice.IssuedAt:yyyy-MM-dd HH:mm}\u2069").FontSize(8);
                    column.Item().Text(invoice.Status switch { "Paid" => "مسددة", "Cancelled" => "ملغاة", "Unpaid" => "غير مسددة", _ => "مسددة جزئيًا" });
                });
            });
            page.Content().PaddingTop(12).Column(column =>
            {
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns => columns.RelativeColumn());
                    table.Header(header => ComposeRow(header.Cell().Background("#EAF2EC"),
                        ["م", "المنتج", "الكمية", "سعر الوحدة", "الإجمالي"], header: true));
                    for (var i = 0; i < invoice.Lines.Count; i++)
                    {
                        var line = invoice.Lines[i];
                        var shade = i % 2 == 0 ? "#FFFFFF" : "#F6F8F5";
                        IContainer row = table.Cell();
                        // The first row already starts below the header. Later rows can move to the next page.
                        if (i > 0) row = row.PreventPageBreak();
                        var cells = new[] { (i + 1).ToString(), line.Name, line.Quantity.ToString(),
                            ExactInput.FormatMoney(line.UnitPrice), ExactInput.FormatMoney(line.Total) };
                        void ComposeItem(IContainer item) => ComposeRow(
                            item.Background(shade).BorderBottom(.4f).BorderColor("#DFE5E0"), cells, header: false);
                        if (i + 1 == invoice.Lines.Count)
                        {
                            row.Column(last =>
                            {
                                ComposeItem(last.Item());
                                ComposeTotals(last.Item(), invoice);
                            });
                        }
                        else ComposeItem(row);
                    }
                    if (invoice.Lines.Count == 0) ComposeTotals(table.Cell(), invoice);
                });
            });
            page.Footer().PaddingTop(10).ContentFromLeftToRight().AlignCenter().Text(text =>
            {
                text.Span("رعد • "); text.CurrentPageNumber(); text.Span(" / "); text.TotalPages();
            });
        })).GeneratePdf(destination);
    }

    private static void ComposeRow(IContainer container, IReadOnlyList<string> cells, bool header)
    {
        container.Row(row =>
        {
            var columns = new[] { row.ConstantItem(30), row.RelativeItem(), row.ConstantItem(40),
                row.ConstantItem(75), row.ConstantItem(80) };
            for (var i = 0; i < columns.Length; i++)
            {
                var cell = columns[i].Padding(header ? 6 : 5);
                if (!header && i >= 2) cell = cell.ContentFromLeftToRight();
                var text = cell.Text(cells[i]);
                if (header) text.Bold();
            }
        });
    }

    private static void ComposeTotals(IContainer container, InvoiceSnapshot invoice)
    {
        container.PaddingTop(12).ShowEntire().Background("#EAF2EC").Padding(12).Column(totals =>
        {
            totals.Item().Text($"إجمالي الفاتورة: {ExactInput.FormatMoney(invoice.Total)} EGP").Bold().FontSize(13);
            totals.Item().Text($"المدفوع: {ExactInput.FormatMoney(invoice.Paid)} EGP");
            totals.Item().Text($"المتبقي: {ExactInput.FormatMoney(invoice.Balance)} EGP");
            totals.Item().Text($"موقف السداد بتاريخ: \u2066{invoice.AsOf:yyyy-MM-dd HH:mm zzz}\u2069").FontSize(8);
        });
    }
}
