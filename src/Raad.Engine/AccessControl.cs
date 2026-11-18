using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raad.Engine;

/// <summary>
/// Per-Windows-user locations. Business data never lives inside the program folder, and access
/// state (activation and privacy lock) is kept outside the business data so that a business
/// backup or a data replacement never carries or resets it.
/// </summary>
public static class AppPaths
{
    public const string Product = "raad-inventory-windows";
    public const string InstallFamily = "raad-inventory";

    private static string? rootOverride;

    /// <summary>
    /// Redirects every location to another root (used by the explicit QA mode only). Passing null
    /// restores the default per-user location under %LOCALAPPDATA%.
    /// </summary>
    public static void ApplyRootOverride(string? root)
    {
        rootOverride = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
    }

    public static bool IsOverridden => rootOverride is not null;

    public static string Root => rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaadInventory");

    public static string DataDirectory => Path.Combine(Root, "data");
    public static string DatabasePath => Path.Combine(DataDirectory, "raad-inventory.db");
    public static string BackupDirectory => Path.Combine(Root, "backups");
    public static string AutomaticBackupDirectory => Path.Combine(BackupDirectory, "auto");
    public static string SafetyBackupDirectory => Path.Combine(BackupDirectory, "safety");
    public static string ExportDirectory => Path.Combine(Root, "exports");
    public static string StateDirectory => Path.Combine(Root, "state");
    public static string LogDirectory => Path.Combine(Root, "logs");
    public static string StagingDirectory => Path.Combine(Root, "staging");

    public static void Ensure()
    {
        foreach (var directory in new[] { Root, DataDirectory, BackupDirectory, AutomaticBackupDirectory, SafetyBackupDirectory, ExportDirectory, StateDirectory, LogDirectory, StagingDirectory })
            Directory.CreateDirectory(directory);
    }
}

/// <summary>Offline activation state of this installation.</summary>
public sealed class ActivationState
{
    [JsonPropertyName("activatedAt")] public string? ActivatedAt { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "preview-owner-setup";
    [JsonPropertyName("salt")] public string? Salt { get; set; }
    [JsonPropertyName("verifier")] public string? Verifier { get; set; }
    [JsonPropertyName("iterations")] public int Iterations { get; set; } = 120_000;
    [JsonPropertyName("product")] public string Product { get; set; } = AppPaths.Product;

    [JsonIgnore] public bool IsLicensedProduction => Kind == "provisioned";
    [JsonIgnore] public bool IsActivated => !string.IsNullOrEmpty(Verifier);
    [JsonIgnore] public string DisplayKind => IsLicensedProduction ? "تفعيل منتج" : "إعداد مالك (معاينة)";
}

/// <summary>Privacy lock state: separate from activation and stored outside the business data.</summary>
public sealed class LockState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("salt")] public string? Salt { get; set; }
    [JsonPropertyName("verifier")] public string? Verifier { get; set; }
    [JsonPropertyName("recoverySalt")] public string? RecoverySalt { get; set; }
    [JsonPropertyName("recoveryVerifier")] public string? RecoveryVerifier { get; set; }
    [JsonPropertyName("iterations")] public int Iterations { get; set; } = 120_000;
    [JsonPropertyName("lockedAt")] public string? LockedAt { get; set; }
}

/// <summary>Offline activation and the optional privacy lock, both stored under the state folder.</summary>
public static class AccessControl
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string ActivationFile => Path.Combine(AppPaths.StateDirectory, "activation.json");
    private static string LockFile => Path.Combine(AppPaths.StateDirectory, "privacy-lock.json");

    public static ActivationState ReadActivation()
    {
        AppPaths.Ensure();
        if (!File.Exists(ActivationFile)) return new ActivationState();
        try { return JsonSerializer.Deserialize<ActivationState>(File.ReadAllText(ActivationFile), Json) ?? new ActivationState(); }
        catch { return new ActivationState(); }
    }

    public static void WriteActivation(ActivationState state)
    {
        AppPaths.Ensure();
        WriteAtomic(ActivationFile, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>
    /// Optional local owner setup of a preview installation. No secret is embedded in the program;
    /// the verifier is derived from the operator password with a random salt. The preview stays
    /// usable without it because no provisioned product verifier exists yet.
    /// </summary>
    public static ActivationState OwnerSetup(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("اكتب كلمة مرور من 6 أحرف على الأقل.");
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var state = new ActivationState
        {
            Kind = "preview-owner-setup",
            Salt = salt,
            Verifier = Verify(password, salt, 120_000),
            Iterations = 120_000,
            ActivatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        WriteActivation(state);
        return state;
    }

    public static bool VerifyActivation(string password)
    {
        var state = ReadActivation();
        if (!state.IsActivated || state.Salt is null || state.Verifier is null) return false;
        return FixedTimeEquals(state.Verifier, Verify(password, state.Salt, state.Iterations));
    }

    /// <summary>Reads the lock state. Corrupt state fails closed: the installation stays locked.</summary>
    public static LockState ReadLock()
    {
        AppPaths.Ensure();
        if (!File.Exists(LockFile)) return new LockState();
        try
        {
            var state = JsonSerializer.Deserialize<LockState>(File.ReadAllText(LockFile), Json);
            if (state is null) throw new InvalidDataException("ملف القفل غير صالح.");
            if (state.Enabled && (state.Salt is null || state.Verifier is null))
                throw new InvalidDataException("ملف القفل غير صالح.");
            return state;
        }
        catch (Exception)
        {
            // Fail closed: an unreadable lock file keeps the data protected.
            return new LockState
            {
                Enabled = true,
                Salt = "corrupt",
                Verifier = "corrupt",
                RecoverySalt = "corrupt",
                RecoveryVerifier = "corrupt"
            };
        }
    }

    public static void WriteLock(LockState state)
    {
        AppPaths.Ensure();
        WriteAtomic(LockFile, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>Enables the privacy lock and returns the one-time recovery code.</summary>
    public static string EnableLock(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("اكتب كلمة مرور من 6 أحرف على الأقل.");
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var recoverySalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var recoveryCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
        WriteLock(new LockState
        {
            Enabled = true,
            Salt = salt,
            Verifier = Verify(password, salt, 120_000),
            RecoverySalt = recoverySalt,
            RecoveryVerifier = Verify(recoveryCode, recoverySalt, 120_000),
            Iterations = 120_000
        });
        return recoveryCode;
    }

    /// <summary>True when the given password unlocks the current privacy lock.</summary>
    public static bool Unlock(string password)
    {
        var state = ReadLock();
        if (!state.Enabled) return true;
        if (state.Salt is null || state.Verifier is null) return false;
        if (state.Salt == "corrupt") return false;
        return FixedTimeEquals(state.Verifier, Verify(password, state.Salt, state.Iterations));
    }

    public static bool UnlockWithRecovery(string recoveryCode)
    {
        var state = ReadLock();
        if (!state.Enabled) return true;
        if (state.RecoverySalt is null || state.RecoveryVerifier is null) return false;
        if (state.RecoverySalt == "corrupt") return false;
        return FixedTimeEquals(state.RecoveryVerifier, Verify(recoveryCode.Trim(), state.RecoverySalt, state.Iterations));
    }

    /// <summary>Local support reset documented for the owner of the Windows account.</summary>
    public static void SupportReset()
    {
        WriteLock(new LockState());
    }

    public static void DisableLock() => WriteLock(new LockState());

    private static string Verify(string password, string salt, int iterations)
    {
        return Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromHexString(salt), iterations, HashAlgorithmName.SHA256, 32));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
