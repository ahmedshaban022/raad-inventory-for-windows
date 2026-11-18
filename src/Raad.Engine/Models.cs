using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Raad.Engine;

public record Product(string Id, string Name, long Price, long Quantity, long Threshold)
{
    public string DisplayPrice => ExactInput.FormatMoney(Price);
}
public record InvoiceLine(string ProductId, string Name, long Quantity, long UnitPrice)
{
    public long Total => checked(Quantity * UnitPrice);
}
public record InvoiceSnapshot(string Reference, string Customer, DateTimeOffset IssuedAt,
    DateTimeOffset AsOf, string Status, long Total, long Paid, IReadOnlyList<InvoiceLine> Lines)
{
    public long Balance => Status == "Cancelled" ? 0 : checked(Total - Paid);
    public static InvoiceSnapshot Fixture(int count)
    {
        var lines = Enumerable.Range(1, count).Select(i => new InvoiceLine($"fixture-{i}",
            $"LINE{i:D4} — تيل فرامل أمامي Motorcycle R3D {i:D4} / أصلي (يمين)" +
            (i % 13 == 0 ? " قطعة غيار للدراجات النارية ذات اسم عربي طويل لاختبار التفاف النص داخل خلية الجدول والمحافظة على جميع الحروف دون قص أو فقد" : ""),
            i % 3 + 1, 12505 + i * 25)).ToArray();
        return new($"INV-20261118-{count:D20}", "عميل تجريبي — أحمد Test", new(2026, 11, 18, 10, 0, 0, TimeSpan.FromHours(2)),
            new(2026, 11, 18, 12, 30, 0, TimeSpan.FromHours(2)), "Partially paid", lines.Sum(x => x.Total), 5000, lines);
    }
}

public static partial class ExactInput
{
    public const long MaxMoney = 999_999_999_999;
    public const long MaxQuantity = 2_147_483_647;
    public static string Digits(string value) => string.Concat(value.Select(c => c switch
    {
        >= '\u0660' and <= '\u0669' => (char)('0' + c - '\u0660'),
        >= '\u06f0' and <= '\u06f9' => (char)('0' + c - '\u06f0'),
        '\u066b' => '.', _ => c
    }));
    public static long Money(string value)
    {
        var text = Digits(value).Trim();
        if (!MoneyPattern().IsMatch(text) || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var amount) || amount * 100 > MaxMoney)
            throw new ArgumentException("اكتب مبلغًا غير سالب وبحد أقصى رقمين بعد العلامة العشرية.");
        return checked((long)(amount * 100));
    }
    public static long Quantity(string value, long minimum = 0)
    {
        var text = Digits(value).Trim();
        if (!QuantityPattern().IsMatch(text) || !long.TryParse(text, out var amount) || amount < minimum || amount > MaxQuantity)
            throw new ArgumentException("اكتب كمية صحيحة في الحدود المسموحة.");
        return amount;
    }
    public static string Normalize(string value) => Spaces().Replace(value.Normalize(NormalizationForm.FormC).Trim(), " ").ToLowerInvariant();
    public static string FormatMoney(long minor) => (minor / 100m).ToString("N2", CultureInfo.InvariantCulture);
    /// <summary>Ungrouped money text for editors, because the parser rejects thousands separators.</summary>
    public static string FormatPlainMoney(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    [GeneratedRegex(@"^(?:\d+(?:\.\d{1,2})?|\.\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();
    [GeneratedRegex(@"^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
