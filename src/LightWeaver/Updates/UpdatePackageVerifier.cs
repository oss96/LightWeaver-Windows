using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace LightWeaver.Updates;

/// <summary>Authenticates staged packages before they can run.</summary>
public static class UpdatePackageVerifier
{
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private const uint CertEUntrustedRoot = 0x800B0109;
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static async Task<bool> VerifyAsync(string installerPath, string? debugSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(installerPath))
        {
            Detail("event=verify outcome=failure mode=none reason=absent");
            return false;
        }
        var started = Stopwatch.StartNew();
#if DEBUG
        if (debugSha256 is not null)
        {
            var matches = string.Equals(await ComputeSha256Async(installerPath, cancellationToken).ConfigureAwait(false),
                debugSha256, StringComparison.OrdinalIgnoreCase);
            // Which mode decided and what it decided. Neither digest is recorded.
            Detail(FormattableString.Invariant(
                $"event=verify outcome={(matches ? "success" : "failure")} mode=fixture elapsed_ms={started.ElapsedMilliseconds}"));
            return matches;
        }
#endif
        var trusted = VerifyAuthenticode(installerPath);
        // The signer's subject, thumbprint and chain are identity and stay out of the record.
        Detail(FormattableString.Invariant(
            $"event=verify outcome={(trusted ? "success" : "failure")} mode=authenticode elapsed_ms={started.ElapsedMilliseconds}"));
        return trusted;
    }

    private static void Detail(string record) => Diagnostics.AppLog.Detail("updates", record);

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 128, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool VerifyAuthenticode(string installerPath)
    {
        var currentExe = Environment.ProcessPath;
        if (currentExe is not { Length: > 0 } executable || !File.Exists(executable)
            || !VerifyTrust(installerPath) || !VerifyTrust(executable))
            return false;
        try
        {
            // Authenticode certificates are embedded in PE files, not certificate files.
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the platform PE Authenticode extractor.
            using var installerCert = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
            using var currentCert = new X509Certificate2(X509Certificate.CreateFromSignedFile(executable));
#pragma warning restore SYSLIB0057
            return HasCodeSigningEku(installerCert) && HasCodeSigningEku(currentCert)
                && string.Equals(installerCert.Thumbprint, currentCert.Thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException) { return false; }
    }

    private static bool HasCodeSigningEku(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } usage
        && usage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningEku);

    private static bool VerifyTrust(string fileName)
    {
        var fileInfo = new WinTrustFileInfo(fileName);
        var data = new WinTrustData(fileInfo);
        try
        {
            var trust = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, data);
            // Self-signed releases have one permitted trust failure. Digest/signature, timestamp,
            // EKU and signer identity are checked separately; all other policy failures reject.
            if (trust != 0 && trust != CertEUntrustedRoot) return false;
            var providerData = WTHelperProvDataFromStateData(data.hWVTStateData);
            var signer = providerData == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
            return signer != IntPtr.Zero && Marshal.PtrToStructure<CryptProviderSigner>(signer).CounterSignerCount > 0;
        }
        finally
        {
            data.Close();
            data.Dispose();
            fileInfo.Dispose();
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, [In, Out] WinTrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint StructSize;
        public FILETIME VerifyAsOf;
        public uint CertificateChainCount;
        public IntPtr CertificateChain;
        public uint SignerType;
        public IntPtr Signer;
        public uint Error;
        public uint CounterSignerCount;
        public IntPtr CounterSigners;
        public uint CounterSignerType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        public IntPtr pcwszFilePath;
        public IntPtr hFile = IntPtr.Zero;
        public IntPtr pgKnownSubject = IntPtr.Zero;
        public WinTrustFileInfo(string path) => pcwszFilePath = Marshal.StringToCoTaskMemUni(path);
        public void Dispose() { if (pcwszFilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(pcwszFilePath); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData : IDisposable
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WinTrustData>();
        public IntPtr pPolicyCallbackData = IntPtr.Zero;
        public IntPtr pSIPClientData = IntPtr.Zero;
        public uint dwUIChoice = 2;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice = 1;
        public IntPtr pFile;
        public uint dwStateAction = 1;
        public IntPtr hWVTStateData = IntPtr.Zero;
        public IntPtr pwszURLReference = IntPtr.Zero;
        public uint dwProvFlags = 0x00000040;
        public uint dwUIContext;
        public WinTrustData(WinTrustFileInfo fileInfo)
        {
            pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, pFile, false);
        }
        public void Close()
        {
            if (hWVTStateData == IntPtr.Zero) return;
            dwStateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, this);
            hWVTStateData = IntPtr.Zero;
        }
        public void Dispose() { if (pFile != IntPtr.Zero) Marshal.FreeCoTaskMem(pFile); }
    }
}
