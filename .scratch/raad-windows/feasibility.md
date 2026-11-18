# Windows feasibility and release evidence

Status: Development-host verification completed for keyboard selection, virtual A4 printing, and Android transfer inspection. Reference-device and physical-print gates remain open.

The accepted product and performance requirements are consolidated in
[the Raad Windows PRD](../../Project%20Doc/windows-clone-PRD.md), with the
[completed interview](spec.md) and its ADR links preserving their rationale.
The owner approved the PRD and C#, WPF, and SQLite for this phase.

## Bounded technology selection

1. Keep the approved C#, WPF, and SQLite application boundary.
2. Select and pin a .NET version only after a self-contained x64 build installs
   and operates offline on the intended Windows 10 and Windows 11 editions.
   Record application compatibility separately from Microsoft's supported-OS
   policy; successful testing does not extend Microsoft's support policy.
3. Evaluate a .NET PDF engine with bundled Arabic fonts. Verify the exact
   package version's license and the application owner's eligibility alongside
   Arabic shaping, mixed-direction text, selectable text, and pagination.
4. If a suitable .NET engine fails, evaluate a WebView2 document adapter that
   runs on demand and releases its resources after generation. Its complete
   process group and offline runtime are part of resource and installer tests.
5. Use the same generated PDF for preview, page-image output, and print-page
   rendering. Process pages incrementally and retain a bounded preview cache.
6. Record the selected runtime and document engine, versions, rationale,
   applicable license, and measured results. Return a concrete trade-off if
   no candidate meets the approved requirements; a failed adapter does not
   automatically replace the approved WPF application interface.

Native WPF printing is based on its document/print APIs. The Windows
`IPdfRendererNative.RenderPageToDeviceContext` interop method accepts a
Direct2D device context; it is not a direct printer-GDI-HDC adapter as implied
by the original UA PRD. The final print route must be verified with page range,
copies, scaling, and a physical A4 output.

## Evidence gates

| Gate | Evidence |
| --- | --- |
| Offline deployment | Clean x64 installer and launch with networking disabled on both agreed Windows targets; required runtime and native dependencies included. |
| Arabic document | Synthetic 1-, 30-, 100-, and 300-line invoices; correct Arabic joining and mixed scripts, selectable/extractable text, each line once, one total, unclipped pages, matching preview/images/print. |
| Performance | Exact CPU, RAM, storage, OS, build, dataset shape, and method; accepted startup/search/ordinary-memory budgets; document-generation time and process-group memory peak reported separately. |
| Realistic data | The accepted 10,000 products and 100,000 invoices have real line items, stock movements, customers, and payment history; fixture volumes and distributions are documented. |
| Android transfer | A real archive from the companion exporter plus deterministic synthetic fixtures; current supported schema detection and rejection of unsupported or malformed input; reconciled stock, totals, effective payments, balances, and references. |
| Recovery | Consistent backup, inspected replacement, safety copy, interruption rollback, failed backup retry, denied writes, low disk space, and restart after interrupted operations. |
| Financial history | Idempotent issue/cancellation/payment correction, no issued-invoice permanent deletion, no cancellation with effective payments, and correct current payment summary without changing issued sale facts. |
| Desktop | Keyboard-only workflows, Arabic bidi, visible focus, modal return, Narrator, high DPI, and reachable save/issue actions on the agreed screen sizes. |
| Upgrade and distribution | Populate data, upgrade using the next installer, preserve data and installation-local access state, verify the pre-migration backup and release checksum. |
| Physical printing | Named physical A4 printer and driver, selected page ranges/copies, legible Arabic, margins, and total/page consistency. Virtual print output alone does not satisfy this gate. |

Each result names its tested build and status. A development preview can be
identified separately from a verified v1 release; pending evidence stays visible.

## Results on 18 November 2026

### Keyboard and printing

- In the redesigned WPF window with bundled Cairo, Tab reached the line-count control, F4 opened it, and Down followed by Enter changed the value from 30 to 100 and left visible focus on the control. Alt+Down reopened the menu. Down followed by Escape kept 100 selected. Tab and Space generated the selected invoice.
- Ctrl+P opened the Windows print dialog. Escape produced the application’s cancellation message. Selecting Microsoft Print to PDF and pages 2–3 submitted a two-page job. That first job used the driver’s Letter default. Printing now validates an A4 portrait ticket against the selected queue and fits each page inside the driver’s printable area.
- Checking the printed pages exposed single item rows continuing onto another page. The document now moves rows together and keeps the totals with the last item when both fit. The 1-, 30-, 100-, and 300-line fixtures produced 1, 3, 10, and 28 pages. Extracted line markers were unique and in order. End markers on each row stayed on the same page as its start, and the totals appeared once on the final page. A stress case with one deliberately oversized name started on page 1 and retained the full name across 14 pages.
- After these changes, the production A4 ticket, renderer, and paginator produced two XPS pages from source pages 2–3. The local Microsoft Print to PDF driver completed the test job at 595.276 × 841.890 points. Comparing rasterized output against the source verified the selected page identities and order. The desktop locked before the updated code could be exercised through the print dialog a second time. Driver submission used a local test harness with an explicit output path.
- The saved evidence includes the [A4 output](../../artifacts/acceptance/corrected/printed-a4-pages-2-3.pdf), [driver ticket](../../artifacts/acceptance/corrected/print-verification.json), and [page validation](../../artifacts/acceptance/corrected/pdf-validation.json).

### Android transfer

The actual Raad Android 1.0.0 exporter ran on a disposable Android 16 (API 36) emulator in airplane mode with Wi-Fi off. An Android app received each archive through the system share sheet and saved it locally for the Windows probe. The companion exporter retains the archive after sharing so the receiver can read it after the chooser returns. Its temporary database snapshot is removed once packaging finishes, and older transfer archives are cleaned on a later export.

| State exported on Android | Stock | Stock movements | Invoices | Effective payments | Sales | Paid | Outstanding |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| One product with opening stock | 12 | 1 | 0 | 0 | EGP 0.00 | EGP 0.00 | EGP 0.00 |
| One walk-in sale, paid in full | 11 | 2 | 1 | 1 | EGP 125.05 | EGP 125.05 | EGP 0.00 |

The production Windows inspector validated both schema-8 archives, checked database integrity and reconciled amounts using read-only staging. The staged sale retained reference `INV-20261118-4EBA8B472760BD7D5108`, its product link, quantity of one, and exact price. Both source archive hashes stayed identical after inspection. The automated fixtures also exercised outstanding balances, a reversed payment, a draft, legacy deletion traces, checksum mismatch, unsupported schema, unsafe paths, and inconsistent invoice totals. Production replacement and safety backup remain phase 3 work.

[Windows inspection results and hashes](../../artifacts/acceptance/android-verification.jsonl) and [synthetic sale archive](../../artifacts/acceptance/android-device-sale.raadbackup).

### Runtime and remaining gates

The development host runs Windows 11 Pro x64 (build 26300) on an i7-12700H with approximately 23.6 GB RAM. The model uses .NET SDK 10.0.402, .NET runtime 10.0.13, WPF, SQLite via Microsoft.Data.Sqlite 10.0.13, and QuestPDF 2026.9.2 with local Cairo. The owner confirmed a privately held business with annual gross revenue below USD 1 million, consistent with the Community license eligibility for the candidate. [QuestPDF Community License](https://www.questpdf.com/license/community.html).

A self-contained `win-x64` publish ran the document probe on the development host. The Windows suite passed 22 tests. Android lint, type checking, and all 86 tests passed. The development dataset includes 10,000 products, 100,000 invoices, 1,000,000 invoice items, 1,010,000 stock movements, and 100,000 payments. Its one-customer distribution bounds what can be inferred from it. Across 100 development-host searches, P95 was 3.73 ms and the slowest search was 52.40 ms. In the same process, the 300-line PDF took 121.9 ms to generate and the cumulative peak working set after its PNG and JPG exports was 292.7 MiB. [Probe measurements](../../artifacts/acceptance/verified-publish/probe-results.json).

Physical A4 output, multiple copies on a physical driver, and clean offline installation and performance on the agreed Windows 10 and Windows 11 target hardware with 4 GB RAM remain unverified. A supported Windows edition and an application that runs on a given OS build are separate checks. [Microsoft’s Windows support table](https://learn.microsoft.com/en-us/dotnet/core/install/windows).

## Primary technical references

- [.NET deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [.NET Windows support table](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [WPF printing](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/documents/printing-overview)
- [Windows PDF page rendering](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpage.rendertostreamasync)
- [PDF interop device-context signature](https://learn.microsoft.com/en-us/windows/desktop/api/windows.data.pdf.interop/nf-windows-data-pdf-interop-ipdfrenderernative-renderpagetodevicecontext)
- [SQLite backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)
- [QuestPDF content direction](https://www.questpdf.com/api-reference/content-direction.html)
- [QuestPDF license selection](https://www.questpdf.com/license/guide.html)
- [WebView2 PDF printing](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/print)
- [WebView2 process model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)
