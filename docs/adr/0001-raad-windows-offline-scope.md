# Use Raad Inventory as the Windows product baseline

On 18 November 2026, the user confirmed that this product follows the current Raad Inventory workflows for products, stock movements, invoices, customers, outstanding balances, collections, and payment corrections, adapted to a Windows desktop. It targets one operator on one offline Windows 10 or Windows 11 x64 computer with 4 GB RAM; shared stock across networked computers is outside this scope.

The originally supplied PRD described UA Invoices and explicitly excluded several core Raad workflows. It is retained only as `Project Doc/archive/ua-invoices-PRD.reference.md`; the consolidated `Project Doc/windows-clone-PRD.md` now describes Raad and awaits final document approval. Using the existing Raad domain preserves its stock and financial history semantics instead of building the narrower invoicing product; conflicting UA requirements and migration assumptions have been replaced.

The RAM figure is a target-machine constraint, not a measured performance claim. Subsequent decisions are recorded in the other ADRs; all twenty interview decisions are now settled and indexed in `.scratch/raad-windows/spec.md`, with detailed acceptance targets in the consolidated PRD awaiting final approval.

The user subsequently confirmed one open draft, catalog-backed invoice lines, and preservation of full existing invoice references without renumbering imported records. Discounts, invoice notes and terms, workbook import/export, SKU fields, and barcodes are deferred from v1; these UA-only additions are not part of Raad parity.
