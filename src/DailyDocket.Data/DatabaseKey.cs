using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DailyDocket.Data;

/// <summary>
/// The database key, kept in Windows Credential Manager on each machine.
/// </summary>
/// <remarks>
/// Generated on first use and never leaves the machine: each machine has its own cache, so there is
/// nothing to share, and a lost key only means a fresh pull. Credential Manager protects it with
/// the user's Windows login (DPAPI), which is what keeps a lost laptop's cache unreadable.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DatabaseKey
{
    private const string Target = "DailyDocket/database";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public static string GetOrCreate()
    {
        if (Read() is { } existing)
        {
            return existing;
        }

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Write(key);
        return key;
    }

    private static string? Read()
    {
        if (!CredRead(Target, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(handle);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    private static void Write(string key)
    {
        var bytes = Encoding.Unicode.GetBytes(key);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "Encrypts DailyDocket's local cache of mail, calendar and Slack.",
            };
            if (!CredWrite(ref cred, 0))
            {
                throw new InvalidOperationException(
                    $"DailyDocket could not save its database key in Windows Credential Manager (error {Marshal.GetLastPInvokeError()}). Check that Credential Manager is available for your account, then start DailyDocket again.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CredFree(IntPtr buffer);
}
