namespace CtrlAltDaze.Core;

/// <summary>Product identity shared by the engine, CLI and editor.</summary>
public static class AppInfo
{
    public const string ProductName = "Ctrl Alt Daze";

    /// <summary>Folder name under %APPDATA% / %LOCALAPPDATA%.</summary>
    public const string FolderName = "CtrlAltDaze";

    /// <summary>Saved sign-ins a flow can use are generic credentials named <c>CtrlAltDaze:&lt;name&gt;</c> (D-031).</summary>
    public const string CredentialPrefix = "CtrlAltDaze:";
}
