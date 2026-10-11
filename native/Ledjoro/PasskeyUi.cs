using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>The passkey steps shared by the sign-in screen and My account: ask the engine for a challenge, let Windows answer it, give the answer back.</summary>
    static class PasskeyFlow
    {
        static IntPtr Owner() { var f = Form.ActiveForm; return f != null ? f.Handle : App.Main.Handle; }

        /// <summary>Sign in with a passkey (no username or password). Throws PasskeyException (Cancelled when the person closes the Windows prompt) or ApiException.</summary>
        public static async Task SignIn()
        {
            var o = await App.Api.Post("api/passkeys/login/options");
            var a = await WebAuthn.Get(Owner(), o["rpId"].Str, o["origin"].Str, o["challenge"].Str);
            await App.Api.Post("api/passkeys/login/finish", Json.O(
                "challengeId", o["challengeId"].Str, "credentialId", WebAuthn.B64u(a.CredentialId), "clientDataJSON", WebAuthn.B64u(a.ClientData),
                "authenticatorData", WebAuthn.B64u(a.AuthenticatorData), "signature", WebAuthn.B64u(a.Signature), "userHandle", a.UserHandle.Length > 0 ? WebAuthn.B64u(a.UserHandle) : null));
            await App.Main.Boot();
        }

        /// <summary>Make a new passkey for the signed-in user (needs their current password).</summary>
        public static async Task Add(string password, string name)
        {
            var o = await App.Api.Post("api/passkeys/register/options", Json.O("password", password));
            var u = o["user"];
            var made = await WebAuthn.Create(Owner(), o["rpId"].Str, o["rpName"].Str, o["origin"].Str, o["challenge"].Str, WebAuthn.FromB64u(u["handle"].Str), u["name"].Str, u["displayName"].Str);
            await App.Api.Post("api/passkeys/register/finish", Json.O("challengeId", o["challengeId"].Str, "clientDataJSON", WebAuthn.B64u(made.ClientData), "attestationObject", WebAuthn.B64u(made.AttestationObject), "name", name));
        }
    }

    /// <summary>My account > Passkeys: the passkeys this person has, add one, remove one.</summary>
    sealed class PasskeyCard : Card
    {
        readonly Grid grid = new Grid { AutoHeight = true }; readonly Lbl none = new Lbl("You have no passkeys yet.", LS.Muted);
        readonly Input name = new Input("for example: Office laptop", 300), pw = new Input("", 300) { Password = true }; readonly Notice err = new Notice(NK.Error); Btn removeBtn;

        public PasskeyCard()
        {
            Add(new Lbl("Passkeys", LS.H3), Theme.Px(6));
            Add(new Lbl("A passkey lets you sign in with Windows Hello (your face, fingerprint or PIN), a phone, or a security key, with no password to type. Your password and recovery key keep working as a backup.", LS.Muted, true), Theme.Px(10));
            if (!WebAuthn.Available) { Add(new Notice(NK.Warn, "This version of Windows cannot use passkeys. It needs Windows 10 version 1903 or newer."), 0); return; }
            grid.Cols(Grid.Col("Name", 2.4f), Grid.Col("Added", 1.5f), Grid.Col("Last used", 1.5f));
            Add(none, Theme.Px(4)); Add(grid, Theme.Px(8));
            removeBtn = Add(Ui.BA("Remove Selected Passkey", BK.Danger, Remove, true), Theme.Px(14));
            Add(Ui.F("Name this passkey", name, 300), Theme.Px(8));
            Add(Ui.F("Your current password (to confirm it is you)", pw, 300), Theme.Px(8));
            Add(err, Theme.Px(6));
            Add(Ui.BA("Add a Passkey", BK.Primary, AddOne), 0);
            Ui.Run(Refresh);
        }

        async Task Refresh()
        {
            var rows = await App.Api.Get("api/passkeys");
            grid.Clear();
            foreach (var p in rows.Items)
                grid.Add(p["id"].Long, p["name"].Str, Fmt.FromUnixMs(p["created_at"].Long).ToLocalTime().ToString("d"), p["last_used_at"].Long > 0 ? Fmt.FromUnixMs(p["last_used_at"].Long).ToLocalTime().ToString("g") : "never");
            none.Visible = rows.Count == 0; grid.Visible = removeBtn.Visible = rows.Count > 0;
            Vx.Relayout(this);
        }

        async Task AddOne()
        {
            err.Reset();
            if (pw.Text.Length == 0) { err.Set("Type your current password first."); return; }
            try { await PasskeyFlow.Add(pw.Text, name.Text.Trim().Length > 0 ? name.Text.Trim() : "Passkey"); pw.Text = ""; name.Text = ""; App.Toast("Passkey added"); await Refresh(); }
            catch (PasskeyException e) { if (!e.Cancelled) err.Set(e.Message); }
            catch (ApiException e) { err.Set(e.Message); }
        }

        async Task Remove()
        {
            err.Reset();
            var p = grid.SelectedTag; if (p == null) { App.Toast("Click a passkey in the list first."); return; }
            if (!Msg.Confirm("Remove this passkey?", "You will no longer be able to sign in with it. Your password still works.", "Remove", true)) return;
            try { await App.Api.Delete("api/passkeys/" + p); App.Toast("Passkey removed"); await Refresh(); }
            catch (ApiException e) { err.Set(e.Message); }
        }
    }
}
