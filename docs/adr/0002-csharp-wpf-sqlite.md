# Use C#, WPF, and SQLite for the Windows application

The user accepted C# with WPF for the Windows desktop interface and application logic, and SQLite for local data, to prioritize a responsive Windows-only application on the agreed 4 GB reference computer. This replaces the supplied UA PRD's Tauri, React, and Rust application stack; keeping the main interface outside a browser shell was preferred to reusing a web interface.

The exact .NET release remains conditional on installation, operation, and performance checks on the agreed Windows 10 and Windows 11 x64 targets. This decision is not a measured claim that WPF outperforms every Rust interface, and it does not yet select a PDF engine or authorize a permanently running document webview.
