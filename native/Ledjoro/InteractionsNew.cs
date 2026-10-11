using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Self-test steps for reconciliation, matching, automatic backups and search.</summary>
    static class InteractionsNew
    {
        public static async Task Run(MainForm form, Action<string> note, Func<string, Task> shot, Action<bool, string> check)
        {
            var api = App.Api;

            // ---- dates follow the PC's regional settings; the books keep yyyy-MM-dd
            {
                var keep = System.Globalization.CultureInfo.CurrentCulture;
                try
                {
                    foreach (var c in new[] { new[] { "en-US", "3/31/2026" }, new[] { "de-DE", "31.03.2026" }, new[] { "en-GB", "31/03/2026" }, new[] { "ja-JP", "2026/03/31" } })
                    {
                        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(c[0]);
                        DateTime d;
                        check(Fmt.Show("2026-03-31") == c[1] && Fmt.TryLocalDate(c[1], out d) && Fmt.Date(d) == "2026-03-31" && Fmt.ToIso(c[1]) == "2026-03-31", "dates show and read as " + c[0] + " writes them (" + c[1] + ") and are stored as 2026-03-31");
                    }
                    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                    var di = new DateInput("2026-01-05"); check(di.Tb.Text == "05.01.2026" && di.Value == "2026-01-05" && di.Valid, "a date box shows the PC's format but hands the books yyyy-MM-dd");
                    di.Text = "2026-02-03"; check(di.Tb.Text == "03.02.2026", "setting a stored date shows it the local way"); di.Tb.Text = "31.12.2026"; check(di.Value == "2026-12-31", "typing it the local way is understood"); di.Dispose();
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = keep; }
            }

            // ---- the converter link on the Import page
            {
                await form.GoAsync("#/import"); await Drive.Settle(500);
                var imp0 = Drive.Find<ImportScreen>(form.Content);
                check(imp0 != null && Drive.All<Btn>(imp0).Any(b => b.Kind == BK.Promo), "the Import page has one converter button inside the Choose a file card");
                await shot("48-import-converter-tip");
                Drive.All<Btn>(imp0).First(b => b.Kind == BK.Promo).Post(); var info = await Drive.WaitDialog();
                check(info != null && Drive.Button(info, "Visit the converter's Page") != null && Drive.All<Lbl>(info).Any(l => l.Text.Contains("worked tirelessly")), "the converter button opens a page about the converter with the link button");
                await shot("48b-import-converter-info"); if (info != null) { Drive.Button(info, "Close").PerformClick(); await Drive.WaitClosed(info); }
                var spI = Drive.All<ScrollPage>(form.Content).FirstOrDefault(); if (spI != null) { spI.ScrollPos = 0; await Drive.Settle(250); }
                await shot("49-import-converter-link"); string was0 = Theme.Name; foreach (var tn in new[] { "light", "night", "dark" }) { Theme.Use(tn); await Drive.Settle(400); await shot("49-import-converter-" + tn); } Theme.Use(was0); await Drive.Settle(300);
            }

            // ---- Sales Tax ------------------------------------------------------------------------------------------------
            {
                await form.GoAsync("#/salestax"); await Drive.Settle(500);
                var st0 = Drive.Find<SalesTaxScreen>(form.Content);
                check(st0 != null && Drive.Button(st0, "New Tax Rate") != null && Drive.Button(st0, "New Combined Rate") != null, "the Sales Tax page opens with New Tax Rate and New Combined Rate");
                check(Drive.All<Lbl>(st0).Any(l => l.Text.StartsWith("Charging sales tax")), "with no rates yet the page explains how sales tax works");
                await shot("70-salestax-empty");
                Drive.Button(st0, "New Tax Rate").Post(); var rd = await Drive.WaitDialog(); check(rd != null, "New Tax Rate opens a dialog");
                if (rd != null)
                {
                    Drive.Find<Input>(rd, i => i.Cue == "State sales tax").Text = "State"; Drive.Find<Input>(rd, i => i.Cue == "6.25").Text = "4"; Drive.Find<Input>(rd, i => i.Cue == "State Department of Revenue").Text = "State Revenue";
                    Drive.Button(rd, "Save").PerformClick(); await Drive.WaitClosed(rd);
                }
                await api.Post("api/salestax/rates", Json.O("name", "County", "rate_pct", "2.5", "agency", "County Treasurer"));
                await form.GoAsync("#/salestax"); await Drive.Settle(400);
                var st1 = Drive.Find<SalesTaxScreen>(form.Content);
                Drive.Button(st1, "New Combined Rate").Post(); var gd = await Drive.WaitDialog();
                check(gd != null && Drive.All<Check>(gd).Count(c => c.Text.StartsWith("State") || c.Text.StartsWith("County")) == 2, "a combined rate offers the single rates to tick");
                if (gd != null)
                {
                    Drive.Find<Input>(gd, i => i.Cue.StartsWith("Springfield")).Text = "Springfield";
                    foreach (var c in Drive.All<Check>(gd).Where(c => c.Text.StartsWith("State") || c.Text.StartsWith("County"))) c.Checked = true;
                    Drive.All<Check>(gd).First(c => c.Text.StartsWith("Use this rate")).Checked = true;
                    Drive.Button(gd, "Save").PerformClick(); await Drive.WaitClosed(gd);
                }
                var rates = await api.Get("api/salestax/rates"); var combo = rates["rates"].Items.FirstOrDefault(r => r["name"].Str == "Springfield");
                check(rates["rates"].Count == 3 && combo != null && combo["rate_milli"].Long == 6500 && rates["default_id"].Long == combo["id"].Long, "the rates were saved: State 4%, County 2.5% and Springfield 6.5% as the default");
                await form.GoAsync("#/salestax"); await Drive.Settle(500);
                check(Drive.All<Grid>(form.Content).Any(g => g.Rows.Count == 3), "the rates are listed");
                await shot("71-salestax-rates");

                // customers: one exempt
                await api.Post("api/contacts", Json.O("kind", "customer", "name", "Tax Test Buyer"));
                await form.GoAsync("#/customers"); await Drive.Settle(400);
                Drive.Button(form.Content, "New Customer").Post(); var cd = await Drive.WaitDialog();
                check(cd != null && Drive.All<Check>(cd).Any(c => c.Text.StartsWith("Tax-exempt customer")), "the customer window has a tax-exempt box");
                if (cd != null)
                {
                    Drive.Find<Input>(cd, i => i.Cue.StartsWith("Business or display name")).Text = "Exempt School";
                    Drive.All<Check>(cd).First(c => c.Text.StartsWith("Tax-exempt customer")).Checked = true; Drive.Find<Input>(cd, i => i.Cue.StartsWith("Exemption certificate")).Text = "ST-4411";
                    await shot("72-customer-tax-exempt"); Drive.Button(cd, "Save").PerformClick(); await Drive.WaitClosed(cd);
                }
                var cl = await api.Get("api/contacts?kind=customer"); var buyer = cl.Items.First(c => c["name"].Str == "Tax Test Buyer"); var school = cl.Items.First(c => c["name"].Str == "Exempt School");
                check(school["tax_exempt"].Long == 1 && school["tax_exempt_note"].Str == "ST-4411", "the exempt customer was saved with its certificate note");

                // the invoice form: tax rate, taxable ticks, live totals
                await form.GoAsync("#/invoices/new"); await Drive.Settle(600);
                var combos = Drive.All<Combo>(form.Content).ToList(); var custBox = combos[0]; var taxBox = combos.First(c => c.Items.Any(i => i.Value == "No sales tax"));
                check(taxBox.Value == combo["id"].Str, "the default tax rate is chosen on a new invoice");
                custBox.Value = buyer["id"].Str; await Drive.Settle(150);
                Drive.Field(form.Content, "Description").Text = "Taxed goods"; Drive.Find<Input>(form.Content, i => i.Cue == "0.00").Text = "200.00"; await Drive.Settle(200);
                check(Drive.All<Lbl>(form.Content).Any(l => l.Text == "$13.00") && Drive.All<Lbl>(form.Content).Any(l => l.Text == "$213.00"), "the form shows tax of $13.00 (6.5% of $200.00) and a total of $213.00");
                var tick = Drive.All<Check>(form.Content).First(c => c.AccessibleName == "Charge sales tax on this line"); tick.Checked = false; await Drive.Settle(150);
                check(Drive.All<Lbl>(form.Content).Count(l => l.Text == "$200.00") >= 1 && Drive.All<Lbl>(form.Content).Count(l => l.Text == "$0.00") >= 1, "unticking Tax on the line takes the tax off");
                tick.Checked = true; await Drive.Settle(150); await shot("73-invoice-with-tax");
                custBox.Value = school["id"].Str; await Drive.Settle(200);
                check(taxBox.Value == "" && Drive.All<Lbl>(form.Content).Any(l => l.Text.StartsWith("This customer is tax-exempt")), "choosing a tax-exempt customer removes the tax and says why");
                custBox.Value = buyer["id"].Str; await Drive.Settle(200);
                Drive.Button(form.Content, "Save").PerformClick();
                bool savedTax = await Drive.Until(() => form.CurrentHash.StartsWith("#/document/")); check(savedTax, "the taxed invoice saves");
                await Drive.Settle(600);
                var taxed = (await api.Get("api/documents?kind=invoice")).Items.OrderByDescending(d => d["id"].Long).First();
                check(taxed["total_cents"].Long == 21300 && taxed["tax_cents"].Long == 1300, "the invoice was saved with $13.00 of sales tax");
                check(Drive.All<Grid>(form.Content).Any(g => g.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[0].Value != null && r.Cells[0].Value.ToString().StartsWith("Sales tax: County"))), "the invoice shows each tax line");
                await shot("74-invoice-detail-tax");
                await api.Post("api/documents", Json.O("kind", "invoice", "contact_id", school["id"].Long, "doc_date", Fmt.Today(), "tax_rate_id", combo["id"].Long,
                    "lines", new[] { Json.O("description", "Supplies", "qty_milli", 1000, "unit_price_cents", 50000, "account_id", (await Lists.Accounts()).Items.First(a => a["name"].Str == "Sales")["id"].Long, "taxable", true) }));

                // the worksheet
                await form.GoAsync("#/salestax/worksheet"); await Drive.Settle(700);
                var ws = Drive.Find<SalesTaxScreen>(form.Content);
                var pre = Drive.All<Combo>(ws).First(c => c.Items.Any(i => i.Key == "last-quarter")); pre.Value = "this-year"; await Drive.Settle(900);
                check(Drive.All<Lbl>(ws).Any(l => l.Text.StartsWith("Sales Tax Return Worksheet")), "the Return Worksheet opens with its period");
                check(Drive.All<Grid>(ws).Any(g => g.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[0].Value != null && r.Cells[0].Value.ToString() == "Exempt sales (to tax-exempt customers)" && r.Cells[1].Value.ToString() == "$500.00")), "exempt sales to the school ($500.00) are listed");
                check(Drive.All<Grid>(ws).Any(g => g.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[0].Value != null && r.Cells[0].Value.ToString() == "Tax due for this period" && r.Cells[1].Value.ToString() == "$13.00")), "the worksheet finds $13.00 of tax due");
                check(Drive.Button(ws, "Print") != null && Drive.Button(ws, "Record a Payment to the Agency") != null, "the worksheet can be printed and a payment recorded from it");
                await shot("75-salestax-worksheet");
                Drive.Button(ws, "Record a Payment to the Agency").Post(); var pd = await Drive.WaitDialog(); check(pd != null, "Record a Payment opens a dialog");
                if (pd != null)
                {
                    var banks = (await Lists.Accounts()).Items.First(a => a["subtype"].Str == "bank");
                    Drive.All<Combo>(pd).First(c => c.Items.Any(i => i.Key == banks["id"].Str)).Value = banks["id"].Str; await shot("76-salestax-payment");
                    Drive.Button(pd, "Save Payment").PerformClick(); await Drive.WaitClosed(pd);
                }
                await Drive.Settle(500);
                var pays = await api.Get("api/salestax/payments");
                check(pays.Count == 1 && pays[0]["amount_cents"].Long == 1300, "the payment to the agency was recorded for the tax due");
                check(form.CurrentHash == "#/salestax/payments" && Drive.All<Grid>(form.Content).Any(g => g.Rows.Count == 1), "the Payments tab lists it");
                await shot("77-salestax-payments");
                check(Drive.All<TabBar>(form.Content).Any(tb => tb.Items.Any(i => i.Value == "Return Worksheet")), "the Sales Tax page has the Tax Rates, Return Worksheet and Payments tabs");
                await api.Patch("api/salestax/rates/" + combo["id"].Str, Json.O("is_default", false)); Lists.Bust();
                await form.GoAsync("#/dashboard"); await Drive.Settle(300);
            }

            // ---- README pictures (only when LEDJORO_README_SHOTS=1): clean scenes of the demo company, window only, saved as readme-*.png -----
            if (Environment.GetEnvironmentVariable("LEDJORO_README_SHOTS") == "1")
            {
                string was = Theme.Name;
                Theme.Use("light"); await Drive.Settle(400);
                await form.GoAsync("#/dashboard"); await Drive.Settle(1200); await shot("readme-dashboard-light");
                await form.GoAsync("#/invoices"); await Drive.Settle(900); await shot("readme-invoices");
                await form.GoAsync("#/reports/profit-loss"); await Drive.Settle(900); await shot("readme-profit-loss");
                await form.GoAsync("#/banking"); await Drive.Settle(900); await shot("readme-banking");
                await form.GoAsync("#/salestax/worksheet"); await Drive.Settle(700); { var wsx = Drive.Find<SalesTaxScreen>(form.Content); var px = Drive.All<Combo>(wsx).First(c => c.Items.Any(i => i.Key == "last-quarter")); px.Value = "this-year"; await Drive.Settle(1000); } await shot("readme-sales-tax-worksheet");
                await form.GoAsync("#/invoices/new"); await Drive.Settle(700); { var cx = Drive.All<Combo>(form.Content).First(); var cust = (await api.Get("api/contacts?kind=customer")).Items.First(c => c["name"].Str == "Tax Test Buyer"); cx.Value = cust["id"].Str; var spr = (await api.Get("api/salestax/rates"))["rates"].Items.First(r => r["name"].Str == "Springfield"); Drive.All<Combo>(form.Content).First(c => c.Items.Any(i => i.Value == "No sales tax")).Value = spr["id"].Str; Drive.Field(form.Content, "Description").Text = "Custom website design"; Drive.Find<Input>(form.Content, i => i.Cue == "0.00").Text = "1200.00"; await Drive.Settle(400); } await shot("readme-invoice-with-tax");
                await form.GoAsync("#/quickbooks"); await Drive.Settle(7000); await shot("readme-quickbooks");
                Theme.Use("night"); await Drive.Settle(500);
                await form.GoAsync("#/dashboard"); await Drive.Settle(1200); await shot("readme-dashboard-night");
                await form.GoAsync("#/reports/balance-sheet"); await Drive.Settle(900); await shot("readme-balance-sheet-night");
                Theme.Use(was); await Drive.Settle(400);
            }

            // ---- About ---------------------------------------------------------------------------------------------------
            {
                check(System.Text.RegularExpressions.Regex.IsMatch(AboutScreen.Version, @"^\d+\.\d+\.\d+(-[a-z]+\.\d+)?$"), "the program knows its version number (" + AboutScreen.Version + ")");
                check(AboutScreen.Version == "0.9.2-beta.1", "the version is 0.9.2-beta.1");
                check(Updater.Compare("0.9.0-beta.1", "v0.9.0-beta.2") < 0 && Updater.Compare("0.9.0-beta.2", "0.9.0") < 0 && Updater.Compare("0.9.0", "0.10.0") < 0 && Updater.Compare("v1.0.0", "1.0.0") == 0 && Updater.Compare("0.9.0-beta.10", "0.9.0-beta.9") > 0, "version numbers are put in the right order");
                {
                    string dl = "https://github.com/" + Updater.Repo + "/releases/download/";
                    var list = Json.Parse("[{\"tag_name\":\"v0.9.0-beta.1\",\"draft\":false,\"name\":\"a\",\"assets\":[{\"name\":\"Ledjoro.exe\",\"size\":5,\"browser_download_url\":\"" + dl + "v0.9.0-beta.1/Ledjoro.exe\"},{\"name\":\"Ledjoro.exe.sig\",\"size\":300,\"browser_download_url\":\"" + dl + "v0.9.0-beta.1/Ledjoro.exe.sig\"}]},"
                        + "{\"tag_name\":\"v0.9.0-beta.2\",\"draft\":false,\"name\":\"b\",\"body\":\"notes\",\"assets\":[{\"name\":\"Ledjoro.exe\",\"size\":2000000,\"digest\":\"sha256:ABC\",\"browser_download_url\":\"" + dl + "v0.9.0-beta.2/Ledjoro.exe\"},{\"name\":\"Ledjoro.exe.sig\",\"size\":300,\"browser_download_url\":\"" + dl + "v0.9.0-beta.2/Ledjoro.exe.sig\"}]},"
                        + "{\"tag_name\":\"v5.0.0\",\"draft\":false,\"assets\":[{\"name\":\"Ledjoro.exe\",\"size\":2000000,\"browser_download_url\":\"" + dl + "v5.0.0/Ledjoro.exe\"}]},"
                        + "{\"tag_name\":\"v9.9.9\",\"draft\":true,\"assets\":[{\"name\":\"Ledjoro.exe\",\"size\":1,\"browser_download_url\":\"" + dl + "v9.9.9/Ledjoro.exe\"}]},"
                        + "{\"tag_name\":\"v8.0.0\",\"draft\":false,\"assets\":[{\"name\":\"Ledjoro.exe\",\"size\":1,\"browser_download_url\":\"https://evil.example/Ledjoro.exe\"}]}]");
                    var pick = Updater.Pick(list);
                    check(pick != null && pick.Version == "0.9.0-beta.2" && pick.Sha256 == "abc" && pick.Notes == "notes", "the update check picks the newest published release (not a draft, not a file from somewhere else, not one without a signature file)");
                    check(pick != null && pick.SigUrl.EndsWith("v0.9.0-beta.2/Ledjoro.exe.sig"), "it knows where the signature file is");
                    {   // signatures: made by scripts/release-sign.js with a throwaway key; the program must accept exactly this and nothing else
                        byte[] pub = Convert.FromBase64String("pXCuPTR9jDuKgQrNp56xnJPeFgXBX5F6zjmif+OFgs+ncVWkYzLYDl9zQwYvQMxn4TuVAIJTQEfgr+0FZ5VbHA==");
                        string vsha = "3a311faa59b0bae41cb73dfbe2aaa1c140938d76f7e825f73321923bea681dd9", vsig = "{\"format\":1,\"version\":\"7.7.7-test.1\",\"sha256\":\"3a311faa59b0bae41cb73dfbe2aaa1c140938d76f7e825f73321923bea681dd9\",\"signature\":\"Y1+XbyJVi4eVxQaEVwl0fPduevIebndPvUiP3IU5SOMHnaVwgnM/+OEOcIutnQSacrWzlI7QOTy7KOIDSI/0vQ==\"}";
                        Func<Action, bool> refused = act => { try { act(); return false; } catch (Updater.UpdateError) { return true; } };
                        bool good = !refused(() => Updater.VerifySignature(vsig, vsha, "v7.7.7-test.1", pub));
                        check(good, "a signature made by the signing script (in Node) verifies in the program (in .NET)");
                        var otherKey = (byte[])pub.Clone(); otherKey[5] ^= 1;
                        check(refused(() => Updater.VerifySignature(vsig, vsha, "7.7.7-test.1", otherKey)) && refused(() => Updater.VerifySignature(vsig, vsha, "7.7.8-test.1", pub)) && refused(() => Updater.VerifySignature(vsig, vsha.Replace('3', '4'), "7.7.7-test.1", pub))
                            && refused(() => Updater.VerifySignature(vsig.Replace("Y1+X", "Y1+Y"), vsha, "7.7.7-test.1", pub)) && refused(() => Updater.VerifySignature("{broken", vsha, "7.7.7-test.1", pub)) && refused(() => Updater.VerifySignature(vsig, vsha, "7.7.7-test.1", null)),
                            "a different key, version, file or signature, a damaged signature file, or a program with no key is refused");
                    }
                    string td = Path.Combine(Path.GetTempPath(), "ledjoro-upd-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(td);
                    try
                    {
                        string exe = Path.Combine(td, "Ledjoro.exe"), nw = Path.Combine(td, "Ledjoro.update.download");
                        var big = new byte[1100000]; big[0] = (byte)'M'; big[1] = (byte)'Z'; File.WriteAllBytes(exe, new byte[] { 1, 2, 3 }); File.WriteAllBytes(nw, big);
                        var rel = new Updater.Release { Size = big.Length, Sha256 = Updater.HashOf(nw) };
                        Updater.Verify(nw, rel); bool badCaught = false;
                        try { Updater.Verify(nw, new Updater.Release { Size = big.Length, Sha256 = "00" }); } catch (Updater.UpdateError) { badCaught = true; }
                        bool shortCaught = false; try { Updater.Verify(nw, new Updater.Release { Size = big.Length + 1 }); } catch (Updater.UpdateError) { shortCaught = true; }
                        check(badCaught && shortCaught, "a download that does not match its published size or fingerprint is refused");
                        string bk = Updater.Install(nw, exe, "0.9.0-beta.1");
                        check(Path.GetFileName(bk) == "Ledjoro-0.9.0-beta.1-backup.exe" && File.ReadAllBytes(bk).Length == 3 && File.ReadAllBytes(exe).Length == big.Length && !File.Exists(nw), "installing keeps the old program as a backup in the same folder and puts the new one in its place");
                        File.WriteAllBytes(nw, big); string bk2 = Updater.Install(nw, exe, "0.9.0-beta.1");
                        check(Path.GetFileName(bk2) == "Ledjoro-0.9.0-beta.1-backup (2).exe" && File.Exists(bk), "a second install never overwrites an earlier backup");
                        bool failed = false; try { Updater.Install(Path.Combine(td, "missing.download"), exe, "0.9.1"); } catch (Updater.UpdateError) { failed = true; }
                        check(failed && File.Exists(exe), "if the new file cannot be put in place the old program is put back");
                    }
                    finally { try { Directory.Delete(td, true); } catch (Exception) { } }
                }
                await form.GoAsync("#/about"); await Drive.Settle(500);
                var ab = Drive.Find<AboutScreen>(form.Content);
                check(ab != null && Drive.All<Lbl>(ab).Any(l => l.Text == "Version " + AboutScreen.Version), "the About page shows the version");
                var excerpt = Drive.All<Lbl>(ab).FirstOrDefault(l => l.Text.Contains("native Windows program") && l.Text.Length > 300);
                check(excerpt != null && !excerpt.Text.Contains("**") && !excerpt.Text.Contains("](") && !excerpt.Text.Contains("`"), "the About page shows a readable excerpt of the README (no markdown marks)");
                check(excerpt != null && excerpt.Text.EndsWith("\u2026") && Drive.All<Lbl>(ab).Any(l => l.Text == "Show the whole README"), "the excerpt is cut at a paragraph and the whole README is one click away");
                check(Drive.Button(ab, "Check for Updates") != null, "the About page has a Check for Updates button");
                {   // clicking it again and again must not make the Updates card grow
                    var upBtn = Drive.Button(ab, "Check for Updates"); var upCard = upBtn.Parent.Parent; int h1 = 0, h5 = 0;
                    for (int n = 1; n <= 5; n++) { upBtn.PerformClick(); await Drive.Settle(400); if (n == 1) h1 = upCard.Height; if (n == 5) h5 = upCard.Height; }
                    check(h1 > 0 && h1 == h5 && Drive.All<Notice>(upCard).Count() == 1, "pressing Check for Updates five times leaves the card the same size (" + h1 + " then " + h5 + ")");
                }
                check(Drive.Button(ab, "Report a Problem") != null && Drive.Button(ab, "See Known Issues") != null && Drive.Button(ab, "Copy Version Details") != null && Drive.Button(ab, "Buy Me a Coffee") != null && Drive.Button(ab, "Request a Feature") != null, "the About page has the Support buttons (report, known issues, copy version details, request a feature) and a donate button");
                check(Links.FeatureMailto().StartsWith("mailto:info@tusivo.com?subject=Ledjoro%20feature%20request&body=") && !Links.FeatureMailto().Contains(" "), "the feature-request email is addressed to info@tusivo.com with a subject and no raw spaces");
                check(Links.Issues.StartsWith("https://github.com/Tusivo/Ledjoro/") && Links.Donate == "https://buymeacoffee.com/tusivo" && Links.VersionDetails().StartsWith("Ledjoro " + AboutScreen.Version) && !Links.VersionDetails().Contains(Engine.CompanyFolder()), "the support links are right and the version details hold nothing about the books");
                await shot("62-about");
                { var spA = Drive.All<ScrollPage>(form.Content).FirstOrDefault(); if (spA != null) { spA.ScrollPos = 100000; await Drive.Settle(300); await shot("62b-about-support"); spA.ScrollPos = 0; await Drive.Settle(200); } }
                var more = Drive.All<Lbl>(ab).FirstOrDefault(l => l.Text == "Show the whole README"); if (more != null) { more.PerformClickLabel(); await Drive.Settle(300); }
                check(Drive.All<Lbl>(ab).Any(l => l.Text.Length > 4000), "Show the whole README shows all of it");
                await form.GoAsync("#/dashboard"); await Drive.Settle(300);
            }

            // ---- text size: Normal / Medium / Large scale fonts and spacing together, and the choice is remembered
            {
                string keepText = Theme.Text;
                try
                {
                    Theme.Text = "normal"; int p0 = Theme.Px(100); float f0 = Theme.F(10f).SizeInPoints;
                    Theme.Text = "medium"; int p1 = Theme.Px(100); float f1 = Theme.F(10f).SizeInPoints;
                    Theme.Text = "large"; int p2 = Theme.Px(100); float f2 = Theme.F(10f).SizeInPoints;
                    check(Math.Abs(f0 - 10f) < 0.01f && Math.Abs(f1 - 11.5f) < 0.01f && Math.Abs(f2 - 13f) < 0.01f && p1 > p0 && p2 > p1, "Medium and Large make text and spacing bigger than Normal");
                    Theme.Text = "large"; Theme.Save(); Theme.Text = "normal"; Theme.Load();
                    check(Theme.Text == "large", "the text size is remembered from one start to the next");
                    string pref = Path.Combine(Engine.DataDir(), "shell.json"); File.WriteAllText(pref, "random words only\r\n{\"text\":\"medium\"} and more"); Theme.Text = "normal"; Theme.Load();
                    check(Theme.Text == "medium", "text added to shell.json by hand does not lose the text size");
                    File.WriteAllText(pref, "garbage"); Theme.Load(); check(Theme.Text == "normal", "with nothing readable the text size is Normal");
                }
                finally { Theme.Text = keepText; Theme.Name = Theme.Name; Theme.Save(); Theme.Load(); }
            }

            // ---- search (Ctrl+K) -------------------------------------------------------------------------------
            {
                var sd = new SearchDialog();
                sd.Box.Text = "recon"; await sd.Search();
                check(sd.Results.Rows.Count > 0 && sd.Results.Rows[0].Cells[1].Value.ToString().Contains("Reconcile"), "search offers the Reconcile page when you type part of its name");
                var first = api.Get("api/contacts?kind=customer"); var cust = (await first).Items.FirstOrDefault();
                if (cust != null)
                {
                    sd.Box.Text = cust["name"].Str.Substring(0, Math.Min(5, cust["name"].Str.Length)); await sd.Search();
                    check(sd.Results.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[1].Value.ToString() == cust["name"].Str && r.Cells[0].Value.ToString() == "Customer"), "search finds a customer from the first letters of the name");
                }
                sd.Box.Text = "zzqxnothing"; await sd.Search();
                check(sd.Results.Rows.Count == 1 && sd.Results.Rows[0].Cells[1].Value.ToString().Contains("Nothing found"), "search says so plainly when nothing matches");
                sd.Dialog.Dispose();
            }

            // ---- reconcile -------------------------------------------------------------------------------------
            {
                await form.GoAsync("#/reconcile"); await Drive.Settle(500);
                var rs = Drive.Find<ReconcileScreen>(form.Content);
                check(rs != null && form.LastError == null, "the Reconcile page opens");
                if (rs != null)
                {
                    await shot("50-reconcile-start");
                    var accts = await api.Get("api/accounts"); var bank = accts.Items.First(a => a["subtype"].Str == "bank");
                    var combo = Drive.All<Combo>(rs).First(); combo.Value = bank["id"].Str;
                    var date = Drive.All<DateInput>(rs).First(); date.Text = "2099-12-31";
                    var st = await api.Get("api/reconcile/start?account_id=" + bank["id"].Str + "&statement_date=2099-12-31");
                    long total = st["cleared_cents"].Long; foreach (var l in st["lines"].Items) total += l["amount_cents"].Long;
                    var amt = Drive.All<Input>(rs).First(i => i.Cue == "0.00"); amt.Text = Fmt.Plain(total + 100);
                    Drive.Button(rs, "Start Reconciling").PerformClick(); await Drive.Settle(700);
                    check(rs.Started && rs.Lines.Rows.Count == st["lines"].Count, "starting lists the entries waiting for the statement (" + rs.Lines.Rows.Count + ")");
                    check(rs.Difference == total + 100 - st["cleared_cents"].Long, "nothing ticked yet, so the difference is the whole statement balance");
                    var fin = Drive.Button(rs, "Finish Reconciling");
                    check(fin != null && !fin.Enabled, "Finish stays switched off while the difference is not zero");
                    if (rs.Lines.Rows.Count > 0) { rs.Lines.CurrentCell = rs.Lines.Rows[0].Cells[1]; }
                    rs.SelectAllForTest(); await Drive.Settle(200);
                    check(rs.Difference == 100 && !fin.Enabled, "ticking everything leaves exactly the 1.00 that was typed in extra, and still cannot finish");
                    await shot("51-reconcile-ticked");
                    amt.Text = Fmt.Plain(total); Drive.Button(rs, "Start Reconciling").PerformClick(); await Drive.Settle(700);
                    rs.SelectAllForTest(); await Drive.Settle(200);
                    fin = Drive.Button(rs, "Finish Reconciling");
                    check(rs.Difference == 0 && fin.Enabled, "when everything is ticked and the balance agrees, the difference is 0.00 and Finish is available");
                    await shot("52-reconcile-ready");
                    fin.PerformClick(); await Drive.Settle(900);
                    var hist = await api.Get("api/reconcile/history");
                    check(hist.Items.Any(h => h["account_id"].Str == bank["id"].Str && h["undone"].Long == 0 && h["statement_cents"].Long == total), "finishing saves the reconciliation");
                    var after = await api.Get("api/reconcile/start?account_id=" + bank["id"].Str + "&statement_date=2099-12-31");
                    check(after["lines"].Count == 0 && after["cleared_cents"].Long == total, "the entries are cleared and no longer offered");
                    await shot("53-reconcile-done");
                    var h0 = hist.Items.First(h => h["undone"].Long == 0); await api.Post("api/reconcile/" + h0["id"].Str + "/undo");   // leave the books as found
                }
            }

            // ---- the invoice form can add a customer without losing the invoice
            {
                await form.GoAsync("#/invoices/new"); await Drive.Settle(500);
                check(Drive.All<Lbl>(form.Content).Any(l => l.Text == "New Invoice"), "the invoice form is titled New Invoice");
                var addC = Drive.Button(form.Content, "New Customer");
                check(addC != null, "the invoice form has a New Customer button");
                if (addC != null)
                {
                    var memoBox = Drive.Find<Input>(form.Content, i => i.Cue == "Memo (optional)"); memoBox.Text = "typed before adding the customer";
                    addC.Post(); var cd = await Drive.WaitDialog();
                    check(cd != null, "New Customer opens the customer window right on the invoice form");
                    if (cd != null)
                    {
                        Drive.Find<Input>(cd, i => i.Cue.StartsWith("Business or display name")).Text = "Added From Invoice Co";
                        Drive.Button(cd, "Save").PerformClick(); await Drive.WaitClosed(cd);
                        var combo = Drive.All<Combo>(form.Content).First();
                        check(combo.Label == "Added From Invoice Co", "the new customer is chosen on the invoice (" + combo.Label + ")");
                        check(memoBox.Text == "typed before adding the customer" && Drive.Find<Input>(form.Content, i => i.Cue == "Memo (optional)") == memoBox, "what was already typed on the invoice is still there");
                    }
                }
            }

            // ---- watched download folder ---------------------------------------------------------------------------------
            {
                string wdir = Path.Combine(Path.GetTempPath(), "ledjoro-watch-test-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(wdir);
                try
                {
                    // before any folder is set up, the Banking page invites the person to make one, and offers the bank instructions
                    await form.GoAsync("#/banking"); await Drive.Settle(900);
                    var tipPanel = Drive.Find<WatchPanel>(form.Content);
                    check(tipPanel != null && tipPanel.Visible && Drive.Button(tipPanel, "Set Up a Folder") != null && Drive.Button(tipPanel, "How to Download From Your Bank") != null, "until a folder is set up, Bank Transactions invites the person to make one");
                    await shot("60-banking-folder-tip");
                    if (tipPanel != null)
                    {
                        Drive.Button(tipPanel, "How to Download From Your Bank").Post(); var helpDlg = await Drive.WaitDialog();
                        check(helpDlg != null && Drive.All<Lbl>(helpDlg).Any(l => l.Text == "Bank of America") && Drive.All<Lbl>(helpDlg).Any(l => l.Text == "Chase") && Drive.All<Lbl>(helpDlg).Any(l => l.Text.Contains("QuickBooks (QBO)")), "the bank instructions window lists popular banks and the general steps");
                        await shot("61-bank-help");
                        if (helpDlg != null) { Drive.Button(helpDlg, "Close").PerformClick(); await Drive.WaitClosed(helpDlg); }
                    }
                    check(WatchPanel.IsGeneralFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads") && WatchPanel.IsGeneralFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) && !WatchPanel.IsGeneralFolder(WatchPanel.DedicatedFolder()) && !WatchPanel.IsGeneralFolder(wdir),
                        "Downloads, Desktop and Documents count as general folders; a folder of its own does not");
                    check(BankHelp.UrlFor("Chase").StartsWith("https://www.google.com/search?q=") && BankHelp.UrlFor("U.S. Bank").StartsWith("https://www.usbank.com/") && BankHelp.Count >= 15, "banks with a known help page link to it, the others to a search");
                    await form.GoAsync("#/import"); await Drive.Settle(700);
                    var imp1 = Drive.Find<ImportScreen>(form.Content); var panel = Drive.Find<WatchPanel>(form.Content);
                    check(panel != null && Drive.Button(imp1, "Create a Folder for Me") != null && Drive.Button(imp1, "How to Download From Your Bank") != null, "the folder section recommends a folder of its own and offers to create it");
                    check(panel != null && Drive.Button(imp1, "Save Folder") != null && Drive.Button(imp1, "Check Now") != null, "Import & Convert has the Watch a Folder section");
                    if (panel != null)
                    {
                        Drive.All<Check>(panel).First().Checked = true; Drive.All<Input>(panel).First().Text = wdir;
                        Drive.Button(imp1, "Save Folder").PerformClick(); await Drive.Settle(800);
                        check((await api.Get("api/watch"))["enabled"].Bool, "saving turns the folder watch on");
                        string ofx = "OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\n\r\n<OFX><SIGNONMSGSRSV1><SONRS><FI><ORG>Watch Test Bank</ORG></FI></SONRS></SIGNONMSGSRSV1><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD</CURDEF><BANKACCTFROM><ACCTID>99990001</ACCTID></BANKACCTFROM><BANKTRANLIST>"
                            + "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260201<TRNAMT>-11.11<FITID>wt1<NAME>WATCH ONE</STMTTRN><STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260202<TRNAMT>-22.22<FITID>wt2<NAME>WATCH TWO</STMTTRN></BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
                        File.WriteAllText(Path.Combine(wdir, "my bank statement.qbo"), ofx);
                        Drive.Button(imp1, "Check Now").PerformClick();
                        check(await Drive.Until(() => Drive.Button(panel, "Import") != null, 8000), "a new statement in the folder appears with an Import button");
                        await shot("58-watch-folder");
                        long before = (await api.Get("api/bank/transactions?status=new")).Count;
                        await form.GoAsync("#/banking"); await Drive.Settle(1200);
                        var bp = Drive.Find<WatchPanel>(form.Content);
                        check(bp != null && bp.Visible && Drive.Button(bp, "Import") != null, "the Bank Transactions page shows the waiting statement");
                        await shot("59-banking-waiting");
                        var accts = await api.Get("api/accounts"); var bankAcct = accts.Items.First(a => a["subtype"].Str == "bank");
                        if (bp != null) { Drive.All<Combo>(bp).First().Value = bankAcct["id"].Str; Drive.Button(bp, "Import").PerformClick(); await Drive.Settle(1500); }
                        long after = (await api.Get("api/bank/transactions?status=new")).Count;
                        check(after == before + 2, "pressing Import brings the statement's two transactions into To Review (" + before + " -> " + after + ")");
                        check(Drive.Find<WatchPanel>(form.Content) == null || !Drive.Find<WatchPanel>(form.Content).Visible, "the statement is no longer listed once imported");
                        var bt = await api.Get("api/import/batches"); var wb = bt.Items.FirstOrDefault(b => b["filename"].Str == "my bank statement.qbo");
                        if (wb != null) await api.Post("api/import/batches/" + wb["id"].Str + "/undo");
                    }
                }
                finally { try { await api.Post("api/watch", Json.O("enabled", false)); } catch (ApiException) { } try { Directory.Delete(wdir, true); } catch (IOException) { } }
            }

            // ---- finding the QuickBooks Web Connector program on this computer
            {
                string wc = QuickBooksScreen.FindWebConnector(); bool installed = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Intuit", "QuickBooks", "QBWebConnector"));
                check(installed ? (wc != null && File.Exists(wc) && wc.EndsWith("QBWebConnector.exe", StringComparison.OrdinalIgnoreCase)) : true, installed ? "the installed QuickBooks Web Connector is found (" + wc + ")" : "no QuickBooks Web Connector on this computer, so there is nothing to find");
            }

            // ---- the QuickBooks state check gives a clear answer either way
            {
                bool running, elevated; string say = QuickBooksScreen.QuickBooksState(out running, out elevated);
                bool qbRunning = System.Diagnostics.Process.GetProcessesByName("QBW32").Length > 0;
                check(running == qbRunning && say.Length > 40 && (running || say.Contains("not open")), "the QuickBooks check reports whether QuickBooks is open (" + (running ? (elevated ? "open, as administrator" : "open") : "not open") + ")");
            }

            // ---- QuickBooks Sync (the Web Connector link) ---------------------------------------------------------------
            {
                await form.GoAsync("#/quickbooks"); await Drive.Settle(500);
                var qs = Drive.Find<QuickBooksScreen>(form.Content);
                check(qs != null && Drive.Button(qs, "Set up the Connection") != null, "QuickBooks Sync offers to set up the connection (it is off until asked)");
                await shot("55-quickbooks-intro");
                if (qs != null && Drive.Button(qs, "Set up the Connection") != null)
                {
                    Drive.Button(qs, "Set up the Connection").PerformClick();
                    check(await Drive.Until(() => Drive.Button(qs, "Save Connection File") != null, 8000), "after setting up, the steps appear with the Save connection file button");
                    var pwLbl = Drive.All<Lbl>(qs).FirstOrDefault(l => l.Style == LS.Mono && l.Text.Length >= 20);
                    var stt = await api.Get("api/qbwc/status");
                    check(pwLbl != null && stt["enabled"].Bool && stt["running"].Bool, "the one-time password is shown and the link is listening");
                    await shot("56-quickbooks-steps");
                    if (pwLbl != null && stt["running"].Bool)
                    {
                        var wc = new FakeWebConnector(stt["port"].Int, stt["username"].Str, pwLbl.Text);
                        check(!await wc.TryLogin("wrong-password-here"), "the Web Connector is refused with a wrong password");
                        string outcome = await wc.Sync();
                        check(outcome == "done", "a pretend Web Connector can read QuickBooks through Ledjoro (" + outcome + ")");
                        check(await Drive.Until(() => Drive.Button(qs, "Import into my Books") != null, 9000), "the screen shows the results with an Import into my books button");
                        await shot("57-quickbooks-review");
                        long before = 0;
                        var imp = Drive.Button(qs, "Import into my Books"); if (imp != null) imp.PerformClick();
                        await Drive.Settle(1500);
                        var batches = await api.Get("api/import/batches"); var mine = batches.Items.FirstOrDefault(b => b["filename"].Str.StartsWith("QuickBooks Web Connector"));
                        check(mine != null && mine["summary"]["journal_entries"].Int == 1, "importing adds the QuickBooks transaction to the books, and it shows under Recent imports so it can be undone");
                        if (mine != null) await api.Post("api/import/batches/" + mine["id"].Str + "/undo");
                    }
                    await api.Post("api/qbwc/disable");
                    check(!(await api.Get("api/qbwc/status"))["running"].Bool, "turning the link off closes it");
                }
            }

            // ---- automatic backups ---------------------------------------------------------------------------------
            {
                await form.GoAsync("#/settings/data"); await Drive.Settle(700);
                var ss = Drive.Find<SettingsScreen>(form.Content);
                check(ss != null && ss.AutoOn != null, "Settings has an Automatic backups section");
                if (ss != null && ss.AutoOn != null)
                {
                    string folder = Path.Combine(Path.GetTempPath(), "ledjoro-autobackup-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    ss.AutoOn.Checked = true; ss.AutoMode.Value = "none"; ss.AutoFolder.Text = folder;
                    ss.AutoSaveBtn.PerformClick(); await Drive.Settle(900);
                    check((await api.Get("api/backup/auto"))["enabled"].Bool, "saving turns automatic backups on");
                    ss.AutoRunBtn.PerformClick(); await Drive.Until(() => Directory.Exists(folder) && Directory.GetFiles(folder, "ledjoro-auto-*.obbak").Length > 0, 12000); await Drive.Settle(300);
                    check(Directory.Exists(folder) && Directory.GetFiles(folder, "ledjoro-auto-*.obbak").Length == 1, "Back up now writes a backup file into the chosen folder");
                    var spA = Drive.All<ScrollPage>(form.Content).FirstOrDefault(); if (spA != null) { spA.ScrollPos = Theme.Px(520); await Drive.Settle(250); } await shot("54-auto-backup");
                    ss.AutoOn.Checked = false; ss.AutoFolder.Text = "Backups"; ss.AutoSaveBtn.PerformClick(); await Drive.Settle(700);
                    try { Directory.Delete(folder, true); } catch (IOException) { }
                }
            }
        }
    }
    /// <summary>Stands in for Intuit's QuickBooks Web Connector (and QuickBooks) in the self-test: it speaks the same SOAP to Ledjoro and answers its questions with a few made-up records.</summary>
    sealed class FakeWebConnector
    {
        readonly int port; readonly string user, password; readonly System.Net.Http.HttpClient http = new System.Net.Http.HttpClient();
        public FakeWebConnector(int port, string user, string password) { this.port = port; this.user = user; this.password = password; }
        static string Esc(string x) { return x.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }
        static string Unesc(string x) { return x.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&"); }
        async Task<string> Call(string method, params string[] kv)
        {
            string inner = ""; for (int i = 0; i + 1 < kv.Length; i += 2) inner += "<" + kv[i] + ">" + Esc(kv[i + 1]) + "</" + kv[i] + ">";
            string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><" + method + " xmlns=\"http://developer.intuit.com/\">" + inner + "</" + method + "></soap:Body></soap:Envelope>";
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "http://127.0.0.1:" + port + "/qbwc") { Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "text/xml") };
            req.Headers.Host = "localhost:" + port;
            var res = await http.SendAsync(req); return await res.Content.ReadAsStringAsync();
        }
        static string Result(string xml, string m) { var r = System.Text.RegularExpressions.Regex.Match(xml, "<" + m + "Result>(.*?)</" + m + "Result>", System.Text.RegularExpressions.RegexOptions.Singleline); return r.Success ? r.Groups[1].Value : ""; }
        public async Task<bool> TryLogin(string pw)
        {
            string r = await Call("authenticate", "strUserName", user, "strPassword", pw); return !r.Contains("<string>nvu</string>");
        }
        static string Rs(string tag, string id, string inner, int code = 0) { return "<?xml version=\"1.0\" ?><QBXML><QBXMLMsgsRs><" + tag + "Rs requestID=\"" + id + "\" statusCode=\"" + code + "\" statusSeverity=\"Info\" statusMessage=\"Status OK\">" + inner + "</" + tag + "Rs></QBXMLMsgsRs></QBXML>"; }
        static string Answer(string req)
        {
            var m = System.Text.RegularExpressions.Regex.Match(req, "<(\\w+)QueryRq requestID=\"(\\d+)\""); string tag = m.Groups[1].Value + "Query", id = m.Groups[2].Value;
            switch (m.Groups[1].Value)
            {
                case "Customer": return Rs(tag, id, "<CustomerRet><Name>QB Sync Customer</Name><FullName>QB Sync Customer</FullName><IsActive>true</IsActive></CustomerRet>");
                case "JournalEntry": return Rs(tag, id, "<JournalEntryRet><TxnID>T-1</TxnID><TxnDate>2026-03-01</TxnDate><RefNumber>QB1</RefNumber><JournalDebitLine><AccountRef><FullName>Checking</FullName></AccountRef><Amount>12.34</Amount></JournalDebitLine><JournalCreditLine><AccountRef><FullName>Owner's Equity</FullName></AccountRef><Amount>12.34</Amount></JournalCreditLine></JournalEntryRet>");
                default: return Rs(tag, id, "", 1);
            }
        }
        /// <summary>Does what the Web Connector does when you press Update Selected.</summary>
        public async Task<string> Sync()
        {
            string auth = await Call("authenticate", "strUserName", user, "strPassword", password);
            var strings = System.Text.RegularExpressions.Regex.Matches(Result(auth, "authenticate"), "<string>(.*?)</string>").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Groups[1].Value).ToList();
            if (strings.Count < 2 || strings[1] == "nvu") return "login refused";
            string ticket = strings[0]; int progress = 0, n = 0;
            while (progress < 100 && n++ < 80)
            {
                string req = Unesc(Result(await Call("sendRequestXML", "ticket", ticket, "strHCPResponse", "", "strCompanyFileName", "", "qbXMLCountry", "US", "qbXMLMajorVers", "16", "qbXMLMinorVers", "0"), "sendRequestXML"));
                if (req.Length == 0) return "no request";
                if (req.Contains("<Add") || req.Contains("<Mod") || req.Contains("<Del")) return "tried to change QuickBooks";
                string r = await Call("receiveResponseXML", "ticket", ticket, "response", Answer(req), "hresult", "", "message", "");
                if (!int.TryParse(Result(r, "receiveResponseXML"), out progress)) return "bad progress";
                if (progress < 0) return "error";
            }
            await Call("closeConnection", "ticket", ticket);
            return progress == 100 ? "done" : "did not finish";
        }
    }

}
