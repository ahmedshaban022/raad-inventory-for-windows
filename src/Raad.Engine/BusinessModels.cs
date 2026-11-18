using System.Globalization;

namespace Raad.Engine;

public record Customer(string Id, string Reference, string Name, string Phone, long Balance)
{
    public string DisplayBalance => ExactInput.FormatMoney(Balance);
}

public record InvoiceEntry(string Id, string Reference, string Customer, DateTimeOffset Date, long Total, long Paid, long Balance, string Status)
{
    public string DisplayDate => Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string DisplayTotal => ExactInput.FormatMoney(Total);
    public string DisplayPaid => ExactInput.FormatMoney(Paid);
    public string DisplayBalance => ExactInput.FormatMoney(Balance);
    public string DisplayStatus => Status switch { "Paid" => "مسددة", "Unpaid" => "غير مسددة", "Cancelled" => "ملغاة", _ => "مسددة جزئيًا" };
}

public record Payment(string Id, long Amount, DateTimeOffset Date, string? ReversalReason, string? Replaces, string? ReplacedBy = null)
{
    public string DisplayAmount => ExactInput.FormatMoney(Amount);
    public string DisplayDate => Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string DisplayStatus => ReversalReason is not null ? "معكوس — " + ReversalReason : Replaces is null ? "فعّال" : "قيد بديل";
}

public record Movement(string Kind, long Change, DateTimeOffset Date, string Details)
{
    public string DisplayDate => Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string DisplayKind => Kind switch { "opening" => "افتتاحي", "addition" => "إضافة", "correction" => "تصحيح", "sale" => "بيع", _ => "إلغاء" };
}

public record DraftLine(string ProductId, string Name, string Quantity, string UnitPrice);
public record SaleDraft(string Id, Customer? Customer, string InitialPayment, IReadOnlyList<DraftLine> Lines);
public record BusinessTotals(long Sales, long Collections, long Outstanding, long OpenInvoices, long Unidentified);
