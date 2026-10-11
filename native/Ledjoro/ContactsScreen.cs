using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class ContactsScreen : Screen
    {
        readonly string kind; readonly bool openNew; Grid grid;
        public ContactsScreen(string kind, bool openNew) { this.kind = kind; this.openNew = openNew; }
        bool Customer { get { return kind == "customer"; } }
        bool CanWrite { get { return A.Can(Customer ? "sales.write" : "purchases.write"); } }

        public override async Task Load()
        {
            Lists.Bust();
            var all = await Lists.Contacts();
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            root.Add(new Header(Customer ? "Customers" : "Vendors", null, CanWrite ? Ui.B(Customer ? "New Customer" : "New Vendor", BK.Primary, () => Ui.Run(() => Edit(null))) : null));
            grid = new Grid(); grid.Cols(Grid.Col("Name", 2.4f), Grid.Col("Email", 2.4f), Grid.Col("Phone", 1.3f), Grid.Col("Terms", 0.9f), Grid.Col(Customer ? ">Balance owed" : ">Balance payable", 1.4f), Grid.Col("", 0.8f));
            foreach (var c in all.Items.Where(c => c["kind"].Str == kind))
            {
                long bal = c["ledger_balance_cents"].Long;
                grid.Add(c, c["name"].Str, c["email"].Str, Fmt.Phone(c["phone"].Str), "Net " + c["terms_days"].Str, Fmt.Money(Customer ? bal : -bal), c["active"].Bool ? "" : "inactive");
            }
            root.AddFill(grid);
            if (CanWrite) grid.Activated += tag => Ui.Run(() => Edit((Jv)tag));
            SetBody(root);
            if (openNew && CanWrite) Ui.Run(() => Edit(null));
        }

        static readonly string[] States = "AL AK AZ AR CA CO CT DE DC FL GA HI ID IL IN IA KS KY LA ME MD MA MI MN MS MO MT NE NV NH NJ NM NY NC ND OH OK OR PA RI SC SD TN TX UT VT VA WA WV WI WY".Split(' ');

        Task Edit(Jv c) { return EditContact(kind, c, id => { App.Main.Reload(); return Task.FromResult(0); }); }

        /// <summary>The New / Edit customer-or-vendor window. <paramref name="after"/> runs once it is saved, with the person's id.</summary>
        public static async Task EditContact(string kind, Jv c, Func<long, Task> after)
        {
            Api A = App.Api; bool Customer = kind == "customer";
            var dlg = new Dlg(c == null ? "New " + (kind == "vendor" ? "Vendor" : "Customer") : "Edit " + c["name"].Str, 640);
            Func<string, string> got = k => c == null ? "" : c[k].Str;
            string person = string.Join(" ", new[] { got("first_name"), got("middle_name"), got("last_name") }.Where(x => x.Length > 0));
            var first = new Input("First") { Text = got("first_name") }; var middle = new Input("Middle") { Text = got("middle_name") }; var last = new Input("Last") { Text = got("last_name") };
            var name = new Input("Business or display name (optional if you gave a person's name)") { Text = c == null || got("name") == person ? "" : got("name") };
            var email = new Input("name@example.com") { Text = got("email") };
            var phone = new PhoneInput(got("phone")); var phone2 = new PhoneInput(got("phone2"));
            string legacy = got("address").Replace("\r", "").Replace("\n", ", "), street0 = got("addr_street");
            bool structured = street0.Length > 0 || got("addr_city").Length > 0 || got("addr_state").Length > 0 || got("addr_zip").Length > 0;
            var street = new Input("Street address") { Text = structured ? street0 : legacy }; var street2 = new Input("Apartment, suite, unit (optional)") { Text = got("addr_street2") };
            var city = new Input("City") { Text = got("addr_city") };
            var items = new List<KeyValuePair<string, string>> { Ui.It("", "State") }; items.AddRange(States.Select(x => Ui.It(x, x)));
            var state = Ui.Cb(items, got("addr_state"), 110); var zip = new Input("ZIP code") { Text = got("addr_zip") }; zip.Tb.MaxLength = 10;
            var terms = new Input("30") { Text = c == null ? "30" : c["terms_days"].Str, RightAlign = true };
            var is1099 = new Check("Track for 1099", c != null && c["is_1099"].Bool); var active = new Check("Active", c == null || c["active"].Bool);
            Check exempt = null; Input exNote = null; Combo defRate = null; var taxJ = Customer ? await Lists.TaxRates() : null;
            if (Customer)
            {
                exempt = new Check("Tax-exempt customer (no sales tax is charged)", c != null && c["tax_exempt"].Long == 1); exNote = new Input("Exemption certificate number or note") { Text = got("tax_exempt_note") };
                var tl = new List<KeyValuePair<string, string>> { Ui.It("", "Use the standard rate") }; tl.AddRange(taxJ["rates"].Items.Where(r => r["active"].Long == 1).Select(r => Ui.It(r["id"].Str, r["name"].Str)));
                defRate = Ui.Cb(tl, c == null || c["tax_rate_id"].IsNull || c["tax_rate_id"].Long == 0 ? "" : c["tax_rate_id"].Str, 300);
            }
            dlg.Body.Gap = Theme.Px(10);
            var r1 = new Flow { Fill = Pnl.K.Surface }; r1.Add(Ui.F("First name", first, 190)); r1.Add(Ui.F("Middle", middle, 130)); r1.Add(Ui.F("Last name", last, 230)); dlg.Body.Add(r1);
            dlg.Body.Add(Ui.F("Business or display name (optional)", name, 560));
            dlg.Body.Add(Ui.F("Email", email, 560));
            var r2 = new Flow { Fill = Pnl.K.Surface }; r2.Add(Ui.F("Phone (xxx-xxx-xxxx)", phone, 270)); r2.Add(Ui.F("Second phone (xxx-xxx-xxxx)", phone2, 270)); dlg.Body.Add(r2);
            var addr = new Card { Gap = Theme.Px(8) }; addr.Padding = new Padding(Theme.Px(14));
            addr.Add(new Lbl("Address", LS.Bold), Theme.Px(2));
            addr.Add(Ui.F("Street address", street, 530)); addr.Add(Ui.F("Apartment, suite, unit", street2, 530));
            var r3 = new Flow { Fill = Pnl.K.Surface }; r3.Add(Ui.F("City", city, 250)); r3.Add(Ui.F("State", state, 110)); r3.Add(Ui.F("ZIP code", zip, 150)); addr.Add(r3, 0);
            dlg.Body.Add(addr);
            var r4 = new Flow { Fill = Pnl.K.Surface }; r4.Add(Ui.F("Payment terms (days)", terms, 150)); dlg.Body.Add(r4);
            if (Customer)
            {
                var taxCard = new Card { Gap = Theme.Px(8) }; taxCard.Padding = new Padding(Theme.Px(14));
                taxCard.Add(new Lbl("Sales tax", LS.Bold), Theme.Px(2)); taxCard.Add(exempt); taxCard.Add(Ui.F("Exemption certificate or note", exNote, 530));
                if (taxJ["rates"].Count > 0) taxCard.Add(Ui.F("Tax rate usually charged to this customer", defRate, 300));
                dlg.Body.Add(taxCard);
            }
            if (!Customer) dlg.Body.Add(is1099); if (c != null) dlg.Body.Add(active);
            dlg.Cancel();
            dlg.Primary("Save", async () =>
            {
                int t; if (!int.TryParse(terms.Text.Trim(), out t) || t < 0 || t > 365) throw new ApiException(0, "The payment terms must be a whole number of days from 0 to 365 (for example 30 for \"Net 30\").", Jv.Null);
                var body = Json.O("name", name.Text.Trim(), "first_name", first.Text.Trim(), "middle_name", middle.Text.Trim(), "last_name", last.Text.Trim(), "email", email.Text.Trim(), "phone", phone.Text, "phone2", phone2.Text,
                    "addr_street", street.Text.Trim(), "addr_street2", street2.Text.Trim(), "addr_city", city.Text.Trim(), "addr_state", state.Value, "addr_zip", zip.Text.Trim(), "terms_days", t, "is_1099", is1099.Checked);
                if (Customer) { body["tax_exempt"] = exempt.Checked; body["tax_exempt_note"] = exNote.Text.Trim(); body["tax_rate_id"] = defRate.Value.Length > 0 ? (object)long.Parse(defRate.Value) : null; }
                long savedId;
                if (c == null) { body["kind"] = kind; var made = await A.Post("api/contacts", body); savedId = made["id"].Long; } else { body["active"] = active.Checked; await A.Patch("api/contacts/" + c["id"].Str, body); savedId = c["id"].Long; }
                Lists.Bust(); App.Toast("Saved"); await after(savedId); return true;
            });
            dlg.Run();
        }
    }
}
