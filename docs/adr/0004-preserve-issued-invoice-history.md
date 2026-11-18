# Preserve issued invoices and their payment history

The Windows application will retain issued invoices permanently and correct mistakes through payment reversals and cancellation under the domain rules. This deliberately differs from the current Android action that can permanently delete an issued invoice, delete its payment history, and return its stock without recording a customer refund.

Preserving the history was chosen over exact action parity because deletion can make stock and collection history difficult to explain. Cancellation is allowed only when an invoice has no effective payments; payment reversal corrects a recording mistake, while merchandise returns and actual customer refunds are deferred from v1. Payment information on re-exported documents is defined in ADR-0006.

Import preserves legitimate historical stock movements left after earlier Android invoice deletion and reports the unavailable invoice/payment history during inspection. These known legacy traces are not reconstructed into estimated sales or payments; unrelated corruption or invalid references still fail validation.
