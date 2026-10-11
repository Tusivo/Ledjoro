using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class ImportScreen : Screen
    {
        string path = "", content = ""; Jv formats, accts; Lbl fileLbl; Combo fmt, order; Input reg; Check mkDocs; ColumnMapper mapper; Field regTop; Stack mapBox; Notice mapErr; Task mapperLoading = Task.FromResult(0); DateInput asof; Notice err; Stack previewBox, batchBox; ScrollPage page;

        public override async Task Load()
        {
            formats = await A.Get("api/import/formats"); accts = await Lists.Accounts();
            page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("Import & Convert", "Bring data in from QuickBooks exports. Nothing is changed until you confirm."));


            var card = new Card(); card.Add(new Lbl("Choose a file exported from QuickBooks", LS.Bold), Theme.Px(6));
            fileLbl = new Lbl("No file chosen", LS.Muted); var pick = new Flow { Fill = Pnl.K.Surface }; pick.Add(Ui.B("Choose File…", BK.Normal, Choose)); pick.Add(fileLbl); card.Add(pick, Theme.Px(10));
            var mapRow = new Flow { Fill = Pnl.K.Surface }; mapRow.Add(Ui.BA("Match Columns Myself", BK.Primary, () => OpenMapper(false))); mapRow.Add(new Lbl("For a CSV the program does not know: say what each column is (Date, Payee, Money out, Money in...).", LS.Muted));
            card.Add(mapRow, Theme.Px(12));
            var items = new List<KeyValuePair<string, string>> { Ui.It("", "Auto-detect") }; items.AddRange(formats.Keys.Select(k => Ui.It(k, formats[k].Str)));
            fmt = Ui.Cb(items, "", 420); fmt.Changed += (s2, e2) => { if (fmt.Value == "csv-mapped") { if (mapper == null && content.Length > 0) Ui.Run(() => OpenMapper(false)); } else CloseMapper(); };
            asof = new DateInput(Fmt.Today(), 170); order = Ui.Cb(new[] { Ui.It("mdy", "Month/Day/Year (US)"), Ui.It("dmy", "Day/Month/Year") }, Fmt.Pattern.TrimStart('d', 'D').Length < Fmt.Pattern.Length ? "dmy" : "mdy", 240);
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Format", fmt, 420)); f.Add(Ui.F("Opening-balance date (trial balance CSV)", asof, 250)); f.Add(Ui.F("Date order (IIF, register CSV)", order, 240));
            reg = new Input("e.g. Checking", 280); regTop = Ui.F("This register's account (account register CSV)", reg, 280); f.Add(regTop); card.Add(f, Theme.Px(8));
            mkDocs = new Check("Account registers: also make bills and invoices from the payees") { Checked = true }; card.Add(mkDocs, Theme.Px(8));
            card.Add(Ui.B("Need to convert a file first?  See the free Converter  \u203A", BK.Promo, ConverterInfo), Theme.Px(12));   // opens a page with the details and the link
            err = new Notice(NK.Error); card.Add(err, Theme.Px(8)); card.Add(Ui.BA("Preview", BK.Primary, Preview), 0);
            s.Add(card);
            var watch = new WatchPanel(true); s.Add(watch); Ui.Run(async () => { await watch.Load(); Vx.Relayout(s); page.PerformLayout(); });
            mapBox = new Stack { Gap = Theme.Px(10) }; s.Add(mapBox);
            previewBox = new Stack { Gap = Theme.Px(10) }; s.Add(previewBox);
            batchBox = new Stack { Gap = Theme.Px(10) }; s.Add(batchBox);

            s.Add(new Lbl("Supported formats", LS.H2));
            foreach (var k in formats.Keys) s.Add(new Lbl("•  " + formats[k].Str, LS.Normal, true), Theme.Px(3));
            s.Add(new Lbl("Desktop company files (.QBW/.QBB) are proprietary and are not read. In QuickBooks, export lists and transactions as IIF or CSV (or a Trial Balance CSV for opening balances) and import that here. Tax IDs are intentionally never imported.", LS.Muted, true));
            if (A.Can("export"))
            {
                s.Add(new Lbl("Export your data", LS.H2));
                s.Add(new Lbl("You always own your data. The Ledjoro format is plain JSON and can be re-imported.", LS.Muted, true));
                var ex = new Flow(); ex.Add(Ui.BA("Everything (Ledjoro JSON)", BK.Normal, () => Export("api/export/obx")));
                foreach (var t in new[] { "accounts", "contacts", "journal", "documents" }) { string table = t; ex.Add(Ui.BA(table + " (CSV)", BK.Normal, () => Export("api/export/csv/" + table))); }
                s.Add(ex);
            }
            SetBody(page);
            await DrawBatches();
        }

        void Choose()
        {
            using (var d = new OpenFileDialog { Title = "Choose a QuickBooks export", Filter = "QuickBooks exports|*.iif;*.qbo;*.qfx;*.ofx;*.csv;*.xml;*.qbxml;*.json;*.txt|All files|*.*" })
                if (d.ShowDialog(App.Main) == DialogResult.OK) LoadFile(d.FileName);
        }

        public void LoadFile(string file)
        {
            var fi = new FileInfo(file);
            if (fi.Length > 10 * 1024 * 1024) { err.Set("That file is " + Math.Max(1, fi.Length / 1048576) + " MB, which is larger than the 10 MB Ledjoro reads at once. Split it into smaller files (for example one per year) and import them one at a time."); return; }
            path = file; content = File.ReadAllText(path); fileLbl.Text = Path.GetFileName(path) + "  (" + (fi.Length / 1024) + " KB)"; fileLbl.Tint = null; ResetErr(); previewBox.ClearItems(); CloseMapper(); Relayout();
            // every CSV that holds transactions gets the drag-and-drop matcher (lists such as the chart of accounts keep their own formats)
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) mapperLoading = OpenMapperIfTransactions();
        }
        public Task PreviewNow() { return Preview(); }
        public void SetFormat(string key) { fmt.Value = key; }
        public void SetRegister(string name) { reg.Text = name; if (mapper != null) mapper.RegisterName.Text = name; }
        string RegisterText() { return (mapper != null && fmt.Value == "csv-mapped" && mapper.RegisterName.Text.Trim().Length > 0 ? mapper.RegisterName.Text : reg.Text).Trim(); }

        /// <summary>The page about the free CSV / QBO to IIF Converter: what it is, who made it, and the link.</summary>
        public static void ConverterInfo()
        {
            var d = new Dlg("CSV / QBO to IIF Converter", 600);
            d.Body.Add(new Lbl("A free program for converting files before you import them", LS.Bold, true));
            d.Body.Add(new Lbl("If a bank download (QBO) or a spreadsheet (CSV) will not import the way you want, this companion program can convert it to IIF first, and Ledjoro reads IIF in full.", LS.Normal, true));
            d.Body.Add(new Lbl("It was made by a friend of Ledjoro, who has worked tirelessly on it for several years, and it costs nothing to use.", LS.Normal, true));
            d.Body.Add(new Lbl("It is a separate program by a separate author. Ledjoro does not install or run it; the button below opens its page (on neowin.net) in your web browser.", LS.Muted, true));
            d.Cancel("Close");
            d.Primary("Visit the converter's Page", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.neowin.net/forum/topic/1434103-csv-qbo-to-iif-converter/") { UseShellExecute = true }); }
                catch (Exception) { Msg.Info("The page could not be opened", "Windows did not open your web browser. You can find the program by searching for \u201CCSV / QBO to IIF Converter neowin\u201D."); }
                return Task.FromResult(true);
            });
            d.Run();
        }

        object Body() { return Json.O("filename", Path.GetFileName(path), "content", content, "format", fmt.Value.Length > 0 ? (object)fmt.Value : null, "options", Json.O("asof", asof.Valid ? asof.Value : null, "dateOrder", order.Value, "register", RegisterText().Length > 0 ? RegisterText() : null, "documents", mkDocs.Checked, "mapping", mapper != null && fmt.Value == "csv-mapped" ? mapper.Mapping() : null, "headerRows", mapper != null && mapper.HasHeader.Checked ? 1 : 0, "invert", mapper != null && mapper.Invert.Checked)); }
        void Relayout() { page.PerformLayout(); Theme.Refresh(previewBox); }

        void ResetErr() { err.Reset(); if (mapErr != null) mapErr.Reset(); }
        void ShowErr(string m) { (mapper != null && mapErr != null ? mapErr : err).Set(m); }
        void CloseMapper() { mapper = null; mapErr = null; if (regTop != null) regTop.Visible = true; if (mapBox != null) { mapBox.ClearItems(); Relayout(); } }

        /// <summary>Test hook and button handler: read the file's columns from the engine and show the drag-and-drop matcher.</summary>
        async Task OpenMapperIfTransactions()
        {
            try
            {
                var sniff = await A.Post("api/import/sniff", Json.O("filename", Path.GetFileName(path), "content", content));
                string det = sniff["detected"].IsNull ? "" : sniff["detected"].Str;
                bool lists = det == "csv-accounts" || det == "csv-customers" || det == "csv-vendors" || det == "csv-items" || det == "csv-trialbalance";
                if (!lists) await OpenMapper(det.Length == 0, det, sniff);
            }
            catch (Exception) { /* the normal Preview explains any problem with the file */ }
        }

        public async Task OpenMapper(bool auto, string detected = null, Jv sniffed = null)
        {
            if (path.Length == 0) { err.Set("Choose a file first: press \"Choose file…\" and pick the export from QuickBooks."); return; }
            Jv sniff = sniffed;
            if (sniff == null) { try { sniff = await A.Post("api/import/sniff", Json.O("filename", Path.GetFileName(path), "content", content)); } catch (ApiException e) { err.Set(e.Message); return; } }
            mapBox.ClearItems(); previewBox.ClearItems(); err.Reset();
            mapper = new ColumnMapper(sniff); fmt.Value = "csv-mapped"; regTop.Visible = false;   // the matcher asks for the account name itself
            var c = new Card(); c.Add(new Lbl("Match your columns", LS.H3), Theme.Px(6));
            if (auto) c.Add(new Notice(NK.Warn, "I don't recognise this file's layout, so please tell me what each column is. Nothing is imported until you have seen a preview and confirmed."), Theme.Px(10));
            else if (detected == "csv-register") c.Add(new Notice(NK.Ok, "This looks like an account register. I have guessed what each column is. Check them, type the account's name, then press Preview."), Theme.Px(10));
            c.Add(mapper, Theme.Px(10));
            mapErr = new Notice(NK.Error); c.Add(mapErr, Theme.Px(8)); c.Add(Ui.BA("Preview", BK.Primary, Preview), 0);
            mapBox.Add(c); Relayout();
        }
        public ColumnMapper Mapper { get { return mapper; } }

        async Task Preview()
        {
            await mapperLoading; ResetErr(); previewBox.ClearItems();
            if (path.Length == 0) { ShowErr("Choose a file first: press \"Choose file…\" and pick the export from QuickBooks."); return; }
            Jv p;
            try { p = await A.Post("api/import/preview", Body()); }
            catch (ApiException e)
            {
                bool csvLike = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                if (mapper == null && csvLike && e.Message.Contains("Could not recognise")) { await OpenMapper(true); return; }   // unknown layout: let the person match the columns
                ShowErr(e.Message); return;
            }
            var sm = p["summary"]; var c = new Card(); c.Add(new Lbl("Preview", LS.H3));
            var det = new Flow { Fill = Pnl.K.Surface }; det.Add(new Lbl("Detected:")); det.Add(new Lbl(p["label"].Str, LS.Bold)); c.Add(det, Theme.Px(6));
            Action<string> bullet = t => c.Add(new Lbl("•  " + t, LS.Normal, true), Theme.Px(3));
            bullet("Accounts: " + sm["accounts"]["new"].Str + " new, " + sm["accounts"]["existing"].Str + " already exist");
            bullet("Contacts: " + sm["contacts"]["new"].Str + " new, " + sm["contacts"]["existing"].Str + " already exist");
            bullet("Items: " + sm["items"]["new"].Str + " new, " + sm["items"]["existing"].Str + " already exist");
            bullet("Journal entries: " + sm["journal_entries"]["new"].Str + " new, " + sm["journal_entries"]["duplicate"].Str + " already imported");
            if (!sm["documents"].IsNull && sm["documents"]["new"].Int + sm["documents"]["duplicate"].Int > 0) bullet("Bills and invoices (paid): " + sm["documents"]["new"].Str + " new, " + sm["documents"]["duplicate"].Str + " already imported");
            if (!sm["bank_history"].IsNull && sm["bank_history"].Int > 0) bullet("Bank history: " + sm["bank_history"].Str + " lines (shown under Bank transactions, Categorized)");
            bullet("Bank transactions: " + sm["bank_transactions"]["count"].Str);
            if (!p["bank_meta"].IsNull) c.Add(new Lbl("Statement from " + (p["bank_meta"]["org"].Str.Length > 0 ? p["bank_meta"]["org"].Str : "your bank") + " (account ending " + p["bank_meta"]["account_id_tail"].Str + ").", LS.Muted, true), Theme.Px(8));
            foreach (var w in p["warnings"].Items) c.Add(new Notice(NK.Warn, w.Str), Theme.Px(6));   // yellow is for things that need a look
            int nn = p["notes"].Count;
            if (nn > 0)
            {   // information that is not a problem stays out of the way, one click to read
                var noteText = new Lbl("\u2022  " + string.Join("\n\u2022  ", p["notes"].Items.Select(x => x.Str)), LS.Muted, true) { Visible = false };
                string closed = "Show " + nn + " note" + (nn == 1 ? "" : "s") + " about this file (nothing here needs fixing)";
                var toggle = new Lbl(closed, LS.Link) { Underline = true };
                toggle.Click += (s2, e2) => { noteText.Visible = !noteText.Visible; toggle.Text = noteText.Visible ? "Hide the notes" : closed; Vx.Relayout(c); };
                c.Add(toggle, Theme.Px(6)); c.Add(noteText, Theme.Px(8));
            }
            int ec = p["errorCount"].Int;
            c.Add(ec > 0 ? new Notice(NK.Error, ec + " problem(s):\n• " + string.Join("\n• ", p["errors"].Items.Take(10).Select(x => x.Str))) : new Notice(NK.Ok, "No problems found."), Theme.Px(10));
            Combo bank = null; Check skip = null;
            if (sm["bank_transactions"]["count"].Int > 0) { bank = Ui.Cb(Ui.AccountItems(accts, a => a["subtype"].Str == "bank" || a["subtype"].Str == "credit_card"), null, 340); c.Add(Ui.F("Import these transactions into", bank, 340), Theme.Px(8)); }
            if (ec > 0) { skip = new Check("Import the valid records and skip the ones with problems"); c.Add(skip, Theme.Px(8)); }
            c.Add(Ui.BA("Import Now", BK.Primary, async () =>
            {
                var body = (Dictionary<string, object>)Body(); if (bank != null && bank.Value.Length > 0) body["bank_account_id"] = long.Parse(bank.Value); body["allow_skip"] = skip != null && skip.Checked;
                Jv r;
                try { r = await A.Post("api/import/commit", body); } catch (ApiException e) { ShowErr(e.Message); return; }
                Lists.Bust(); previewBox.ClearItems();
                previewBox.Add(new Notice(NK.Ok, "Imported: " + r["accounts"].Str + " accounts, " + r["contacts"].Str + " contacts, " + r["items"].Str + " items, " + r["journal_entries"].Str + " journal entries, " + r["documents"].Str + " bills/invoices, " + r["bank_history"].Str + " bank history lines, " + r["bank_added"].Str + " bank transactions. Skipped " + r["skipped_duplicates"].Str + " duplicates and " + r["skipped_invalid"].Str + " invalid."));
                Relayout(); await DrawBatches();
            }), 0);
            previewBox.Add(c); Relayout();
        }

        async Task DrawBatches()
        {
            var rows = await A.Get("api/import/batches"); batchBox.ClearItems();
            if (rows.Count == 0) { Relayout(); return; }
            batchBox.Add(new Lbl("Recent imports", LS.H2));
            batchBox.Add(new Lbl("Made a mistake? Undo posts reversing entries for everything an import added (history stays intact) and lets you import the file again.", LS.Muted, true));
            var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("#", 0.5f), Grid.Col("When", 1.8f), Grid.Col("File", 2.4f), Grid.Col("Format", 1f), Grid.Col("Result", 2.2f));
            foreach (var b in rows.Items) g.Add(b, b["id"].Str, Fmt.FromUnixMs(b["created_at"].Long).ToLocalTime().ToString("g"), b["filename"].Str.Length > 0 ? b["filename"].Str : "-", b["format"].Str,
                b["undone"].Bool ? new GC("undone", Theme.Warn) : new GC(b["summary"]["journal_entries"].Long + " entries, " + b["summary"]["documents"].Long + " bills/invoices, " + b["summary"]["bank_added"].Long + " bank rows"));
            batchBox.Add(g);
            if (A.Can("import")) batchBox.Add(Ui.BA("Undo Selected Import", BK.Danger, async () =>
            {
                var b = g.SelectedTag as Jv; if (b == null) { App.Toast("Click an import in the list first, then press Undo."); return; }
                if (b["undone"].Bool) { App.Toast("That import was already undone."); return; }
                if (!Msg.Confirm("Undo import #" + b["id"].Str + "?", "Its " + b["live_entries"].Str + " journal entries will be reversed, and the bills and invoices it created are removed (the reversing entries stay in the journal as the record). Accounts, contacts and items it created stay.", "Undo Import", true)) return;
                var r = await A.Post("api/import/batches/" + b["id"].Str + "/undo"); Lists.Bust();
                App.Toast("Undone: " + r["reversed"].Str + " entries reversed, " + r["bank_removed"].Str + " bank rows removed"); await DrawBatches();
            }, true), 0);
            Relayout();
        }

        async Task Export(string url)
        {
            var f = await A.GetFile(url);
            using (var d = new SaveFileDialog { FileName = f.FileName, Title = "Save export" })
            { if (d.ShowDialog(App.Main) != DialogResult.OK) return; File.WriteAllBytes(d.FileName, f.Bytes); App.Toast("Saved " + Path.GetFileName(d.FileName)); }
        }
    }
}
