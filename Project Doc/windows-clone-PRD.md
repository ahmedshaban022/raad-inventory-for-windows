# UA Invoices for Windows

**Product requirements document · v1 · 18 November 2026**

| Item | Decision |
| --- | --- |
| Product | Standalone Windows desktop clone of UA Invoices for Android |
| Project | New public GitHub repository, separate from the Android app and the original Windows app |
| Target | Windows 11 x64 and Windows 10 22H2 x64 |
| Interface | Arabic, right to left, with keyboard and mouse support |
| Data | Per-user local SQLite. Core workflows operate offline. |
| Migration | One-time replacement import from Android or original Windows backups, with safety backup and recovery |
| Distribution | Per-user Windows setup `.exe` from the new repository’s GitHub Releases page |

## 1. Product goal and release scope

Build an invoicing application for a small-business operator who enters customer details per sale and draws from a large product catalog. The operator can create and recover drafts, issue each invoice once, find it later, and deliver a professional Arabic A4 document as a PDF, a set of images, or a printout.

Use the Android app as the baseline for invoice behavior, draft recovery, products, settings, and document design. Use the original Windows project as a reference for desktop interaction, Windows integration, and legacy data formats. Preserve the invoice’s appearance while adapting the editor to a wide screen and keyboard. The original Windows application and the clone can coexist on one computer with separate data directories and installers. The Android verification report records that in-app printing was removed from the Android release. This Windows release has its own native printer requirement and acceptance test.

**Included in v1.** Invoice editor, recoverable drafts, issued history, duplication, Trash, product catalog, workbook import/export, seller and document preferences, A4 preview/PDF/PNG/JPG/print, local backup and restore, migration from both existing applications, and clean installation and in-place upgrades.

**Outside v1.** Accounts, cloud sync, multi-user access, database merge, customer profiles, inventory quantities, payments, accounting, tax authority and e-invoicing integrations, bulk invoice deletion, and in-app update checks or downloads.

**Release success.** A user installs the downloaded setup while offline, imports either supported source, completes the invoice workflow while disconnected, and installs a later version over the first with the same local records and settings. A physical A4 printout passes the document checks in Section 7.

## 2. Windows interface and navigation

| Screen | Required behavior |
| --- | --- |
| **فاتورة جديدة** — New Invoice | Enter customer and date, add catalog and manual lines, set discount, add notes and terms, save, preview, and issue. |
| **سجل الفواتير** — History | Switch between issued invoices and drafts. Search and filter, resume a draft, open an issued document, duplicate an invoice, or move a record to Trash. |
| **الأصناف** — Products | Search, add, edit, and soft-delete products. Import a workbook, export the catalog, or save a template. |
| **الإعدادات** — Settings | Manage company details, brand, currency, date format, reusable terms, and backups. Show the installed version and a link to GitHub Releases. |
| **المحذوفات** — Trash | Separate invoices, drafts, and products. Restore any of them. Permanently delete invoices or drafts after a separate confirmation. |
| **معاينة الفاتورة** — Preview | Fit to page or width, zoom, and navigate pages. Save PDF or images, print, and reveal saved files in Explorer. Offer a return to editing from a draft preview. |

- Use right-side navigation, a wide line-item grid, and inline product suggestions. Place customer details above the grid. Keep the total and primary actions accessible when the grid grows.
- Start with a `1280 × 840` logical window and support a `940 × 620` logical minimum where the work area allows. On smaller effective work areas, compact the navigation and allow scrolling so save, issue, and validation actions stay usable at high DPI.
- Use bundled Cairo, the supplied blue SVG, `#0b68e6` as the default accent, `#faf7f2` as the workspace background, and white surfaces. Use concise Arabic labels and errors. Isolate invoice numbers, SKUs, dates, phone numbers, and monetary values from surrounding RTL text where direction matters.
- Support visible keyboard focus, logical Tab order, and modal focus return. Suggestions support Arrow keys, Enter, Escape, and Tab. Selecting a product fills its name and price and moves focus to quantity. `Ctrl+S` saves the active draft, `Ctrl+N` starts a new draft after handling pending edits, and `Ctrl+P` prints from preview. Issue remains an explicit action.
- Label inputs and announce save state, validation failures, and dialog titles to Windows Narrator. Show loading, empty, error, and retry states. Show progress or a busy state for long operations and guard against double submission.

## 3. Invoice, catalog, and settings requirements

### INV-01 · Create and calculate an invoice

Start with today’s local calendar date and one empty line. Collect customer name, optional phone, ordered items, one optional discount, optional notes, and zero or more reusable terms. A line stores a product reference when applicable, name, quantity, and unit price. Free-text items and catalog selections can coexist. Editing a line’s price affects that line only. Removing the last row leaves an editable placeholder.

Issue requires a valid calendar date, a nonblank customer name, and at least one named line with positive quantity and nonnegative price. Ignore wholly untouched placeholder rows and identify partially completed rows by field. Cap newly entered customer and item names at 300 characters. Preserve longer historical values in imported issued records. Accept international phone formats and keep the displayed phone text as entered.

Use one Rust calculation path for the editor, draft preview, and issue. Store money as signed 64-bit integer minor units, quantity as integer thousandths, and percentage as integer hundredths of one percent. Use wider intermediates and reject overflow. Transfer scaled integers and record IDs across the webview boundary as decimal strings. Preserve the Android parser’s behavior for Western, Arabic-Indic, and Persian digits, decimal separator normalization, half-up scaling for money and quantity, and percentage truncation to hundredths. A single comma in money input with no dot is a decimal mark. Quantity and percentage treat commas as grouping marks and accept a dot or Arabic decimal mark for fractions. Validate a percentage against 0–100 before truncation.

| Calculated field | Rule |
| --- | --- |
| Line total | Round `quantity_thousandths × unit_price_minor ÷ 1000` half-up to minor units. |
| Total quantity | Sum line quantities. |
| Subtotal | Sum rounded line totals. |
| Discount | Fixed amount between zero and subtotal, or round `subtotal_minor × percent_hundredths ÷ 10000` half-up. |
| Grand total | Subtotal minus discount. |

Show the line totals, total quantity, subtotal, discount when applied, and grand total. Keep invalid input visible with a field error and block issue. For example, `0.5 × 0.05` yields `0.03`, and `10%` off `5.05` yields a `0.51` discount and a `4.54` grand total.

### INV-02 · Save, recover, and issue

- Give each draft a stable key and retain raw editable input, selected term text, currency, and line order. Save incomplete drafts. Debounce autosave at about 900 ms. Flush pending work on navigation and normal close. A failed flush offers retry or explicit discard while preserving the editor state.
- Show saving, saved, and error status. After an interrupted session offer to resume the last durably saved draft or leave it in History and start a new one. Serialize saves by draft and prevent stale saves from overwriting a different draft or reviving an issued one.
- Permit document output for a draft that meets document validation. Put **مسودة** on every draft page. Preview, export, and print consume no permanent number.
- Validate and recalculate on issue. Persist the invoice and ordered line snapshots, allocate a number, and consume the draft in a single transaction. Begin at `INV-000001`, keep one sequence without a yearly reset, and grow past six digits. A duplicate request for the same draft returns the issued record or reports that it was purged. Failure rolls back issuance and leaves the draft recoverable.
- Issued records capture customer, date, line names and prices, totals, discount, notes, selected terms, and seller name, phone, address, logo, color, currency code/exponent, and date format. Use those snapshots in History and every re-export after settings or products change. Open issued records read-only.

### INV-03 · Find, duplicate, trash, and restore

Sort issued records by invoice date descending and stable ID descending. Search by customer name or invoice number and filter by inclusive date range. Show an Arabic error if the range is inverted. Fetch 25 records at a time. Order drafts by most recently updated and allow an incomplete draft to open directly in the editor.

Duplicating an issued invoice creates an editable draft dated today. Copy customer, phone, item snapshots and prices, currency, discount, notes, and terms. Apply current seller identity and presentation settings when the duplicate is issued. Confirm moving an invoice or draft to Trash. Restore with its original date, status, and number. Require a separate explicit confirmation to permanently delete from Trash. Purging never decreases the number counter or releases an issued number for reuse.

### CAT-01 · Manage and search products

Store the displayed name, nonnegative base price, optional SKU, timestamps, and soft-delete state. Preserve original Arabic display text. Normalize common Arabic letter variants, diacritics, tatweel, and digit forms for search. Use SQLite FTS5 trigram for terms of at least three characters and a bounded normalized search for shorter terms. Limit inline invoice suggestions to ten results showing name, price, and optional SKU. Debounce by about 150 ms, disregard stale replies, and page or virtualize catalog results. Deleted products disappear from active results and can be restored from Trash.

### CAT-02 · Import and export workbooks

1. Accept `.xlsx`, `.xls`, and `.ods`. Let the operator select the worksheet and map required name and price columns and an optional SKU column using that sheet’s headers. Read workbook values without executing formulas or macros.
2. Dry run using physical row numbers and display the proposed action or reason for each row. Cover new products, exact SKU matches, normalized name matches, repeated rows or targets, ambiguous matches, and invalid rows. Prefer an unambiguous exact SKU match. Present ambiguity as a skipped row with an explanation. Ignore entirely empty rows.
3. Default to adding valid new products and skipping matches. Offer an explicit update mode that also updates unambiguous SKU matches and name-only matches individually confirmed by the user. If the file, sheet, mapping, or catalog state changes after preview, require a new dry run.
4. Apply the accepted batch in one transaction. Show inserted, updated, skipped, and invalid totals. Cancellation before apply leaves the catalog intact. A failed apply rolls back the batch and reports the error.
5. Save a valid `.xlsx` import template with Arabic headers and an example. Export all active products or every row matching the current filter across pages. Write normal prices as numeric cells and prices that exceed Excel’s numeric precision as exact decimal text. Write names and SKUs as literal text and preserve Arabic and leading zeros.

### SET-01 · Company and invoice preferences

Provide editable company name, phone, and address, sanitized SVG import and reset, a six-digit HEX brand color, and ordered reusable terms. Default the company fields to blank and the logo to the bundled artwork. Reject blank or duplicate new terms and limit each to 300 characters. Copy selected term text into a draft and issued invoice so later Settings changes do not rewrite it.

Offer EGP, USD, EUR, SAR, and AED at two decimal places for new invoices, defaulting to EGP. Offer `dd/MM/yyyy`, `yyyy-MM-dd`, and `dd-MM-yyyy`, defaulting to `dd/MM/yyyy`. Store dates independently of display format as `YYYY-MM-DD`. Explain that changing the default currency relabels catalog prices without conversion. A draft keeps the currency it started with. If that differs from the active catalog currency, catalog selection can supply the item name and the operator must enter the price in the draft’s currency.

## 4. A4 document and Windows output

### DOC-01 · Preserve the invoice design

Use a versioned snapshot model and a local HTML/CSS template. Match the reference layout with A4 portrait (`210 × 297 mm`), 14 mm padding, white paper, bundled Cairo, company name above the logo, contact details, brand divider, and invoice number/date/customer blocks. The table has continuous line number, item, quantity, price, and total columns. Preserve the brand-colored table heading, alternating light rows, and aligned numeric cells. Put the emphasized grand total inside the table, with subtotal and discount when a discount applies. Place notes and selected terms below. Show company name when available and `page / page count` in the footer. Omit blank optional fields. Escape text, sanitize SVG, and load the entire document from local resources.

### DOC-02 · Paginate once and keep outputs aligned

Wait for fonts and logo before measuring. Lay out based on real content, repeat identity and appropriate table headings, and continue oversized text across pages. Keep the totals together and show them once at the end of the item list. Notes and terms can flow beyond that page. Maintain order and prevent clipping, overlapping footers, blank trailing pages, and loss of characters. Include the draft mark on every draft page.

Use a dedicated local WebView2 with `PrintToPdfStream` to produce a real paginated PDF with selectable Arabic text. Use that PDF for preview, images, and printing. Regenerate it when the selected draft or invoice snapshot changes. Window zoom and display scaling must not change pagination. Keep document controls, system URL headers, and window chrome out of the output. [Microsoft WebView2 print API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/print).

### DOC-03 · Save, reveal, and print

- Use native dialogs to choose the destination. Save one PDF or one ordered PNG or JPG per page at approximately `1588 × 2246` pixels. Use opaque white backgrounds for JPG. Use names such as `INV-000001.pdf` and `INV-000001-page-001.png`. Use a draft prefix and stable draft key for draft filenames.
- Confirm collisions before overwrite. Stage the complete set before publishing it to the chosen location. Clean up partial temporary outputs and report any files left behind if publishing fails. Offer **Open containing folder** and **Copy path**. Leave the invoice untouched on cancel, write failure, or insufficient storage.
- Open the Windows print dialog with printer, range, and copies controls. Render the requested pages of the same PDF with the Windows PDF API and send them one at a time to the selected printer device context. Default to A4 portrait and scale proportionally to fit the printer’s printable area. Distinguish canceled, failed, and handed off to the printer. Verify the physical output separately. [Windows PDF page rendering](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpage?view=winrt-26100), [Windows printer device contexts](https://learn.microsoft.com/en-us/windows/win32/printdocs/printer-output).

## 5. Architecture, backup, and migration

### Application and data boundaries

Use Tauri 2, React, TypeScript, and Vite for the desktop shell and UI. Use Rust for domain logic, SQLite, workbook handling, and Windows adapters. Bundle SQLite with FTS5 trigram, enable WAL and foreign keys, and apply versioned migrations. Run one instance per Windows user and serialize writes. Use a fixed independent product ID, proposed as `com.ua.invoice.desktop`. Store data and backups in per-user locations separate from the installed program. Keep UI and document assets bundled. Restrict the native file layer to application storage and user-selected files or folders. Keep the PDF webview isolated from privileged application commands and remote content.

| Boundary | Contract |
| --- | --- |
| `saveDraft(draftKey, editablePayload)` | Persist a draft revision and report durable success or a structured error. Inputs include raw quantity, price, and discount text. |
| `issueDraft(draftKey)` | Return the authoritative issued snapshot or a structured error. The UI supplies neither totals nor the permanent number. |
| History and catalog queries | Pass search/filter inputs and a bounded page request. Return deterministic pages and exact decimal strings for IDs or amounts. |
| Workbook preview and apply | Produce a preview ID bound to the input and catalog state. Apply only the reviewed preview, selected strategy, and confirmed rows. |
| Document service | Resolve a validated draft revision or issued snapshot into a paginated document. Use it for preview, export, and printing. |
| `inspectBackup(path)` / `restoreBackup(stagedId)` | Inspect a staged copy and return source/version, counts, and warnings. Restore only the inspected payload after confirmation. |

### BAK-01 · Create and retain backups

Take a SQLite-consistent copy with the Online Backup API. Write a ZIP containing `invoice.db`, optional `brand/logo.svg`, and `manifest.json` with product identifier, archive format version, database schema version, app version, UTC creation time, and SHA-256 checksums for payload files. Keep the active brand with the data generation so activation cannot pair one dataset with another seller’s assets. Verify the archive before exposing it as complete. Show last successful backup time, file size, and local backup list. A failure leaves the last-success marker intact.

Manual backups can be saved outside the app. Automatic backup defaults to off. When enabled, run once per local calendar day at first launch or when the app next gains focus. Default to keeping seven automatic backups and retry a failed due run on a later eligible launch or focus. Retention applies only to automatic backups created by this app. Preserve manual exports and the latest pre-restore safety copy.

### BAK-02 · Accept three backup families

| Source | Detection and conversion |
| --- | --- |
| **New Windows clone** | Require the clone product identifier, supported format and schema versions, and valid payload checksums. Migrate a staged copy when schema advances. |
| **UA Invoices for Android** | Accept manifest `format: 1` and database schemas **1–3**, checking Android schema markers and agreement with the manifest. Validate `invoice.db` and any `brand/logo.svg` against the `checksums` map. Transfer portable settings, editable drafts, and issuance state. Preserve draft Trash where supported by the source schema. |
| **Original UA Invoices for Windows** | Accept its ZIP of `invoice.db` and optional `brand/logo.svg`, without a manifest. Support schemas **v1–v3**. Inspect expected table and column shape and check migration metadata when present. Use empty terms in v1 and zero discount in v1–v2. Convert both persisted invoice drafts and the editor recovery payload into editable drafts. |

Preserve products, SKU and price values, deletion state, invoice numbers and ordered lines, seller and currency snapshots, discount and saved totals, notes, terms, dates, drafts, and portable settings. Preserve each issued record’s stored values during migration. Keep platform-specific backup destinations, backup scheduling, and updater preferences local to the new app. Use available issue records to keep consumed drafts out of the editable list, even when the corresponding invoice was purged. Reject a required record that cannot be converted and identify the failure during inspection.

Keep the counter at least as high as the current destination counter, imported counter, imported invoice numbers, and available imported issue history. For example, a known maximum of 50 makes the next issue `INV-000051`. A fresh installation can carry forward the numbers known to its backup. Two independently active installations may issue overlapping numbers after a transfer. The Arabic guide must explain how to choose one active installation when numbering continuity matters.

### BAK-03 · Restore with rollback

1. Read the archive into private staging. Enforce compressed and expanded size limits and reject unexpected entries, duplicate paths, unsafe SVG, malformed data, corrupt SQLite, checksum mismatch, and unsupported schemas. Validate SQLite integrity and foreign keys. Check required domain fields as part of staging.
2. Show source, version, creation date if known, record counts including Trash, and any compatibility warnings. State that restore replaces the destination dataset. Let the operator cancel before replacement.
3. After confirmation, pause writes and autosave and make a verified safety backup. Migrate the staged dataset, build derived indexes, verify it again, and persist the destination number high-water mark in the staged database.
4. Activate on the same volume using a durable restore journal and an atomic switch to the validated data generation. After an interruption, reopen either the complete prior generation or the complete validated replacement. On error, keep the safety backup and source archive available and show an actionable Arabic error.

## 6. Installer, release, and implementation order

- Build a per-user NSIS `.exe` on Windows x64. Embed the offline WebView2 installer so a compatible clean machine can install without downloading a runtime. Confirm the installer works as a standard user on the two target Windows versions. Keep the identity and install family stable after the first release. [Tauri Windows installer guide](https://v2.tauri.app/distribute/windows-installer/).
- Publish the tested installer to the **new public GitHub repository’s Releases page**, along with SHA-256, a versioned release note, and the Arabic guide. A GitHub Actions build can produce release assets from a version tag. Keep the repository URL configurable until the owner creates it. Use Windows Authenticode signing when the owner provides a signing identity. If the installer is unsigned, state that in the release notes and document the Windows prompt. Keep real customer data and signing credentials out of source, fixtures, and release artifacts.
- Upgrade by running the newer setup over the installed version after closing the app. Save pending work before normal shutdown. Take a verified pre-migration backup before applying schema changes. Fail safely if migration fails or an older binary encounters a newer database schema. Show the installed version and document upgrade and recovery steps in Arabic.

| Milestone | Exit criteria |
| --- | --- |
| **1. Windows feasibility** | Install an x64 package offline, render a real Arabic multipage A4 PDF with bundled assets, open native Print, rasterize the PDF, and stage one backup from each existing app. |
| **2. Product workflows** | Complete invoice, catalog, settings, workbook, document, and backup workflows. Pass exact arithmetic, persistence, restore, and source migration tests on real SQLite. |
| **3. Release verification** | Complete physical print and both Windows targets, confirm upgrade from populated data, verify coexistence with the original Windows installer, and trace release checksums to the tested build. |

Deliver source and lockfiles, migrations, deterministic synthetic fixtures, build and release instructions, the installer, checksum, release notes, Arabic user guide, and a verification report. The user guide covers installation, in-place upgrade, backup to an external destination, restore replacement, Android and legacy Windows migration, numbering continuity, draft marking, and printing. The report names the Windows versions, hardware or VM, printer, tested build, and any outstanding checks.

## 7. Acceptance matrix

| Gate | Evidence required before v1 release |
| --- | --- |
| **Offline operation** | Install from a previously downloaded file with networking disabled. Create one catalog line and one manual line, save an incomplete draft, force-close and recover it, add discount/notes/terms, issue it once, reopen it, duplicate it, and trash and restore it. Verify an autosave failure and an issue retry. |
| **Financial correctness** | Run the two arithmetic examples, Arabic and Persian digits, decimal separators, quantity scaling, zero price, invalid percentage and fixed discount, overflow, and values larger than JavaScript’s safe integer range. Change seller, currency, and product settings after issue and compare the original saved invoice and History. |
| **Catalog and workbook** | Import the 10,000-product fixture and measure search p95 below 200 ms, excluding debounce, on a documented Windows reference PC. Exercise short and variant Arabic queries, ambiguous matches, selected sheet header changes, stale previews, transaction rollback, and export across every matching page. |
| **Document consistency** | Use 1-, 30-, 100-, and 300-line fixtures with long Arabic names, mixed scripts, notes, terms, and discounts. Compare page counts, order, and content in preview, extracted PDF text, PNG/JPG, and printed copies. Check one grand total, each item exactly once, page labels, no clipping, and a draft mark on every draft page. Test save failure, normal and selected print ranges, cancel, Microsoft Print to PDF, and a physical A4 printer. |
| **Backup and migration** | Round-trip a clone backup. Import fixture coverage for Android schemas 1–3 and legacy Windows schemas 1–3, plus one genuinely exported archive from each existing app. Compare record counts, historical totals, identity, drafts, Trash, and the next invoice number. Test corruption, denied writes, low space, interruption during activation, and a restore after clean installation. |
| **Accessibility and desktop** | Test keyboard-only invoice entry, focus and modal return, Windows Narrator, bidi text, and reachable actions at 100%, 125%, 150%, and 200% DPI scaling. |
| **Installer and upgrades** | Clean-install on Windows 11 x64 and Windows 10 22H2 x64, including the embedded runtime path. Populate invoices, drafts, catalog, and settings, then upgrade in place with the next setup file. Confirm the original Windows app remains independently usable and the published hash matches the tested installer. |

## 8. Source references for the new repository

The paths below are relative to their respective source repositories. Copy this PRD into the new project as the product and implementation baseline.

| Source repository | Reference | Use |
| --- | --- | --- |
| `ua-apk-invoices` (`d6a7644`) | `mobile-app-PRD.md` and `docs/VERIFICATION.md` | Original mobile intent, shipped behavior, and platform verification limits. |
| `ua-apk-invoices` (`d6a7644`) | `src/domain/invoice.ts`, `src/domain/decimal.ts`, and `src/db/draft.ts` | Exact arithmetic, parsing, and draft model. |
| `ua-apk-invoices` (`d6a7644`) | `src/document/render.ts` and `src/document/paginateScript.ts` | Arabic A4 design, template content, and measured pagination reference. |
| `ua-apk-invoices` (`d6a7644`) | `src/db/backup.ts`, `src/db/schema.ts`, and `tests/fixtures/` | Android archive layout, schema revisions, and migration and scale fixtures. |
| `ua-windows-invoices` (`137babc`) | `Project Docs/Windows_Invoice_Application_PRD.md` and `src-tauri/src/export/windows.rs` | Desktop workflow and Windows WebView2 integration reference. |
| `ua-windows-invoices` (`137babc`) | `src-tauri/src/db/migrations/` and `src-tauri/src/services/backup.rs` | Legacy Windows schema v1–v3 and backup archive layout. |

The imports from both existing apps and the public installer distribution are product decisions. The x64 targets, Windows version floor, Tauri stack, and desktop identifier are v1 implementation defaults. Establish the final app identifier before creating the first public installer so future releases can preserve its installation and data paths.
