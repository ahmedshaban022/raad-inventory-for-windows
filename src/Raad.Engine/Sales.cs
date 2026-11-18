using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Raad.Engine;

/// <summary>Result of issuing a draft invoice. The reference matches the Android format.</summary>
public record IssuedInvoice(string Id, string Reference, long Total, long Paid);

/// <summary>Full detail of an issued invoice, including its payments and lines.</summary>
public record InvoiceDetail(string Id, string Reference, string Customer, string CustomerId, DateTimeOffset IssuedAt,
    long Total, long Paid, long Balance, string Status, IReadOnlyList<InvoiceLine> Lines, IReadOnlyList<Payment> Payments,
    bool Cancelled, string OriginalCustomer = "", string LinkedCustomerId = "")
{
    public string DisplayDate => IssuedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string DisplayTotal => ExactInput.FormatMoney(Total);
    public string DisplayPaid => ExactInput.FormatMoney(Paid);
    public string DisplayBalance => ExactInput.FormatMoney(Balance);
    public string DisplayStatus => Status switch
    {
        "Paid" => "مسددة", "Unpaid" => "غير مسددة", "Cancelled" => "ملغاة", _ => "مسددة جزئيًا"
    };
}

public sealed partial class Store
{
    private const int DraftPageSize = 200;

    // ------------------------------------------------------------------ draft

    /// <summary>Loads the single resumable draft, or null when no draft exists.</summary>
    public SaleDraft? LoadDraft()
    {
        using var connection = Database.Open(true);
        var rows = Rows(connection, null,
            "SELECT id,COALESCE(customer_id,''),initial_payment FROM invoice_draft WHERE singleton=1",
            r => (r.GetString(0), r.GetString(1), r.GetString(2))).ToList();
        if (rows.Count == 0) return null;
        var row = rows[0];
        var lines = Rows(connection, null,
            "SELECT l.product_id,p.name,l.quantity,l.unit_price FROM draft_lines l JOIN products p ON p.id=l.product_id WHERE l.draft_id=$a ORDER BY l.position",
            r => new DraftLine(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), row.Item1);
        var customer = row.Item2.Length == 0 ? null : ReadCustomer(connection, null, row.Item2);
        return new SaleDraft(row.Item1, customer, row.Item3, lines);
    }

    /// <summary>Saves the draft as raw user input; the values are validated only when the invoice is issued.</summary>
    public void SaveDraft(string? customerId, string initialPayment, IReadOnlyList<DraftLine> lines) =>
        Write((connection, tx) => SaveDraftCore(connection, tx, customerId, initialPayment, lines));

    /// <summary>
    /// Saves the draft and issues it in one transaction. The draft keeps its id, so the operation is
    /// idempotent and a failed save can never lead to a stale draft being issued.
    /// </summary>
    public IssuedInvoice SaveAndIssue(string? customerId, string initialPayment, IReadOnlyList<DraftLine> lines) =>
        Write((connection, tx) =>
        {
            SaveDraftCore(connection, tx, customerId, initialPayment, lines);
            return IssueCore(connection, tx);
        });

    private void SaveDraftCore(SqliteConnection connection, SqliteTransaction tx, string? customerId, string initialPayment, IReadOnlyList<DraftLine> lines)
    {
        {
            if (customerId is not null)
            {
                var exists = Scalar(connection, tx, "SELECT id FROM customers WHERE id=$a AND archived_at IS NULL", customerId);
                if (exists is null) throw new InvalidOperationException("العميل غير متاح.");
            }
            var draftId = (Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string) ?? NewId();
            Execute(connection, tx, """
                INSERT INTO invoice_draft(singleton,id,initial_payment,customer_id) VALUES (1,$a,$b,$c)
                ON CONFLICT(singleton) DO UPDATE SET id=$a, initial_payment=$b, customer_id=$c
                """, draftId, initialPayment ?? "", customerId);
            Execute(connection, tx, "DELETE FROM draft_lines WHERE draft_id=$a", draftId);
            var position = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                if (line.ProductId.Length == 0) throw new InvalidOperationException("سطر بلا صنف.");
                if (!seen.Add(line.ProductId)) continue; // one unique line per product
                var product = Scalar(connection, tx,
                    "SELECT name FROM products WHERE id=$a AND archived_at IS NULL", line.ProductId) as string;
                if (product is null) throw new InvalidOperationException("الصنف غير متاح.");
                Execute(connection, tx,
                    "INSERT INTO draft_lines(draft_id,product_id,quantity,unit_price,position) VALUES ($a,$b,$c,$d,$e)",
                    draftId, line.ProductId, line.Quantity ?? "", line.UnitPrice ?? "", position++);
            }
        }
    }

    /// <summary>Adds (or refreshes) one product line in the draft with a snapshot of the current price.</summary>
    public void AddDraftLine(string productId)
    {
        Write((connection, tx) =>
        {
            var name = Scalar(connection, tx, "SELECT name FROM products WHERE id=$a AND archived_at IS NULL", productId) as string
                ?? throw new InvalidOperationException("الصنف غير متاح.");
            var price = Convert.ToInt64(Scalar(connection, tx, "SELECT price FROM products WHERE id=$a", productId));
            var draftId = (Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string) ?? NewId();
            Execute(connection, tx, """
                INSERT INTO invoice_draft(singleton,id,initial_payment) VALUES (1,$a,'')
                ON CONFLICT(singleton) DO UPDATE SET id=$a
                """, draftId);
            var exists = Scalar(connection, tx, "SELECT 1 FROM draft_lines WHERE draft_id=$a AND product_id=$b", draftId, productId);
            if (exists is not null) return;
            var position = Convert.ToInt64(Scalar(connection, tx,
                "SELECT COALESCE(MAX(position)+1,0) FROM draft_lines WHERE draft_id=$a", draftId));
            Execute(connection, tx,
                "INSERT INTO draft_lines(draft_id,product_id,quantity,unit_price,position) VALUES ($a,$b,$c,$d,$e)",
                draftId, productId, "1", ExactInput.FormatMoney(price), position);
        });
    }

    public void SetDraftLine(string productId, string quantity, string unitPrice) => Write((connection, tx) =>
    {
        var draftId = Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string
            ?? throw new InvalidOperationException("لا توجد مسودة مفتوحة.");
        var affected = Execute(connection, tx,
            "UPDATE draft_lines SET quantity=$b, unit_price=$c WHERE draft_id=$a AND product_id=$d",
            draftId, quantity ?? "", unitPrice ?? "", productId);
        if (affected == 0) throw new InvalidOperationException("السطر غير موجود في المسودة.");
    });

    public void RemoveDraftLine(string productId) => Write((connection, tx) =>
    {
        var draftId = Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string;
        if (draftId is null) return;
        Execute(connection, tx, "DELETE FROM draft_lines WHERE draft_id=$a AND product_id=$b", draftId, productId);
    });

    public void SetDraftCustomer(string? customerId) => Write((connection, tx) =>
    {
        if (customerId is not null &&
            Scalar(connection, tx, "SELECT id FROM customers WHERE id=$a AND archived_at IS NULL", customerId) is null)
            throw new InvalidOperationException("العميل غير متاح.");
        var draftId = (Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string) ?? NewId();
        Execute(connection, tx, """
            INSERT INTO invoice_draft(singleton,id,initial_payment,customer_id) VALUES (1,$a,'',$b)
            ON CONFLICT(singleton) DO UPDATE SET id=$a, customer_id=$b
            """, draftId, customerId);
    });

    public void SetDraftPayment(string initialPayment) => Write((connection, tx) =>
    {
        var draftId = Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string;
        if (draftId is null) return;
        Execute(connection, tx, "UPDATE invoice_draft SET initial_payment=$b WHERE id=$a", draftId, initialPayment ?? "");
    });

    public void DiscardDraft() => Write((connection, tx) =>
    {
        var draftId = Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string;
        if (draftId is null) return;
        Execute(connection, tx, "DELETE FROM draft_lines WHERE draft_id=$a", draftId);
        Execute(connection, tx, "DELETE FROM invoice_draft WHERE id=$a", draftId);
    });

    // ------------------------------------------------------------------ issue

    /// <summary>
    /// Issues the current draft exactly once. The draft id doubles as the issue key, so a repeated
    /// call with the same key returns the invoice that was already written instead of selling twice.
    /// </summary>
    public IssuedInvoice IssueDraft()
    {
        return Write((connection, tx) => IssueCore(connection, tx));
    }

    /// <summary>Issues the current draft inside an open write transaction.</summary>
    private IssuedInvoice IssueCore(SqliteConnection connection, SqliteTransaction tx)
    {
        {
            var draftId = Scalar(connection, tx, "SELECT id FROM invoice_draft WHERE singleton=1") as string
                ?? throw new InvalidOperationException("لا توجد مسودة مفتوحة.");

            var previous = Rows(connection, tx, "SELECT id,reference,total FROM invoices WHERE issue_key=$a",
                r => new IssuedInvoice(r.GetString(0), r.GetString(1), r.GetInt64(2), 0), draftId).SingleOrDefault();
            if (previous is not null)
            {
                var paid = Convert.ToInt64(Scalar(connection, tx,
                    "SELECT COALESCE(SUM(amount),0) FROM effective_payments WHERE invoice_id=$a", previous.Id));
                return previous with { Paid = paid };
            }

            var rawLines = Rows(connection, tx,
                "SELECT product_id,quantity,unit_price FROM draft_lines WHERE draft_id=$a ORDER BY position",
                r => (r.GetString(0), r.GetString(1), r.GetString(2)), draftId);
            if (rawLines.Count == 0) throw new InvalidOperationException("أضف صنفًا واحدًا على الأقل قبل الإصدار.");

            var lines = new List<InvoiceLine>();
            foreach (var (productId, quantityText, priceText) in rawLines)
            {
                var quantity = ExactInput.Quantity(quantityText, 1);
                var unitPrice = ExactInput.Money(priceText);
                var name = Scalar(connection, tx, "SELECT name FROM products WHERE id=$a", productId) as string
                    ?? throw new InvalidOperationException("الصنف غير موجود.");
                if (Scalar(connection, tx, "SELECT archived_at FROM products WHERE id=$a", productId) is not null)
                    throw new InvalidOperationException($"الصنف «{name}» مؤرشف ولا يمكن بيعه.");
                lines.Add(new InvoiceLine(productId, name, quantity, unitPrice));
            }

            long total = 0;
            foreach (var line in lines)
            {
                total = checked(total + line.Total);
                if (total > ExactInput.MaxMoney) throw new InvalidOperationException("إجمالي الفاتورة يتجاوز الحد المسموح.");
            }

            var paymentText = Scalar(connection, tx, "SELECT initial_payment FROM invoice_draft WHERE id=$a", draftId) as string ?? "";
            var initialPayment = paymentText.Trim().Length == 0 ? 0 : ExactInput.Money(paymentText);
            if (initialPayment > total) throw new InvalidOperationException("المبلغ المدفوع أكبر من إجمالي الفاتورة.");

            var customerId = Scalar(connection, tx, "SELECT customer_id FROM invoice_draft WHERE id=$a", draftId) as string;
            if (customerId is null)
            {
                if (initialPayment != total)
                    throw new InvalidOperationException("الفاتورة بدون عميل يجب سدادها بالكامل.");
            }
            else
            {
                var customers = Rows(connection, tx, "SELECT name,name_key FROM customers WHERE id=$a AND archived_at IS NULL",
                    r => (r.GetString(0), r.GetString(1)), customerId).ToList();
                if (customers.Count == 0) throw new InvalidOperationException("العميل غير متاح.");
            }

            // Stock revalidation inside the write transaction.
            foreach (var line in lines)
            {
                var available = Convert.ToInt64(Scalar(connection, tx,
                    "SELECT COALESCE(SUM(quantity),0) FROM stock_movements WHERE product_id=$a", line.ProductId));
                if (available < line.Quantity)
                    throw new InvalidOperationException($"الكمية غير كافية للصنف «{line.Name}». المتاح: {available}.");
            }

            var invoiceId = NewId();
            var issuedAt = DateTimeOffset.UtcNow;
            var issuedText = Stamp(issuedAt);
            var customerName = customerId is null ? null : (Scalar(connection, tx, "SELECT name FROM customers WHERE id=$a", customerId) as string);
            var customerKey = customerId is null ? null : (Scalar(connection, tx, "SELECT name_key FROM customers WHERE id=$a", customerId) as string);
            var reference = $"INV-{issuedAt.ToLocalTime():yyyyMMdd}-{invoiceId.Replace("-", "").ToUpperInvariant()[..20]}";
            Execute(connection, tx, """
                INSERT INTO invoices(id,reference,issue_key,issued_at,customer_id,customer_name,customer_name_key,total)
                VALUES ($a,$b,$c,$d,$e,$f,$g,$h)
                """, invoiceId, reference, draftId, issuedText, customerId, customerName, customerKey, total);

            for (var position = 0; position < lines.Count; position++)
            {
                var line = lines[position];
                Execute(connection, tx, """
                    INSERT INTO invoice_items(invoice_id,product_id,name,quantity,unit_price,position) VALUES ($a,$b,$c,$d,$e,$f)
                    """, invoiceId, line.ProductId, line.Name, line.Quantity, line.UnitPrice, position);
                Execute(connection, tx, """
                    INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at,source_id) VALUES ($a,$b,'sale',$c,$d,$e)
                    """, NewId(), line.ProductId, -line.Quantity, issuedText, invoiceId);
            }

            if (initialPayment > 0)
            {
                Execute(connection, tx, """
                    INSERT INTO payments(id,invoice_id,amount,received_at,created_at) VALUES ($a,$b,$c,$d,$d)
                    """, NewId(), invoiceId, initialPayment, issuedText);
            }

            Execute(connection, tx, "DELETE FROM draft_lines WHERE draft_id=$a", draftId);
            Execute(connection, tx, "DELETE FROM invoice_draft WHERE id=$a", draftId);
            return new IssuedInvoice(invoiceId, reference, total, initialPayment);
        }
    }

    // ------------------------------------------------------------------ invoices

    public IReadOnlyList<InvoiceEntry> Invoices(string query, string? customerId, int offset)
    {
        var normalized = ExactInput.Normalize(query);
        return Read(connection => Rows(connection, null, """
            SELECT i.id,i.reference,COALESCE(c.name, i.customer_name, 'عميل نقدي'),
              i.issued_at, i.total,
              COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0),
              CASE WHEN i.cancelled_at IS NOT NULL THEN 0 ELSE i.total - COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0) END,
              CASE WHEN i.cancelled_at IS NOT NULL THEN 'Cancelled'
                   WHEN COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0)=i.total THEN 'Paid'
                   WHEN COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0)=0 THEN 'Unpaid'
                   ELSE 'Partially paid' END
            FROM invoices i
            LEFT JOIN customer_links l ON l.invoice_id=i.id LEFT JOIN customers c ON c.id=l.customer_id
            WHERE ($b='' OR instr(lower(i.reference),$b)>0 OR instr(COALESCE(c.name_key, i.customer_name_key,''),$b)>0)
              AND ($c='' OR i.customer_id=$c OR l.customer_id=$c)
            ORDER BY i.issued_at DESC, i.id DESC LIMIT $d OFFSET $e
            """, r => new InvoiceEntry(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7)),
            normalized, normalized, customerId ?? "", PageSize, offset));
    }

    public InvoiceDetail? InvoiceDetailById(string invoiceId) => Read(connection =>
    {
        // One consistent transaction so the header, the lines and the payments belong to the same state.
        using var transaction = connection.BeginTransaction();
        var header = Rows(connection, transaction, """
            SELECT i.id,i.reference,
              COALESCE(c.name, i.customer_name, 'عميل نقدي'),
              COALESCE(i.customer_id,l.customer_id,''),
              COALESCE(i.customer_name,'عميل نقدي'),
              COALESCE(l.customer_id,''),
              i.issued_at,i.total,
              COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0),
              CASE WHEN i.cancelled_at IS NOT NULL THEN 0 ELSE i.total - COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0) END,
              CASE WHEN i.cancelled_at IS NOT NULL THEN 'Cancelled'
                   WHEN COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0)=i.total THEN 'Paid'
                   WHEN COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0)=0 THEN 'Unpaid'
                   ELSE 'Partially paid' END,
              i.cancelled_at IS NOT NULL
            FROM invoices i LEFT JOIN customer_links l ON l.invoice_id=i.id LEFT JOIN customers c ON c.id=l.customer_id WHERE i.id=$a
            """, r => new
            {
                Id = r.GetString(0), Reference = r.GetString(1), Customer = r.GetString(2), CustomerId = r.GetString(3),
                OriginalCustomer = r.GetString(4), LinkedCustomerId = r.GetString(5),
                IssuedAt = DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), Total = r.GetInt64(7),
                Paid = r.GetInt64(8), Balance = r.GetInt64(9), Status = r.GetString(10), Cancelled = r.GetBoolean(11)
            }, invoiceId).SingleOrDefault();
        if (header is null) { transaction.Commit(); return null; }
        var lines = Rows(connection, transaction,
            "SELECT product_id,name,quantity,unit_price FROM invoice_items WHERE invoice_id=$a ORDER BY position",
            r => new InvoiceLine(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)), invoiceId);
        var payments = Rows(connection, transaction, """
            SELECT p.id,p.amount,p.received_at,
              (SELECT r.reason FROM payment_reversals r WHERE r.payment_id=p.id),
              (SELECT p2.id FROM payments p2 WHERE p2.replaces_payment_id=p.id),
              p.replaces_payment_id
            FROM payments p WHERE p.invoice_id=$a ORDER BY p.received_at DESC, p.id DESC
            """, r => new Payment(r.GetString(0), r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)), invoiceId);
        transaction.Commit();
        return new InvoiceDetail(header.Id, header.Reference, header.Customer, header.CustomerId, header.IssuedAt,
            header.Total, header.Paid, header.Balance, header.Status, lines, payments, header.Cancelled,
            header.OriginalCustomer, header.LinkedCustomerId);
    });

    /// <summary>Snapshot of an issued invoice with the payment position as of now, used for PDF and printing.</summary>
    public InvoiceSnapshot? Snapshot(string invoiceId)
    {
        var detail = InvoiceDetailById(invoiceId);
        if (detail is null) return null;
        return new InvoiceSnapshot(detail.Reference, detail.Customer, detail.IssuedAt, DateTimeOffset.UtcNow,
            detail.Status, detail.Total, detail.Paid, detail.Lines);
    }

    // ------------------------------------------------------------------ payments

    public void AddPayment(string invoiceId, string amountText)
    {
        Write((connection, tx) =>
        {
            var detail = InvoiceRow(connection, tx, invoiceId);
            if (detail.Cancelled) throw new InvalidOperationException("لا يمكن تحصيل دفعة على فاتورة ملغاة.");
            var amount = ExactInput.Money(amountText);
            if (amount <= 0) throw new InvalidOperationException("اكتب مبلغًا أكبر من صفر.");
            if (detail.Paid + amount > detail.Total)
                throw new InvalidOperationException("المبلغ المدفوع يتجاوز إجمالي الفاتورة.");
            var received = Stamp(DateTimeOffset.UtcNow);
            Execute(connection, tx, """
                INSERT INTO payments(id,invoice_id,amount,received_at,created_at) VALUES ($a,$b,$c,$d,$d)
                """, NewId(), invoiceId, amount, received);
        });
    }

    /// <summary>
    /// Reverses an effective payment with a reason. When a replacement amount is given the corrected
    /// payment keeps the original received date and points back at the reversed payment.
    /// </summary>
    public void ReversePayment(string paymentId, string reason, string? replacementAmountText)
    {
        Write((connection, tx) =>
        {
            reason = Required(reason, "اكتب سبب عكس الدفعة.");
            var payments = Rows(connection, tx, "SELECT id,invoice_id,amount,received_at FROM payments WHERE id=$a",
                r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)), paymentId).ToList();
            if (payments.Count == 0) throw new InvalidOperationException("الدفعة غير موجودة.");
            var payment = payments[0];
            if (Scalar(connection, tx, "SELECT id FROM payment_reversals WHERE payment_id=$a", paymentId) is not null)
                throw new InvalidOperationException("تم عكس هذه الدفعة مسبقًا.");
            var detail = InvoiceRow(connection, tx, payment.Item2);
            if (detail.Cancelled) throw new InvalidOperationException("لا يمكن عكس دفعة على فاتورة ملغاة.");
            Execute(connection, tx, """
                INSERT INTO payment_reversals(id,payment_id,reason,created_at) VALUES ($a,$b,$c,$d)
                """, NewId(), paymentId, reason, Stamp(DateTimeOffset.UtcNow));
            if (!string.IsNullOrWhiteSpace(replacementAmountText))
            {
                var replacement = ExactInput.Money(replacementAmountText);
                if (replacement < 0) throw new InvalidOperationException("اكتب مبلغًا غير سالب.");
                if (detail.Paid - payment.Item3 + replacement > detail.Total)
                    throw new InvalidOperationException("المبلغ التصحيحي يتجاوز إجمالي الفاتورة.");
                if (replacement > 0)
                {
                    Execute(connection, tx, """
                        INSERT INTO payments(id,invoice_id,amount,received_at,created_at,replaces_payment_id)
                        VALUES ($a,$b,$c,$d,$e,$f)
                        """, NewId(), payment.Item2, replacement, payment.Item4, Stamp(DateTimeOffset.UtcNow), paymentId);
                }
            }
        });
    }

    /// <summary>Cancels an invoice that has no effective payment and restores its stock exactly once.</summary>
    public void CancelInvoice(string invoiceId, string reason)
    {
        Write((connection, tx) =>
        {
            reason = Required(reason, "اكتب سبب الإلغاء.");
            var detail = InvoiceRow(connection, tx, invoiceId);
            if (detail.Cancelled) throw new InvalidOperationException("الفاتورة ملغاة مسبقًا.");
            if (detail.Paid > 0) throw new InvalidOperationException("لا يمكن إلغاء فاتورة عليها دفعة فعالة. اعكس الدفعة أولًا.");
            var restored = Convert.ToInt64(Scalar(connection, tx,
                "SELECT COUNT(*) FROM stock_movements WHERE source_id=$a AND kind='cancellation'", invoiceId));
            if (restored > 0) throw new InvalidOperationException("تم رد المخزون لهذه الفاتورة مسبقًا.");
            var items = Rows(connection, tx, "SELECT product_id,quantity FROM invoice_items WHERE invoice_id=$a",
                r => (r.GetString(0), r.GetInt64(1)), invoiceId);
            foreach (var (productId, quantity) in items)
            {
                // The restored quantity must stay inside the allowed stock bounds.
                var current = Convert.ToInt64(Scalar(connection, tx,
                    "SELECT COALESCE(SUM(quantity),0) FROM stock_movements WHERE product_id=$a", productId));
                var afterRestore = checked(current + quantity);
                if (afterRestore is < 0 or > ExactInput.MaxQuantity)
                    throw new InvalidOperationException("رد المخزون يتجاوز حدود الكمية المسموحة.");
                Execute(connection, tx, """
                    INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at,source_id,reason)
                    VALUES ($a,$b,'cancellation',$c,$d,$e,$f)
                    """, NewId(), productId, quantity, Stamp(DateTimeOffset.UtcNow), invoiceId, reason);
            }
            Execute(connection, tx, "UPDATE invoices SET cancelled_at=$b WHERE id=$a", invoiceId, Stamp(DateTimeOffset.UtcNow));
        });
    }

    /// <summary>Links an invoice that had no identified customer to a known, active customer.</summary>
    public void LinkCustomer(string invoiceId, string customerId)
    {
        Write((connection, tx) =>
        {
            if (Scalar(connection, tx, "SELECT id FROM customers WHERE id=$a AND archived_at IS NULL", customerId) is null)
                throw new InvalidOperationException("العميل غير موجود أو مؤرشف.");
            if (Scalar(connection, tx, "SELECT 1 FROM customer_links WHERE invoice_id=$a", invoiceId) is not null)
                throw new InvalidOperationException("الفاتورة مرتبطة بعميل بالفعل.");
            var invoice = Rows(connection, tx,
                "SELECT customer_id,total,cancelled_at FROM invoices WHERE id=$a",
                r => (r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1), !r.IsDBNull(2)), invoiceId).SingleOrDefault();
            if (invoice == default) throw new InvalidOperationException("الفاتورة غير موجودة.");
            if (invoice.Item1 is not null) throw new InvalidOperationException("الفاتورة لها عميل مسجل بالفعل.");
            if (invoice.Item3) throw new InvalidOperationException("لا يمكن ربط فاتورة ملغاة.");
            var paid = Convert.ToInt64(Scalar(connection, tx,
                "SELECT COALESCE(SUM(amount),0) FROM effective_payments WHERE invoice_id=$a", invoiceId));
            if (invoice.Item2 - paid <= 0) throw new InvalidOperationException("لا يوجد رصيد مستحق للربط.");
            Execute(connection, tx, "INSERT INTO customer_links(invoice_id,customer_id,created_at) VALUES ($a,$b,$c)",
                invoiceId, customerId, Stamp(DateTimeOffset.UtcNow));
        });
    }

    // ------------------------------------------------------------------ totals

    /// <summary>Today view: sales, collections, outstanding debt and unidentified balances.</summary>
    public BusinessTotals Totals()
    {
        var today = DateTime.Now.Date;
        return Read(connection =>
        {
            long Money(string sql, params object?[] args) => Convert.ToInt64(Scalar(connection, null, sql, args));

            var sales = Money("SELECT COALESCE(SUM(total),0) FROM invoices WHERE cancelled_at IS NULL AND date(issued_at,'localtime')=date('now','localtime')");
            var collections = Money("""
                SELECT COALESCE(SUM(amount),0) FROM effective_payments
                WHERE date(received_at,'localtime')=date('now','localtime')
                  AND invoice_id IN (SELECT id FROM invoices WHERE cancelled_at IS NULL)
                """);
            var outstanding = Money("""
                SELECT COALESCE(SUM(balance),0) FROM invoice_balances WHERE balance>0
                """);
            var unidentified = Money("""
                SELECT COALESCE(SUM(balance),0) FROM invoice_balances
                WHERE balance>0 AND COALESCE(linked_customer_id, customer_id) IS NULL
                """);
            var open = Money("SELECT COUNT(*) FROM invoice_balances WHERE balance>0");
            return new BusinessTotals(sales, collections, outstanding, open, unidentified);
        });
    }

    /// <summary>Reference and identity of every issued invoice, used by the export list.</summary>
    public IReadOnlyList<(string Id, string Reference)> AllInvoiceReferences(int limit = 2000) => Read(connection =>
        Rows(connection, null, "SELECT id,reference FROM invoices ORDER BY issued_at DESC, id DESC LIMIT $a",
            r => (r.GetString(0), r.GetString(1)), limit));

    private (string Id, long Total, long Paid, bool Cancelled) InvoiceRow(SqliteConnection connection, SqliteTransaction tx, string invoiceId)
    {
        var rows = Rows(connection, tx, """
            SELECT i.id,i.total,
              COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id=i.id),0),
              i.cancelled_at IS NOT NULL
            FROM invoices i WHERE i.id=$a
            """, r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetBoolean(3)), invoiceId).ToList();
        if (rows.Count == 0) throw new InvalidOperationException("الفاتورة غير موجودة.");
        return rows[0];
    }
}
