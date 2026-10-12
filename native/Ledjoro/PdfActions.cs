using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Save an invoice or credit memo as a PDF, and start an email to the customer to send it with.</summary>
    static class PdfActions
    {
        /// <summary>Where the last PDF was written (used by the self-test, which never opens a save window).</summary>
        public static string LastPath = "";

        public static byte[] Make(Jv d, bool credit, Jv profile, Jv contact) { return InvoicePdf.Build(d, credit, profile, contact); }

        public static string FileName(Jv d, bool credit)
        {
            string s = (credit ? "Credit memo " : "Invoice ") + d["number"].Str + " - " + d["contact_name"].Str;
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Trim() + ".pdf";
        }

        public static async Task Save(Jv d, bool credit, bool thenEmail)
        {
            var profile = await App.Api.Get("api/profile"); var contacts = await Lists.Contacts();
            var contact = contacts.Items.FirstOrDefault(c => c["id"].Long == d["contact_id"].Long);
            byte[] pdf = Make(d, credit, profile, contact);
            string path;
            if (App.SelfTesting) path = Path.Combine(Path.GetTempPath(), "ledjoro-test-" + FileName(d, credit));
            else
            {
                using (var dlg = new SaveFileDialog { Title = "Save as PDF", Filter = "PDF file (*.pdf)|*.pdf", FileName = FileName(d, credit), OverwritePrompt = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
                {
                    if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            }
            try { File.WriteAllBytes(path, pdf); }
            catch (Exception ex) { Msg.Info("The PDF could not be saved", "Windows would not let Ledjoro write the file: " + Say.Plain(ex) + " Try a different folder.", true); return; }
            LastPath = path;
            if (App.SelfTesting) return;
            App.Toast("Saved " + Path.GetFileName(path));
            if (thenEmail)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch (Exception) { }
                string company = profile["name"].Str.Length > 0 ? profile["name"].Str : App.Api.Company;
                string to = contact == null || contact.IsNull ? "" : (contact["email"].Str ?? "");
                string what = credit ? "credit memo " + d["number"].Str : "invoice " + d["number"].Str;
                string due = credit ? "" : " It is due on " + Fmt.Show(d["due_date"].Str) + ".";
                string body = "Hello,\r\n\r\nPlease find " + what + " for " + Fmt.Money(d["total_cents"].Long) + " attached." + due + "\r\n\r\nThank you,\r\n" + company;
                string url = "mailto:" + Uri.EscapeDataString(to) + "?subject=" + Uri.EscapeDataString((credit ? "Credit memo " : "Invoice ") + d["number"].Str + " from " + company) + "&body=" + Uri.EscapeDataString(body);
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { Msg.Info("No email program opened", "Windows did not open an email program. The PDF is saved at " + path + "; attach it to an email yourself.", true); return; }
                Msg.Info("Attach the PDF to the email", "Ledjoro opened a new email and showed you the PDF in its folder. Drag the PDF onto the email to attach it, then send.");
            }
            else
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch (Exception) { }
            }
        }
    }
}
