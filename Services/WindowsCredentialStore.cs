using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Thin wrapper over the Windows Credential Manager (advapi32 CredRead/CredWrite/CredDelete)
    /// for storing secrets (such as source control tokens) secured by DPAPI rather than in SQLite.
    /// Secrets are persisted as generic credentials under a caller-supplied target name.
    /// </summary>
    public static class WindowsCredentialStore
    {
        /// <summary>
        /// Stores a secret under the given credential target name. Overwrites any existing value.
        /// Returns true on success.
        /// </summary>
        public static bool Write(string target, string secret)
        {
            var byteArray = Encoding.Unicode.GetBytes(secret);

            var credential = new NativeCredential
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)byteArray.Length,
                CredentialBlob = Marshal.AllocHGlobal(byteArray.Length),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = target
            };

            try
            {
                Marshal.Copy(byteArray, 0, credential.CredentialBlob, byteArray.Length);
                return CredWrite(ref credential, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }
        }

        /// <summary>
        /// Reads the secret stored under the given credential target name.
        /// Returns null if no credential exists.
        /// </summary>
        public static string Read(string target)
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
                return null;

            try
            {
                var cred = Marshal.PtrToStructure<NativeCredential>(credPtr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                    return null;

                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        /// <summary>
        /// Deletes the credential stored under the given target name.
        /// Returns true if a credential was deleted.
        /// </summary>
        public static bool Delete(string target)
        {
            return CredDelete(target, CRED_TYPE_GENERIC, 0);
        }

        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, int type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete(string target, int type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr credential);
    }
}
