# Raad Inventory

Raad Inventory tracks stock, sales, and outstanding customer balances for a single operator.

## Language

### Sales and payments

**Invoice**:
An issued record of a sale with its original items, prices, and issue date. A cancelled invoice remains part of sales history.
_Avoid_: Invoice draft, payment receipt

**Invoice draft**:
An editable working copy of a prospective sale. Stock and balances change only when the invoice is issued.
_Avoid_: Issued invoice

**Invoice reference**:
The stable, customer-facing identifier for an issued invoice.

**Invoice cancellation**:
A recorded invalidation of an issued sale with no effective payments that preserves the invoice in its history and reverses its stock effect.
_Avoid_: Invoice deletion, customer refund

**Walk-in sale**:
A sale issued without a named customer. Its recorded balance is zero at issuance.

**Initial payment**:
A payment recorded as part of issuing an invoice.

**Outstanding balance**:
The amount still owed on an active invoice. A cancelled invoice has no outstanding balance.
_Avoid_: Stock balance

**Unidentified balance**:
An outstanding balance revealed by correcting a payment on a walk-in invoice whose buyer is still unknown.

**Customer link**:
A recorded association of an unidentified balance with the buyer once they are identified. The original invoice remains a walk-in sale.

**Effective payment**:
A recorded payment that has yet to be reversed and counts toward the invoice's paid amount.

**Payment reversal**:
An entry that corrects a mistakenly recorded payment while keeping the original payment and the reason for the reversal visible in its history.
_Avoid_: Payment deletion

**Payment correction**:
A reversal of a mistaken payment entry, optionally with a replacement for the amount actually received. The replacement belongs to the original collection day.

### Inventory

**Stock movement**:
A signed change to a product's available quantity from opening stock, an addition, a correction, a sale, or an invoice cancellation.

**Stock correction**:
An adjustment to match a physical count, with a reason for the discrepancy.

### Recovery

**Dataset**:
The operator's inventory and sales records, together with their currency.

**Backup snapshot**:
A recoverable copy of a dataset at a point in time.

**Restore**:
Replacement of the active dataset with the records from a selected backup snapshot.
_Avoid_: Merge

**Android transfer**:
A one-time replacement of the Windows dataset with converted Raad records exported from Android.
_Avoid_: Synchronization

**Transfer handover**:
The point at which the operator stops recording new business on Android and continues on Windows after a verified transfer.
_Avoid_: Synchronization

**Legacy deletion trace**:
A retained stock movement whose original invoice was permanently deleted in an earlier Raad installation.
_Avoid_: Reconstructed invoice

**Safety backup**:
A backup snapshot of the current dataset retained before replacing or upgrading it, so the prior state can be recovered.

### Access

**Activation**:
The initial authorization to use an installation of Raad Inventory.
_Avoid_: Privacy lock, user login

**Privacy lock**:
An optional restriction on opening the operator's Raad workspace until they unlock it.
_Avoid_: Activation, user account

**Privacy recovery code**:
A code retained by the operator to regain access to the privacy-locked workspace after forgetting its password.
_Avoid_: Activation password, backup snapshot
