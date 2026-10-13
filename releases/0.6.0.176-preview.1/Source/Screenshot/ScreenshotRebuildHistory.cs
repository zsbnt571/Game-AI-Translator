using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal sealed class ScreenshotRebuildHistory : ScrollableControl
{
    private sealed record Card(SessionHistoryItem Item, Bitmap Thumbnail);
    private readonly List<Card> cards = [];
    private readonly ToolTip hint = new() { InitialDelay = 450 };
    private readonly Font localFont = ScreenshotRebuildStyle.Font();
    private readonly Font titleFont = ScreenshotRebuildStyle.Font(8.25f, FontStyle.Bold);
    private Rectangle[] bounds = [];
    private (string Label, int Y)[] headings = [];
    private int selected = -1, hover = -1;
    internal string EmptyMessage { get; set; } = "还没有截图记录\n开始截图或导入图片";
    internal int DecodeCount { get; private set; }
    internal int Count => cards.Count;
    internal SessionHistoryItem? SelectedRecord => selected < 0 ? null : cards[selected].Item;
    internal event EventHandler? SelectionChanged;
    internal int SelectedIndex
    {
        get => selected;
        set
        {
            int next = value >= 0 && value < Count ? value : -1;
            if (next == selected) return;
            selected = next; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal ScreenshotRebuildHistory()
    {
        AutoScroll = true; TabStop = true; Font = localFont;
        AccessibleName = "截图历史记录";
        FontManager.MarkPreviewTextRoot(this);
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    // History image names are stable identifiers; the data directory may move
    // when a new private build carries forward the user's existing records.
    internal static string NormalizeKey(string? value) => (value ?? "").Replace('\\', '/').Split('/').Last();
    internal static string Key(SessionHistoryItem item) => NormalizeKey(item.SourceImagePath ?? item.Timestamp.ToString("O"));
    internal void SetRecords(IReadOnlyList<SessionHistoryItem> records)
    {
        var selectedKey = SelectedRecord is { } oldSelected ? Key(oldSelected) : "";
        if (!cards.Select(x => x.Item).SequenceEqual(records))
        {
            var old = cards.GroupBy(x => Key(x.Item)).ToDictionary(x => x.Key, x => new Queue<Card>(x));
            var next = new List<Card>();
            foreach (var item in records)
            {
                Card? previous = old.TryGetValue(Key(item), out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
                if (previous is not null && previous.Item.ThumbnailJpeg.AsSpan().SequenceEqual(item.ThumbnailJpeg))
                    next.Add(new(item, previous.Thumbnail));
                else
                {
                    previous?.Thumbnail.Dispose();
                    Bitmap thumbnail;
                    try { using var stream = new MemoryStream(item.ThumbnailJpeg); using var image = Image.FromStream(stream); thumbnail = new(image); }
                    catch { thumbnail = new Bitmap(86, 53); using var g = Graphics.FromImage(thumbnail); g.Clear(ScreenshotRebuildStyle.Palette.Control); }
                    next.Add(new(item, thumbnail)); DecodeCount++;
                }
            }
            foreach (var queue in old.Values) foreach (var card in queue) card.Thumbnail.Dispose();
            cards.Clear(); cards.AddRange(next);
            selected = cards.FindIndex(x => Key(x.Item) == selectedKey); hover = -1;
        }
        Reflow(); Invalidate();
    }
    internal void Reflow()
    {
        int S(int n) => ScreenshotRebuildStyle.S(this, n);
        int y = S(4), width = Math.Max(S(80), ClientSize.Width - SystemInformation.VerticalScrollBarWidth - S(4));
        DateTime? day = null;
        var positions = new List<Rectangle>(); var dates = new List<(string, int)>();
        foreach (var card in cards)
        {
            var current = card.Item.Timestamp.LocalDateTime.Date;
            if (current != day)
            {
                if (day is not null) y += S(8);
                dates.Add((current == DateTime.Today ? "今天" : current == DateTime.Today.AddDays(-1) ? "昨天" : current.ToString("MM月dd日"), y));
                day = current; y += S(26);
            }
            positions.Add(new(S(2), y, width, S(70))); y += S(78);
        }
        bounds = positions.ToArray(); headings = dates.ToArray();
        AutoScrollMinSize = new(0, y); Invalidate();
    }
    private Rectangle BoundsAt(int index) => index >= 0 && index < bounds.Length ? bounds[index] with { Y = bounds[index].Y + AutoScrollPosition.Y } : Rectangle.Empty;
    private int Hit(Point point) { for (int i = 0; i < Count; i++) if (BoundsAt(i).Contains(point)) return i; return -1; }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ScreenshotNativeScrollbarTheme.Apply(this, force: true); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Reflow(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Reflow(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = ScreenshotRebuildStyle.Palette; var g = e.Graphics;
        g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int S(int n) => ScreenshotRebuildStyle.S(this, n);
        if (Count == 0)
            WorkspaceDrawing.Text(g, EmptyMessage, Font, ClientRectangle, p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        foreach (var heading in headings)
            WorkspaceDrawing.Text(g, heading.Label, titleFont, new(S(3), heading.Y + AutoScrollPosition.Y, Width - S(6), S(22)), p.SecondaryText);
        for (int i = 0; i < Count; i++)
        {
            var r = BoundsAt(i); if (!r.IntersectsWith(e.ClipRectangle)) continue;
            using var shape = WorkspaceDrawing.Round(r, S(7));
            if (i == selected || i == hover)
            {
                var color = p.Main.GetBrightness() > .5f ? i == selected ? Color.FromArgb(219,233,253) : Color.FromArgb(230,239,250) : p.Control;
                using var brush = new SolidBrush(color); g.FillPath(brush, shape);
            }
            var thumbnailBox = new Rectangle(r.Left + S(7), r.Top + S(8), S(86), S(53));
            var image = cards[i].Thumbnail;
            float scale = Math.Min(thumbnailBox.Width / (float)image.Width, thumbnailBox.Height / (float)image.Height);
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            using (var fill = new SolidBrush(p.Control)) g.FillRectangle(fill, thumbnailBox);
            g.DrawImage(image, new Rectangle(thumbnailBox.Left + (thumbnailBox.Width - w) / 2, thumbnailBox.Top + (thumbnailBox.Height - h) / 2, w, h));
            int x = thumbnailBox.Right + S(10), width = Math.Max(1, r.Right - x - S(5));
            WorkspaceDrawing.Text(g, MainForm.GalleryItemTitle(cards[i].Item), titleFont, new(x, r.Top + S(8), width, S(18)), p.Text);
            WorkspaceDrawing.Text(g, cards[i].Item.Timestamp.LocalDateTime.ToString("HH:mm"), Font, new(x, r.Top + S(27), width, S(17)), p.SecondaryText);
            WorkspaceDrawing.Text(g, UiTargetLanguageDisplay.DisplayFromCode(cards[i].Item.TargetLanguage), Font, new(x, r.Top + S(44), width, S(17)), p.SecondaryText);
            if (i == selected && Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -S(2), -S(2)), p.Accent, BackColor);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button is MouseButtons.Left or MouseButtons.Right) { Focus(); var hit = Hit(e.Location); if (hit >= 0) SelectedIndex = hit; } }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var next = Hit(e.Location); if (next == hover) return;
        hover = next; Invalidate();
        hint.SetToolTip(this, next < 0 ? null : cards[next].Item.Timestamp.ToLocalTime().ToString("g") + "\n" + cards[next].Item.TranslationSummary);
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; hint.SetToolTip(this, null); Invalidate(); }
    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int next = e.KeyCode switch { Keys.Up => selected - 1, Keys.Down => selected + 1, Keys.Home => 0, Keys.End => Count - 1, _ => selected };
        if (next != selected && Count > 0)
        {
            SelectedIndex = Math.Clamp(next, 0, Count - 1);
            var r = BoundsAt(selected);
            if (r.Top < 0) AutoScrollPosition = new(0, -AutoScrollPosition.Y + r.Top);
            else if (r.Bottom > ClientSize.Height) AutoScrollPosition = new(0, -AutoScrollPosition.Y + r.Bottom - ClientSize.Height);
            e.Handled = e.SuppressKeyPress = true;
        }
        if (e.KeyCode == Keys.Enter && SelectedRecord is not null) { OnDoubleClick(EventArgs.Empty); e.Handled = true; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) { foreach (var card in cards) card.Thumbnail.Dispose(); cards.Clear(); localFont.Dispose(); titleFont.Dispose(); hint.Dispose(); } }
    }
}

internal sealed class ScreenshotHistoryRail : Control
{
    internal event EventHandler? ExpandRequested;
    internal int RecordCount { get; set; }
    internal ScreenshotHistoryRail()
    {
        TabStop = true; Cursor = Cursors.Hand; AccessibleName = "展开历史记录";
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); ExpandRequested?.Invoke(this, EventArgs.Empty); } }
    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Enter or Keys.Space || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Enter or Keys.Space) { ExpandRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; } }
    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int n) => ScreenshotRebuildStyle.S(this, n);
        var p = ScreenshotRebuildStyle.Palette; var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        var button = new Rectangle(S(7), S(14), Math.Max(S(20), Width - S(14)), S(66));
        using (var path = WorkspaceDrawing.Round(button, S(5)))
        {
            using var fill = new SolidBrush(p.Secondary); using var edge = new Pen(p.Border);
            g.FillPath(fill, path); g.DrawPath(edge, path);
        }
        WorkspaceSkin.Icon(g, "next", new(Width / 2 - S(5), S(24), S(10), S(10)), p.Accent);
        WorkspaceDrawing.Text(g, UiLanguage.Text("展开\n历史"), Font, new(S(5), S(44), Width - S(10), S(30)), p.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        WorkspaceSkin.Icon(g, "image", new(Width / 2 - S(7), S(102), S(14), S(14)), p.SecondaryText);
        WorkspaceDrawing.Text(g, RecordCount.ToString(), Font, new(0, S(127), Width, S(17)), p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var line = new Pen(p.Border); g.DrawLine(line, Width - 1, 0, Width - 1, Height);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(button, -2, -2), p.Accent, BackColor);
    }
}

