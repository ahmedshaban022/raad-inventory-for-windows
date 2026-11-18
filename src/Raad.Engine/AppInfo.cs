namespace Raad.Engine;

/// <summary>Product identity of this build. The installer family and identity stay stable across updates.</summary>
public static class AppInfo
{
    public const string Version = "0.2.0-preview";
    public const string InstallFamily = AppPaths.InstallFamily;
    public const string DisplayName = "رعد — المخزون والمبيعات";
    public const bool IsReleaseQualified = false;
}
