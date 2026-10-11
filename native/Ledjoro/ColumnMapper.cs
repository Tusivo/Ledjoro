using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>
    /// "What is each column?": one card per column of the CSV, showing its title and a couple of sample values, with a plain list to say what it is
    /// (Date, Payee, Money out...). The program fills in its best guess; a column can mean only one thing and each meaning can be used once.
    /// </summary>
    sealed class ColumnMapper : Stack
    {
        public static readonly string[][] Targets =
        {
            new[] { "date", "Date" }, new[] { "number", "Check number" }, new[] { "payee", "Payee (who)" }, new[] { "account", "Category" },
            new[] { "memo", "Memo" }, new[] { "out", "Money out" }, new[] { "in", "Money in" }, new[] { "amount", "Amount (+ or -)" }, new[] { "balance", "Balance" },
        };
        public sealed class Col { public int Index; public string Header = ""; public string[] Samples = new string[0]; public string Title { get { return Header.Length > 0 ? Header : "Column " + (Index + 1); } } }

        public readonly List<Col> Cols = new List<Col>();
        public readonly List<ColCard> Cards = new List<ColCard>();
        readonly Dictionary<string, int> map = new Dictionary<string, int>();
        public readonly Check HasHeader = new Check("The first row of the file holds column titles (skip it)", true), Invert = new Check("In this file a positive amount means money going out (some card statements)");
        public readonly Input RegisterName = new Input("for example: Personal Checking", 340);
        readonly Field regField; readonly Lbl status = new Lbl("", LS.Bold, true);
        bool syncing;
        public event Action Changed;

        public ColumnMapper(Jv sniff)
        {
            Fill = K.Surface; Gap = Theme.Px(12);
            foreach (var c in sniff["columns"].Items)
                Cols.Add(new Col { Index = c["index"].Int, Header = c["header"].Str, Samples = c["samples"].Items.Select(x => x.Str).ToArray() });
            HasHeader.Checked = sniff["hasHeader"].Bool;
            foreach (var k in sniff["suggested"].Keys) map[k] = sniff["suggested"][k].Int;

            Add(new Lbl("Below is each column of your file. For each one, choose what it is from its list. I have filled in my best guess; change anything that is wrong, and leave a column on \"not used\" if you do not need it.", LS.Normal, true), Theme.Px(2));
            Add(new Lbl("Money out means payments, withdrawals and card charges. Money in means deposits and payments received.", LS.Muted, true));
            Add(new Lbl("Green = I found a match for this column.   Red = no match (fine to leave red if you do not need that column).", LS.Bold, true));
            var flow = new Flow { Fill = K.Surface, HGap = Theme.Px(12), VGap = Theme.Px(12) };
            foreach (var c in Cols) { var card = new ColCard(this, c); Cards.Add(card); flow.Add(card); }
            Add(flow);
            regField = new Field("What do you call this account? (the account this file is the history of)", RegisterName, 560); Add(regField);
            Add(HasHeader, Theme.Px(2)); Add(Invert, Theme.Px(2));
            Add(status, 0);
            HasHeader.Changed += (s, e) => Raise(); Invert.Changed += (s, e) => Raise(); RegisterName.Tb.TextChanged += (s, e) => Refresh2();
            Refresh2();
        }

        public int? ColumnFor(string key) { int i; return map.TryGetValue(key, out i) ? (int?)i : null; }
        public string KeyFor(int col) { foreach (var p in map) if (p.Value == col) return p.Key; return ""; }
        public Col Column(int i) { return Cols.FirstOrDefault(c => c.Index == i); }

        public void Assign(string key, int col)
        {
            foreach (var k in map.Where(p => p.Value == col && p.Key != key).Select(p => p.Key).ToList()) map.Remove(k);   // a column means one thing
            map[key] = col; Raise();
        }
        public void Clear(string key) { if (map.Remove(key)) Raise(); }
        /// <summary>The list on a card changed: this column is now "key" (or not used, when key is empty).</summary>
        public void Choose(int col, string key)
        {
            if (syncing) return;
            foreach (var k in map.Where(p => p.Value == col).Select(p => p.Key).ToList()) map.Remove(k);
            if (key.Length > 0) Assign(key, col); else Raise();
        }
        void Raise() { Refresh2(); var h = Changed; if (h != null) h(); }

        string Missing()
        {
            if (!map.ContainsKey("date")) return "Still needed: choose which column is the Date.";
            if (!map.ContainsKey("out") && !map.ContainsKey("in") && !map.ContainsKey("amount")) return "Still needed: choose which column is Money out and/or Money in (or a single Amount column).";
            if (map.ContainsKey("account") && RegisterName.Text.Trim().Length == 0) return "Still needed: type the name of this account in the box above (for example Personal Checking).";
            return "";
        }

        void Refresh2()
        {
            syncing = true;
            foreach (var card in Cards) { card.Combo.Value = KeyFor(card.Col.Index); card.Invalidate(); }
            syncing = false;
            regField.Visible = map.ContainsKey("account");
            string miss = Missing();
            status.Tint = miss.Length > 0 ? Theme.Warn : Theme.Good;
            status.Text = miss.Length > 0 ? miss
                : map.ContainsKey("account") ? "Ready. Because a Category column is chosen, this will be read as an account register: every line is posted to the books. Press Preview to see what will happen."
                : "Ready. There is no Category column, so this will be read as a bank statement: every line waits under Banking, \"To review\", for the bank account you choose in the preview.";
            Vx.Relayout(this);
        }

        /// <summary>The mapping as the engine wants it: {"date": 0, "out": 5, ...}.</summary>
        public Dictionary<string, object> Mapping()
        {
            var d = new Dictionary<string, object>(); foreach (var p in map) d[p.Key] = p.Value; return d;
        }
    }

    /// <summary>One column of the file: its title and sample values, centred, with a list saying what it is.</summary>
    sealed class ColCard : Control
    {
        public readonly ColumnMapper.Col Col; public readonly Combo Combo; readonly ColumnMapper owner;
        public ColCard(ColumnMapper owner, ColumnMapper.Col col)
        {
            this.owner = owner; Col = col; TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Size = new Size(Theme.Px(250), Theme.Px(150));
            var items = new List<KeyValuePair<string, string>> { Ui.It("", "not used") };
            items.AddRange(ColumnMapper.Targets.Select(t => Ui.It(t[0], t[1])));
            Combo = Ui.Cb(items, "", 214); Combo.AccessibleName = "What is the column " + col.Title + "?";
            Combo.Changed += (s, e) => owner.Choose(col.Index, Combo.Value);
            Controls.Add(Combo);
        }
        public override Size GetPreferredSize(Size proposed) { return Size; }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Combo == null) return;   // the size is set in the constructor, before the list exists
            int pad = Theme.Px(14), h = Theme.Px(34);
            Combo.SetBounds(pad, Height - pad - h, Width - pad * 2, h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            bool used = owner.KeyFor(Col.Index).Length > 0;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Theme.Round(r, Theme.Px(10))) using (var b = new SolidBrush(used ? Theme.GoodSoft : Theme.BadSoft)) using (var pen = new Pen(used ? Theme.Good : Theme.Bad, 2)) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
            var f = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter;
            int pad = Theme.Px(12), w = Width - pad * 2;
            TextRenderer.DrawText(e.Graphics, Col.Title, Theme.F(11f, FontStyle.Bold), new Rectangle(pad, Theme.Px(14), w, Theme.Px(24)), Theme.Ink, f);
            string s1 = Col.Samples.Length > 0 ? Col.Samples[0] : "", s2 = Col.Samples.Length > 1 ? Col.Samples[1] : "";
            TextRenderer.DrawText(e.Graphics, s1.Length > 0 ? "e.g.  " + s1 : "(empty)", Theme.F(9f), new Rectangle(pad, Theme.Px(44), w, Theme.Px(18)), Theme.Muted, f);
            if (!used) TextRenderer.DrawText(e.Graphics, "No match found", Theme.F(9f, FontStyle.Bold), new Rectangle(pad, Theme.Px(84), w, Theme.Px(18)), Theme.Bad, f);
            if (s2.Length > 0) TextRenderer.DrawText(e.Graphics, "and  " + s2, Theme.F(9f), new Rectangle(pad, Theme.Px(63), w, Theme.Px(18)), Theme.Muted, f);
        }
    }
}
