using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>"How to Download From Your Bank": general steps that fit almost every bank, and a list of popular banks with their own help pages.</summary>
    static class BankHelp
    {
        sealed class Bank { public string Name, Note, Url; public Bank(string n, string note, string url) { Name = n; Note = note; Url = url; } }

        // Official help pages found for each bank (checked when this list was written; banks do move their pages, so a search link is offered for the rest).
        static readonly Bank[] Banks =
        {
            new Bank("Bank of America", "Quicken and QuickBooks downloads, with the Web Connect steps", "https://www.bankofamerica.com/online-banking/online-financial-management-faqs/"),
            new Bank("Wells Fargo (personal)", "Download Account Activity and Web Connect", "https://www.wellsfargo.com/online-banking/software/web-connect/"),
            new Bank("Wells Fargo (business)", "QuickBooks Web Connect for Business Online", "https://www.wellsfargo.com/biz/online-banking/software/quickbooks/webconnect/"),
            new Bank("U.S. Bank", "How to download your transaction history (CSV, QFX or QBO)", "https://www.usbank.com/customer-service/knowledge-base/KB0069323.html"),
            new Bank("PNC", "Online banking with Quicken and QuickBooks, including QFX and QBO files", "https://www.pnc.com/en/personal-banking/banking/online-and-mobile-banking/online-banking-with-quicken.html"),
            new Bank("Regions", "How to download Web Connect files", "https://www.regions.com/help/online-banking-help/quickbooks/how-do-i-download-regions-bank-web-connect"),
            new Bank("American Express", "Download transactions and statements for QuickBooks and Quicken", "https://www.americanexpress.com/us/customer-service/faq.download-export-transactions-software.html"),
            new Bank("Capital One (business)", "Capital One Business and QuickBooks", "https://www.capitalone.com/learn-grow/business-resources/capital-one-business-and-quickbooks-integration/"),
            new Bank("TD Bank", "Using Quicken and QuickBooks with online banking", "https://www.tdbank.com/PFMAccess/"),
            new Bank("Fifth Third", "Quicken and QuickBooks questions and answers", "https://www.53.com/content/fifth-third/en/customer-service/quicken-quickbooks-faqs.html"),
            new Bank("Ally", "Account information help, including downloading activity", "https://www.ally.com/help/bank/account-information/"),
            new Bank("Chase", "Search for Chase's download instructions", null),
            new Bank("Citi", "Search for Citi's download instructions", null),
            new Bank("Truist", "Search for Truist's download instructions", null),
            new Bank("Navy Federal", "Search for Navy Federal's download instructions", null),
            new Bank("USAA", "Search for USAA's download instructions", null),
            new Bank("Discover", "Search for Discover's download instructions", null),
        };

        public static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { Msg.Info("The page could not be opened", "Windows did not open your web browser. You can search for the bank's name and “download transactions QBO” yourself."); }
        }
        public static string SearchUrl(string bank) { return "https://www.google.com/search?q=" + Uri.EscapeDataString(bank + " download transactions QFX OR QBO OR OFX for QuickBooks"); }
        public static string UrlFor(string name) { var b = Banks.FirstOrDefault(x => x.Name == name); return b == null ? null : (b.Url ?? SearchUrl(b.Name)); }
        public static int Count { get { return Banks.Length; } }

        public static void Show()
        {
            var d = new Dlg("How to Download From Your Bank", 760);
            var page = new ScrollPage(); page.Body.Padding = new Padding(0, 0, Theme.Px(6), 0); page.Body.Gap = Theme.Px(10); page.Body.Fill = Pnl.K.Surface; page.Fill = Pnl.K.Surface;
            var s = page.Body;
            s.Add(new Lbl("The steps are almost the same at every bank", LS.H3));
            s.Add(new Lbl("1.  Sign in to your bank's website (the app often does not offer downloads).\n2.  Open the account, then look for a button or link called Download, Export, Download Transactions or Download Account Activity.\n3.  Pick a date range. Pick the file type: choose QuickBooks (QBO), Quicken (QFX) or Web Connect / OFX. Avoid CSV if there is a choice.\n4.  Save the file into your Ledjoro statements folder. Ledjoro notices it within a minute and offers to import it.", LS.Normal, true));
            s.Add(new Lbl("Tips", LS.H3));
            s.Add(new Lbl("•  Credit cards and savings accounts usually have the same download button as checking.\n•  Start the next download the day after your last one ends. Ledjoro skips transactions it already has, but a tidy range is easier to follow.\n•  If the only choice is CSV, save it anywhere, then use Choose File and Match Columns Myself on Import & Convert.\n•  Some banks offer a direct connection to QuickBooks. Ledjoro does not use those, but the same bank's download button works.", LS.Normal, true));
            s.Add(new Lbl("Your bank's own instructions", LS.H3));
            s.Add(new Lbl("These open the bank's own help page in your web browser. Banks change their pages and menus from time to time, so if something does not match, use the search link or ask the bank.", LS.Muted, true));
            foreach (var b in Banks)
            {
                var row = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(10) }; var bank = b;
                var link = new Lbl(b.Name, LS.Link) { Underline = true }; link.Click += (x, e) => Open(bank.Url ?? SearchUrl(bank.Name));
                row.Add(link); row.Add(new Lbl("—  " + b.Note, LS.Muted)); s.Add(row, Theme.Px(2));
            }
            var other = new Lbl("Another bank? Search the web for its instructions", LS.Link) { Underline = true }; other.Click += (x, e) => Open(SearchUrl("your bank name"));
            s.Add(other, Theme.Px(10));
            d.Body.Add(page, null, Theme.Px(380));
            d.Primary("Close", () => Task.FromResult(true)); d.Shown += (x, e) => { page.ScrollPos = 0; }; d.Run();
        }
    }
}
