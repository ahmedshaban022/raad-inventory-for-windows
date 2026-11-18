# Re-export original sale facts with explicitly current payment status

An invoice export preserves the issued sale's items, prices, reference, and original sale date, while reporting effective payments and outstanding balance at export time under a visible as-of label. For an invoice worth EGP 1,000, an initial EGP 200 payment and a later EGP 300 payment make the re-export show paid EGP 500 and outstanding EGP 500.

This preserves Raad's payment-aware output rather than freezing the payment summary at issuance, while making the distinction between historical sale facts and current collection status visible. A re-export does not alter the sale or replace its payment history.
