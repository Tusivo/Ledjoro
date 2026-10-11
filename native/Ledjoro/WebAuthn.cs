using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Ledjoro
{
    /// <summary>Raised for anything that goes wrong while talking to Windows about passkeys. Cancelled = the person closed the Windows prompt.</summary>
    public sealed class PasskeyException : Exception
    {
        public readonly bool Cancelled;
        public PasskeyException(string message, bool cancelled = false) : base(message) { Cancelled = cancelled; }
    }

    public sealed class NewPasskey { public byte[] CredentialId, AttestationObject, ClientData; }
    public sealed class PasskeyAnswer { public byte[] CredentialId, AuthenticatorData, Signature, UserHandle, ClientData; }

    /// <summary>
    /// Passkeys through the Windows WebAuthn API (webauthn.dll, the same component browsers use): Windows shows its own prompt (Windows Hello face /
    /// fingerprint / PIN, a phone, or a security key). This class only relays what the authenticator produces; the engine checks it all.
    /// Requires Windows 10 version 1903 or newer; on anything older <see cref="Available"/> is false and the rest of the app is unaffected.
    /// </summary>
    public static class WebAuthn
    {
        const string Dll = "webauthn.dll";

        [DllImport(Dll)] static extern uint WebAuthNGetApiVersionNumber();
        [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int WebAuthNAuthenticatorMakeCredential(IntPtr hWnd, ref RpInfo rp, ref UserInfo user, ref CoseParams pubKeyCredParams, ref ClientDataStruct clientData, ref MakeOptions options, out IntPtr attestation);
        [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int WebAuthNAuthenticatorGetAssertion(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string rpId, ref ClientDataStruct clientData, ref GetOptions options, out IntPtr assertion);
        [DllImport(Dll)] static extern void WebAuthNFreeCredentialAttestation(IntPtr attestation);
        [DllImport(Dll)] static extern void WebAuthNFreeAssertion(IntPtr assertion);

        [StructLayout(LayoutKind.Sequential)] struct RpInfo { public uint Version; public IntPtr Id, Name, Icon; }
        [StructLayout(LayoutKind.Sequential)] struct UserInfo { public uint Version; public uint IdLen; public IntPtr Id, Name, Icon, DisplayName; }
        [StructLayout(LayoutKind.Sequential)] struct CoseParam { public uint Version; public IntPtr Type; public int Alg; }
        [StructLayout(LayoutKind.Sequential)] struct CoseParams { public uint Count; public IntPtr Items; }
        [StructLayout(LayoutKind.Sequential)] struct ClientDataStruct { public uint Version; public uint Len; public IntPtr Json; public IntPtr HashAlg; }
        [StructLayout(LayoutKind.Sequential)] struct CountPtr { public uint Count; public IntPtr Items; }
        [StructLayout(LayoutKind.Sequential)]
        struct MakeOptions { public uint Version, TimeoutMs; public CountPtr Exclude, Extensions; public uint Attachment; public int RequireResidentKey; public uint UserVerification, Attestation, Flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct GetOptions { public uint Version, TimeoutMs; public CountPtr Allow, Extensions; public uint Attachment, UserVerification, Flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct Attestation
        {
            public uint Version; public IntPtr FormatType; public uint AuthDataLen; public IntPtr AuthData; public uint AttestationLen; public IntPtr AttestationData;
            public uint DecodeType; public IntPtr Decode; public uint AttestationObjectLen; public IntPtr AttestationObject; public uint CredentialIdLen; public IntPtr CredentialId;
        }
        [StructLayout(LayoutKind.Sequential)] struct CredentialInfo { public uint Version; public uint IdLen; public IntPtr Id; public IntPtr Type; }
        [StructLayout(LayoutKind.Sequential)]
        struct Assertion { public uint Version; public uint AuthDataLen; public IntPtr AuthData; public uint SignatureLen; public IntPtr Signature; public CredentialInfo Credential; public uint UserIdLen; public IntPtr UserId; }

        const uint UserVerificationRequired = 1, AttestationNone = 1, AnyAttachment = 0;
        public const uint TimeoutMs = 120000;

        /// <summary>True when this Windows can make and use passkeys.</summary>
        public static bool Available
        {
            get { try { return WebAuthNGetApiVersionNumber() >= 1; } catch (Exception) { return false; } }
        }
        public static uint ApiVersion { get { try { return WebAuthNGetApiVersionNumber(); } catch (Exception) { return 0; } } }

        public static string B64u(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        public static byte[] FromB64u(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/'); while (s.Length % 4 != 0) s += "=";
            return Convert.FromBase64String(s);
        }
        static string ClientDataJson(string type, string challenge, string origin) { return "{\"type\":\"" + type + "\",\"challenge\":\"" + challenge + "\",\"origin\":\"" + origin + "\",\"crossOrigin\":false}"; }

        static IntPtr Str(string s) { return Marshal.StringToHGlobalUni(s); }
        static IntPtr Bytes(byte[] b) { var p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); return p; }
        static byte[] Read(IntPtr p, uint len) { if (p == IntPtr.Zero || len == 0) return new byte[0]; var b = new byte[len]; Marshal.Copy(p, b, 0, (int)len); return b; }

        static PasskeyException Fail(int hr)
        {
            uint u = unchecked((uint)hr);
            if (u == 0x80090036 || u == 0x800704C7 || u == 0x800705B4) return new PasskeyException("Cancelled.", true);   // user cancelled / timed out
            if (u == 0x80090029 || u == 0x80070032) return new PasskeyException("This computer or key cannot make passkeys that require your fingerprint, face or PIN.");
            if (u == 0x8009000F) return new PasskeyException("That passkey is already on this device.");
            if (u == 0x80070057) return new PasskeyException("Windows could not understand the passkey request (0x80070057).");
            return new PasskeyException("Windows could not use a passkey (error 0x" + u.ToString("X8") + ").");
        }

        /// <summary>Make a new passkey. Windows shows its prompt, so this is run off the screen's thread. Returns what the engine needs to store it.</summary>
        public static Task<NewPasskey> Create(IntPtr hwnd, string rpId, string rpName, string origin, string challenge, byte[] userHandle, string userName, string displayName)
        {
            return Task.Run(() =>
            {
                var alloc = new System.Collections.Generic.List<IntPtr>();
                Func<string, IntPtr> S = s => { var p = Str(s); alloc.Add(p); return p; };
                IntPtr att = IntPtr.Zero;
                try
                {
                    var cd = Encoding.UTF8.GetBytes(ClientDataJson("webauthn.create", challenge, origin));
                    var rp = new RpInfo { Version = 1, Id = S(rpId), Name = S(rpName), Icon = IntPtr.Zero };
                    var idp = Bytes(userHandle); alloc.Add(idp);
                    var user = new UserInfo { Version = 1, IdLen = (uint)userHandle.Length, Id = idp, Name = S(userName), Icon = IntPtr.Zero, DisplayName = S(displayName) };
                    // ES256 first (most keys), RS256 second (Windows Hello with a TPM)
                    var one = Marshal.SizeOf(typeof(CoseParam)); var arr = Marshal.AllocHGlobal(one * 2); alloc.Add(arr);
                    Marshal.StructureToPtr(new CoseParam { Version = 1, Type = S("public-key"), Alg = -7 }, arr, false);
                    Marshal.StructureToPtr(new CoseParam { Version = 1, Type = S("public-key"), Alg = -257 }, arr + one, false);
                    var prm = new CoseParams { Count = 2, Items = arr };
                    var cdp = Bytes(cd); alloc.Add(cdp);
                    var cds = new ClientDataStruct { Version = 1, Len = (uint)cd.Length, Json = cdp, HashAlg = S("SHA-256") };
                    var opt = new MakeOptions { Version = 1, TimeoutMs = TimeoutMs, Attachment = AnyAttachment, RequireResidentKey = 1, UserVerification = UserVerificationRequired, Attestation = AttestationNone };
                    int hr = WebAuthNAuthenticatorMakeCredential(hwnd, ref rp, ref user, ref prm, ref cds, ref opt, out att);
                    if (hr != 0 || att == IntPtr.Zero) throw Fail(hr);
                    var a = (Attestation)Marshal.PtrToStructure(att, typeof(Attestation));
                    return new NewPasskey { CredentialId = Read(a.CredentialId, a.CredentialIdLen), AttestationObject = Read(a.AttestationObject, a.AttestationObjectLen), ClientData = cd };
                }
                finally
                {
                    if (att != IntPtr.Zero) WebAuthNFreeCredentialAttestation(att);
                    foreach (var p in alloc) Marshal.FreeHGlobal(p);
                }
            });
        }

        /// <summary>Sign in with a passkey. No username is needed: Windows lists the passkeys it has for this program.</summary>
        public static Task<PasskeyAnswer> Get(IntPtr hwnd, string rpId, string origin, string challenge, uint timeoutMs = TimeoutMs)
        {
            return Task.Run(() =>
            {
                var alloc = new System.Collections.Generic.List<IntPtr>();
                IntPtr asr = IntPtr.Zero;
                try
                {
                    var cd = Encoding.UTF8.GetBytes(ClientDataJson("webauthn.get", challenge, origin));
                    var cdp = Bytes(cd); alloc.Add(cdp); var alg = Str("SHA-256"); alloc.Add(alg);
                    var cds = new ClientDataStruct { Version = 1, Len = (uint)cd.Length, Json = cdp, HashAlg = alg };
                    var opt = new GetOptions { Version = 1, TimeoutMs = timeoutMs, Attachment = AnyAttachment, UserVerification = UserVerificationRequired };
                    int hr = WebAuthNAuthenticatorGetAssertion(hwnd, rpId, ref cds, ref opt, out asr);
                    if (hr != 0 || asr == IntPtr.Zero) throw Fail(hr);
                    var a = (Assertion)Marshal.PtrToStructure(asr, typeof(Assertion));
                    return new PasskeyAnswer { CredentialId = Read(a.Credential.Id, a.Credential.IdLen), AuthenticatorData = Read(a.AuthData, a.AuthDataLen), Signature = Read(a.Signature, a.SignatureLen), UserHandle = Read(a.UserId, a.UserIdLen), ClientData = cd };
                }
                finally
                {
                    if (asr != IntPtr.Zero) WebAuthNFreeAssertion(asr);
                    foreach (var p in alloc) Marshal.FreeHGlobal(p);
                }
            });
        }

        /// <summary>Sizes of the structures handed to Windows (checked by the self-test: a wrong layout would make Windows reject or misread the request).</summary>
        public static string LayoutReport()
        {
            return "RP " + Marshal.SizeOf(typeof(RpInfo)) + ", user " + Marshal.SizeOf(typeof(UserInfo)) + ", cose " + Marshal.SizeOf(typeof(CoseParam)) + ", clientdata " + Marshal.SizeOf(typeof(ClientDataStruct))
                + ", make " + Marshal.SizeOf(typeof(MakeOptions)) + ", get " + Marshal.SizeOf(typeof(GetOptions)) + ", attestation " + Marshal.SizeOf(typeof(Attestation)) + ", assertion " + Marshal.SizeOf(typeof(Assertion)) + ", pointer " + IntPtr.Size;
        }
    }
}
