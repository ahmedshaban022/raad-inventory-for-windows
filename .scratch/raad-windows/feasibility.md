# Windows feasibility and release evidence

Status: Offline installation and launch passed in isolated Windows 10 and 11 guests with 4 GiB RAM. Cold startup exceeded the target in five of six runs. Physical A4 and HDD reference-device verification remain open.

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

## Installation and multiple-copy follow-up

The Windows spooler and present-device inventory exposed three queues on the development host: Microsoft Print to PDF, Send to OneNote, and AnyDesk Printer. No USB or installed network printer was visible.

The A4 portrait job requested two copies of source pages 2–3 from the 100-line invoice. Microsoft Print to PDF completed the job with four A4 pages at 595.276 × 841.890 points. Raster hashes gave the sequence 2, 3, 2, 3. Physical feeding, margins, copy count, and collating remain open. [Two-copy A4 output](../../artifacts/acceptance/qualification/copies-virtual.pdf).

A per-user feasibility installer now packages the self-contained x64 application, SQLite native library, QuestPDF, and Cairo. The unsigned candidate built with Inno Setup 6.4.3 has SHA-256 `7D99DFBEFF26D64ECD837365B1B25F6FAA07527F38C4693AB1F359CFFE4507F3`. It installed in 10.31 seconds on the Windows 11 development host and the runtime trace showed CoreCLR loading from the installation directory. The installed document and SQLite probe exited successfully, and uninstall left the separate fixture database intact. The development host had network connectivity during this run. [Installer](../../artifacts/acceptance/qualification/installer-final/Raad-Inventory-Feasibility-win-x64.exe), [installer source](../../scripts/Raad.Feasibility.iss), and [installation results](../../artifacts/acceptance/qualification/host-final-install-result.json).

The Windows 11 development host reached its usable-window marker in 2.78 seconds with warm caches before tuning. A 100-query WPF test measured from input change to the matching row's completed layout. Reducing the search delay from 150 to 100 ms and starting the initial load immediately lowered P95 from 224.4 to 179.5 ms. The tuned run peaked at 337.1 MB working set and 255.1 MB private bytes. The 300 MB ordinary-use memory gate remains open on the reference device. [Before tuning](../../artifacts/acceptance/qualification/host-ui-inprocess.json) and [after tuning](../../artifacts/acceptance/qualification/host-ui-inprocess-tuned.json).

### Offline Windows 10 and 11 guests

Both QEMU 11.1.0 guests used WHPX acceleration, an NVMe-backed virtual disk, two vCPUs, and 4 GiB allocated RAM (4,288,389,120 usable bytes). Each guest reported zero network adapters. A standard local account installed the same candidate from read-only media on each clean x64 guest, with no preinstalled .NET Desktop Runtime. Both launched on the bundled .NET 10.0.13. [Windows 10 test log](../../artifacts/acceptance/qualification/windows10-qualification.jsonl) and [Windows 11 test log](../../artifacts/acceptance/qualification/windows11-qualification.jsonl).

| Measured result | Windows 10 Enterprise LTSC Evaluation, build 19044 | Windows 11 Enterprise LTSC Evaluation, build 26100 |
| --- | ---: | ---: |
| Clean offline installation | Passed, 15.62 s | Passed, 13.43 s |
| Cold launch to usable window, three guest restarts | 8.62 s, 6.99 s, 6.31 s | 9.52 s, 5.69 s, 4.83 s |
| Cold launch within 5 s | 0/3 | 1/3 |
| WPF search P95, 100 queries (budget 200 ms) | 139.1 ms | 142.2 ms |
| Peak working set during WPF search (budget 300 MB) | 178.8 MB | 185.3 MB |
| SQLite search P95, 100 queries | 26.6 ms | 37.8 ms |
| 300-line PDF generation (28 pages) | 268.1 ms | 87.5 ms |
| Peak working set after PDF, PNG, and JPG export | 220.6 MB | 224.4 MB |

Cold startup uses elapsed time from application process creation through initial product loading and the usable-window marker. Each sample followed a guest restart and a 25-second desktop settling interval. The WPF search test includes the 100 ms input delay and ends when the matching row is laid out. A separate test driving the installed UI and polling accessibility results from another process observed P95 of 394.3 ms and 253.8 ms for Windows 10 and 11 respectively, with zero child processes. This measure includes cross-process observation. Both guests used the same SQLite fixture described above. [Windows 10 WPF timings](../../artifacts/acceptance/qualification/windows10-light-probe.json) and [Windows 11 WPF timings](../../artifacts/acceptance/qualification/windows11-light-probe.json).

The offline deployment check passed for these two editions. The cold-start target failed in the guests, and the PRD's performance gate on the dual-core, 4 GB, HDD reference hardware remains open. Physical A4 output and collation of two copies are blocked on a physical printer. The PDF spooler evidence above covers A4 size, range, and copy order on the virtual driver.

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
