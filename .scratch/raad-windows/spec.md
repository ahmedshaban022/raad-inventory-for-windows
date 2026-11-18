# Raad Windows design interview

Status: All twenty product decisions and the consolidated PRD were approved. Feasibility verification is in progress.

## Authority

The [Raad Windows PRD](../../Project%20Doc/windows-clone-PRD.md) is the consolidated
review document. It replaces the original UA Invoices requirements, which are
retained only in its explicitly non-authoritative archive. The ADRs record why
important choices were made; this index records interview completion.

## Settled design tree

- Product identity, operating context, and v1 scope: [ADR-0001](../../docs/adr/0001-raad-windows-offline-scope.md).
- C#, WPF, and SQLite: [ADR-0002](../../docs/adr/0002-csharp-wpf-sqlite.md).
- Windows backups, replacement recovery, Android exporter/transfer, and cutover: [ADR-0003](../../docs/adr/0003-local-backups-and-android-transfer.md).
- Retained issued invoices, cancellation boundaries, and legitimate legacy deletion traces: [ADR-0004](../../docs/adr/0004-preserve-issued-invoice-history.md).
- Offline activation, interface-only privacy lock, installation-local credentials, recovery code, and local support reset: [ADR-0005](../../docs/adr/0005-separate-offline-activation-and-privacy-lock.md).
- Original sale facts with explicitly current payment status on re-export: [ADR-0006](../../docs/adr/0006-payment-aware-invoice-exports.md).
- Desktop interaction, outputs, workload/budgets, delivery, and release acceptance: consolidated in the PRD.

## Interview record

| Round | Accepted questions | Result |
| --- | --- | --- |
| 1 | Q1–Q3 | Raad baseline; Windows 10/11 x64, 4 GB reference; one offline operator/computer. |
| 2 | Q4–Q9 | C#/WPF/SQLite; optional Android transfer; A4 outputs/printing; local backups; distinct activation/privacy; no issued-invoice deletion. |
| 3 | Q10–Q16 | Companion Android exporter; scoped Raad features; dated live payment summary; cancellation/refund boundaries; UI-only lock/recovery; measurable budgets; independent public release. |
| 4 | Q17–Q20 | Stop Android entry at export and continue Windows after verified transfer; preserve legitimate missing-history traces; daily backup while open with retry and protected retention; support reset of privacy only. |

## Frontier

No unanswered product decision remains after Q1–Q20.

The user requested a consolidated final PRD and review before implementation.
Await that document approval; no Windows implementation or companion Android
change has started in this task.

## Engineering evidence still pending

The [feasibility plan](feasibility.md) selects the exact runtime/document adapter
and defines the required measurements, installation/upgrade checks, transfer
proof, recovery tests, and physical printing evidence. These are pending
implementation outcomes, not already-passed gates or new product questions.
