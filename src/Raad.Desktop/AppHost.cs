using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Raad.Engine;

namespace Raad.Desktop;

/// <summary>
/// Host services that must be ready before any window or store exists: the per-user
/// single-instance guard, the QA data-root override, and the Windows session-lock privacy
/// trigger. Nothing in this type writes to the live data folder.
/// </summary>
public static class AppHost
{
    private const string MutexNamePrefix = @"Local\RaadInventory-SingleInstance-";

    private static Mutex? instanceMutex;

    /// <summary>
    /// Raised when the Windows session is locked (and when a remote session is lost) so the
    /// shell can hide the business data. The event is raised on the UI dispatcher.
    /// </summary>
    public static event Action? PrivacyLockRequested;

    /// <summary>True when the run was started with a QA data-root override.</summary>
    public static bool IsQaMode { get; private set; }

    /// <summary>The QA data root in effect, or null outside QA mode.</summary>
    public static string? QaRoot { get; private set; }

    /// <summary>True while this process holds the single-instance mutex.</summary>
    public static bool SingleInstanceHeld { get; private set; }

    /// <summary>True once the Windows session-switch subscription is in place.</summary>
    public static bool SessionLockSubscribed { get; private set; }

    /// <summary>The live data folder of this Windows user. QA runs must never write into it.</summary>
    public static string LiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaadInventory");

    /// <summary>The data folder this run actually uses: the live folder, or the QA root.</summary>
    public static string DataRoot
    {
        get
        {
            var engine = AppPaths.Root;
            if (!IsQaMode) return engine;
            // Fall back to the requested root when the engine still reports the live folder.
            return IsSameOrInside(engine, LiveRoot) ? QaRoot ?? engine : engine;
        }
    }

    /// <summary>The reason a start was refused; empty when nothing was refused.</summary>
    public static string RefusalMessage { get; private set; } = string.Empty;

    // ------------------------------------------------------------------ single instance

    /// <summary>
    /// Acquires the per-user single-instance mutex. Returns false when another instance of
    /// this user already holds it.
    /// </summary>
    public static bool TryAcquireSingleInstance()
    {
        if (SingleInstanceHeld) return true;
        try
        {
            // The QA root is part of the name so an isolated test run never collides with the
            // live instance of the same Windows user. The scope is a stable hash of the
            // canonical path, because string.GetHashCode is salted per process.
            var scope = IsQaMode && QaRoot is not null ? "-" + StableScope(QaRoot) : "";
            var name = MutexNamePrefix + CurrentUserSid() + scope;
            Mutex mutex;
            try
            {
                // A cross-session name is preferred; it needs a privilege that a normal user may
                // not have, so the per-session name stays as the fallback.
                try { mutex = new Mutex(false, @"Global\" + name.Replace("Local\\", string.Empty)); }
                catch (UnauthorizedAccessException) { mutex = new Mutex(false, name); }
            }
            catch (UnauthorizedAccessException)
            {
                // The guard cannot be enforced here, so the start is refused instead of failing open.
                SingleInstanceHeld = false;
                RefusalMessage = "لا يمكن التحقق من تشغيل نسخة واحدة فقط. أعد التشغيل بصلاحيات كافية.";
                return false;
            }
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                mutex.Dispose();
                return false;
            }
            instanceMutex = mutex;
            SingleInstanceHeld = true;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // The guard cannot be enforced here; start anyway and report it as not held.
            SingleInstanceHeld = false;
            return true;
        }
    }

    /// <summary>Releases the single-instance mutex.</summary>
    public static void ReleaseSingleInstance()
    {
        if (instanceMutex is null) return;
        try { instanceMutex.ReleaseMutex(); }
        catch (ApplicationException) { /* the mutex was not owned by this thread */ }
        instanceMutex.Dispose();
        instanceMutex = null;
        SingleInstanceHeld = false;
    }

    /// <summary>Stable, process independent scope for a path (SHA-256 over the canonical form).</summary>
    private static string StableScope(string path)
    {
        var canonical = path.Trim().TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..8];
    }

    private static string CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception) { return Environment.UserName; }
    }

    // ------------------------------------------------------------------ QA data root

    /// <summary>
    /// Reads the QA root from the command line. Supports "--qa &lt;dir&gt;" and "--qa=&lt;dir&gt;".
    /// Returns false when the switch is present but cannot be used; the reason is then in
    /// <paramref name="error"/>.
    /// </summary>
    public static bool TryReadQaRoot(string[] args, out string? root, out string? error)
    {
        root = null;
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.IsNullOrWhiteSpace(argument)) continue;
            if (argument.StartsWith("--qa=", StringComparison.OrdinalIgnoreCase))
            {
                root = argument["--qa=".Length..].Trim();
                if (root.Length == 0) { error = "مجلد الاختبار مفقود بعد --qa."; return false; }
                return true;
            }
            if (string.Equals(argument, "--qa", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    root = args[index + 1].Trim();
                    if (root.Length == 0) { error = "مجلد الاختبار مفقود بعد --qa."; return false; }
                    return true;
                }
                error = "مجلد الاختبار مفقود بعد --qa.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Applies the QA data-root override to the engine. It always refuses the live data folder
    /// and anything inside it: a QA run must never touch the business data.
    /// </summary>
    public static bool TryApplyQaRoot(string requestedRoot)
    {
        string resolved;
        try { resolved = Path.GetFullPath(requestedRoot.Trim()); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            RefusalMessage = "مجلد الاختبار غير صالح: " + requestedRoot;
            return false;
        }
        if (IsSameOrInside(resolved, LiveRoot))
        {
            RefusalMessage = "لا يمكن استخدام مجلد البيانات الفعلي في وضع الاختبار. اختر مجلدًا آخر.";
            return false;
        }
        AppPaths.ApplyRootOverride(resolved);
        IsQaMode = true;
        QaRoot = resolved;
        RefusalMessage = string.Empty;
        return true;
    }

    /// <summary>True when the given path is the live data folder or lives inside it.</summary>
    public static bool IsInsideLiveRoot(string path) => IsSameOrInside(path, LiveRoot);

    private static bool IsSameOrInside(string candidate, string parent)
    {
        string left, right;
        try
        {
            left = Normalize(candidate);
            right = Normalize(parent);
        }
        catch (Exception) { return false; }
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Length == 0 ? Path.DirectorySeparatorChar.ToString() : full;
    }

    // ------------------------------------------------------------------ session lock

    /// <summary>Subscribes to the Windows session-switch notifications.</summary>
    public static void SubscribeSessionEvents()
    {
        if (SessionLockSubscribed) return;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SessionLockSubscribed = true;
    }

    /// <summary>Unsubscribes from the Windows session-switch notifications.</summary>
    public static void UnsubscribeSessionEvents()
    {
        if (!SessionLockSubscribed) return;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SessionLockSubscribed = false;
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect)) return;
        RaisePrivacyLockRequested();
    }

    private static void RaisePrivacyLockRequested()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RaisePrivacyLockRequested));
            return;
        }
        PrivacyLockRequested?.Invoke();
    }
}
