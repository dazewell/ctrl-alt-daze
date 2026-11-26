using System.Runtime.InteropServices;
using CtrlAltDaze.Core;

namespace CtrlAltDaze.Editor.Inspector;

/// <summary>
/// Writes a flow's saved sign-in: a generic credential <c>CtrlAltDaze:&lt;name&gt;</c> that the engine reads by name (D-014,
/// D-031). The secret goes as UTF-16 like cmdkey's, kept on this PC only (it never roams), and is never held longer than the call.
/// </summary>
internal static unsafe partial class SavedKeys
{
    private const uint CredTypeGeneric = 1, CredPersistLocalMachine = 2;

    /// <summary>Saves or replaces the secret; false when Windows refuses (the sentence says why).</summary>
    public static bool TrySave(string name, string secret, string comment, out string? problem)
    {
        var blob = (secret.Length * sizeof(char));
        fixed (char* target = AppInfo.CredentialPrefix + name, text = comment, value = secret, user = "")
        {
            var credential = new CredentialW
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = text,
                CredentialBlobSize = (uint)blob,
                CredentialBlob = (byte*)value,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (CredWriteW(&credential, 0))
            {
                problem = null;
                return true;
            }
        }
        problem = $"Windows didn't save the key (error {Marshal.GetLastPInvokeError()}).";
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialW
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(CredentialW* credential, uint flags);
}
