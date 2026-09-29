namespace ScreenshotTranslationUiTester;

public sealed class RecognitionRegionEditorForm : Form
{
    private enum Tool { Select, Create, Polygon }
    private readonly Bitmap _image;
    private readonly RegionEditorSession _session;
    private readonly List<RecognitionRegion> _automatic;
    private readonly Func<RecognitionRegion, CancellationToken, Task<IReadOnlyList<OcrEngineBlock>>> _ocrRegion;
    private readonly Func<RecognitionRegion, CancellationToken, Task<VisualRegion?>> _visualRegion;
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(14, 17, 23) };
    private readonly PictureBox _canvas = new() { BackColor = Color.FromArgb(14, 17, 23), SizeMode = PictureBoxSizeMode.StretchImage };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly NumericUpDown _order = new() { Minimum = 0, Maximum = 9999, Width = 74 };
    private readonly CheckBox _ignore = new() { Text = UiStrings.IgnoreRegion, AutoSize = true };
    private readonly CheckBox _preserve = new() { Text = UiStrings.PreserveOriginal, AutoSize = true };
    private readonly Label _diagnostics = new() { Dock = DockStyle.Bottom, Height = 150, AutoEllipsis = true, ForeColor = Color.Gainsboro };
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private float _zoom = 1;
    private Tool _tool;
    private PointF? _dragStart;
    private PointF? _dragCurrent;
    private string? _dragRegion;
    private int _dragVertex = -1;
    private bool _syncing;
    private bool _showSourceLines;
    public IReadOnlyList<RecognitionRegion> ResultRegions => _session.Regions.Select(x => x.Clone()).ToArray();

    public RecognitionRegionEditorForm(Bitmap image, IEnumerable<RecognitionRegion> regions,
        Func<RecognitionRegion, CancellationToken, Task<IReadOnlyList<OcrEngineBlock>>> ocrRegion,
        Func<RecognitionRegion, CancellationToken, Task<VisualRegion?>> visualRegion)
    {
        _image = new Bitmap(image); _automatic = regions.Select(x => x.Clone()).ToList(); _session = new(regions);
        _ocrRegion = ocrRegion; _visualRegion = visualRegion;
        Text = UiStrings.RegionEditor; StartPosition = FormStartPosition.CenterParent;
        Size = new(1380, 860); MinimumSize = new(980, 640); KeyPreview = true;
        BuildUi(); UiDarkTheme.Apply(this); RefreshAll();
        _canvas.Paint += PaintCanvas; _canvas.MouseDown += CanvasMouseDown; _canvas.MouseMove += CanvasMouseMove; _canvas.MouseUp += CanvasMouseUp;
        _list.SelectedIndexChanged += (_, _) => { if (_syncing) return; _selected.Clear(); foreach (RegionListItem item in _list.SelectedItems) _selected.Add(item.Region.RegionId); LoadProperties(); _canvas.Invalidate(); };
        _role.Items.AddRange(Enum.GetNames<RegionRoleType>()); _role.SelectionChangeCommitted += (_, _) => ApplyRole();
        _order.ValueChanged += (_, _) => { if (_syncing || _selected.Count != 1) return; _session.SetReadingOrder(_selected.Single(), (int)_order.Value); RefreshAll(); };
        _ignore.CheckedChanged += (_, _) => ApplyFlag(true); _preserve.CheckedChanged += (_, _) => ApplyFlag(false);
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Z) { _session.Undo(); RefreshAll(); } else if (e.Control && e.KeyCode == Keys.Y) { _session.Redo(); RefreshAll(); } else if (e.KeyCode == Keys.Delete) DeleteSelected(); };
    }

    private void BuildUi()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(4), BackColor = Color.FromArgb(29, 34, 44) };
        Button Add(string text, Action action) { var b = new Button { Text = text, AutoSize = true, Height = 34 }; b.Click += (_, _) => action(); bar.Controls.Add(b); return b; }
        Add(UiStrings.SelectMoveResize, () => _tool = Tool.Select); Add(UiStrings.CreateRegion, () => _tool = Tool.Create); Add(UiStrings.EditPolygon, () => _tool = Tool.Polygon);
        Add(UiStrings.DeleteRegion, DeleteSelected); Add(UiStrings.MergeRegions, MergeSelected); Add(UiStrings.SplitHorizontal, () => SplitSelected(false)); Add(UiStrings.SplitVertical, () => SplitSelected(true));
        Add(UiStrings.Undo, () => { _session.Undo(); RefreshAll(); }); Add(UiStrings.Redo, () => { _session.Redo(); RefreshAll(); });
        Add(UiStrings.RerunRegionOcr, async () => await RunRegionOcr()); Add(UiStrings.RerunVisual, async () => await RunVisual());
        Add(UiStrings.ResetSelected, ResetSelected); Add(UiStrings.ResetAll, () => { _session.ResetAllAuto(_automatic); RefreshAll(); });
        Add(UiStrings.SourceLines, () => { _showSourceLines = !_showSourceLines; _canvas.Invalidate(); });
        Add(UiStrings.ZoomIn, () => SetZoom(_zoom * 1.2f)); Add(UiStrings.ZoomOut, () => SetZoom(_zoom / 1.2f)); Add(UiStrings.ActualSize, () => SetZoom(1));
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 1030 };
        _canvas.Image = _image; _scroll.Controls.Add(_canvas); split.Panel1.Controls.Add(_scroll);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new(SizeType.Percent, 100)); right.RowStyles.Add(new(SizeType.Absolute, 80)); right.RowStyles.Add(new(SizeType.Absolute, 150));
        right.Controls.Add(_list, 0, 0);
        var props = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(4) }; props.Controls.AddRange([new Label { Text = UiStrings.RegionType, AutoSize = true }, _role, new Label { Text = UiStrings.ReadingOrder, AutoSize = true }, _order, _ignore, _preserve]); right.Controls.Add(props, 0, 1); right.Controls.Add(_diagnostics, 0, 2); split.Panel2.Controls.Add(right);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.Add(new Button { Text = UiStrings.Apply, DialogResult = DialogResult.OK, Width = 100, Height = 34 }); bottom.Controls.Add(new Button { Text = UiStrings.Cancel, DialogResult = DialogResult.Cancel, Width = 100, Height = 34 });
        Controls.Add(split); Controls.Add(bottom); Controls.Add(bar); AcceptButton = (IButtonControl)bottom.Controls[0]; CancelButton = (IButtonControl)bottom.Controls[1];
    }

    private void SetZoom(float value) { _zoom = Math.Clamp(value, .25f, 4); _canvas.Size = new((int)(_image.Width * _zoom), (int)(_image.Height * _zoom)); _canvas.Invalidate(); }
    private PointF Source(Point p) => new(p.X / _zoom, p.Y / _zoom);
    private PointF Screen(PointF p) => new(p.X * _zoom, p.Y * _zoom);
    private void RefreshAll()
    {
        _syncing = true; _list.BeginUpdate(); _list.Items.Clear();
        foreach (var r in _session.Regions.OrderBy(x => x.ReadingOrder)) { var item = new RegionListItem(r); var index = _list.Items.Add(item); if (_selected.Contains(r.RegionId)) _list.SetSelected(index, true); }
        _list.EndUpdate(); _syncing = false; SetZoom(_zoom); LoadProperties();
    }
    private void LoadProperties()
    {
        _syncing = true; var r = _session.Regions.FirstOrDefault(x => _selected.Contains(x.RegionId));
        if (r is not null) { _role.SelectedItem = r.RoleType.ToString(); _order.Value = Math.Clamp(r.ReadingOrder, 0, 9999); _ignore.Checked = r.IsIgnored; _preserve.Checked = r.PreserveOriginal; _diagnostics.Text = RegionDiagnosticsV2.Describe(r, OcrEngineKind.Rapid, r.DetectionSources.Contains(DetectionSource.VisualModel) ? "Visual Adapter" : "Off"); }
        else _diagnostics.Text = "选择 Region 查看诊断。"; _syncing = false;
    }
    private void PaintCanvas(object? sender, PaintEventArgs e)
    {
        using var normal = new Pen(Color.Lime, 2); using var selected = new Pen(Color.Gold, 3); using var sourceLine = new Pen(Color.DeepSkyBlue, 1) { DashStyle=System.Drawing.Drawing2D.DashStyle.Dash }; using var vertex = new SolidBrush(Color.Cyan); using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        foreach (var r in _session.Regions) { var points = r.Polygon.Select(Screen).ToArray(); if (points.Length < 3) continue; e.Graphics.DrawPolygon(_selected.Contains(r.RegionId) ? selected : normal, points); if(_showSourceLines) foreach(var line in r.SourceLinePolygons){var lp=line.Select(Screen).ToArray();if(lp.Length>=3)e.Graphics.DrawPolygon(sourceLine,lp);} var label=$"#{r.ReadingOrder} {r.RoleType}"; var labelSize=e.Graphics.MeasureString(label,font); var labelY=Math.Max(0,points[0].Y-labelSize.Height); e.Graphics.FillRectangle(Brushes.Black,points[0].X,labelY,labelSize.Width,labelSize.Height); e.Graphics.DrawString(label, font, _selected.Contains(r.RegionId)?Brushes.Gold:Brushes.Lime, points[0].X,labelY); if (_tool == Tool.Polygon && _selected.Contains(r.RegionId)) foreach (var p in points) e.Graphics.FillEllipse(vertex, p.X - 5, p.Y - 5, 10, 10); }
        if (_dragStart is not null && _dragCurrent is not null && _tool == Tool.Create) { var a = Screen(_dragStart.Value); var b = Screen(_dragCurrent.Value); e.Graphics.DrawRectangle(selected, Rectangle.Round(RectangleF.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y)))); }
    }
    private void CanvasMouseDown(object? sender, MouseEventArgs e)
    {
        var p = Source(e.Location); _dragStart = _dragCurrent = p;
        if (_tool == Tool.Create) return;
        var region = _session.Regions.Where(x => x.BoundingBox.Contains(p)).OrderBy(x => x.BoundingBox.Width * x.BoundingBox.Height).FirstOrDefault();
        if (region is null) { if ((ModifierKeys & Keys.Control) == 0) _selected.Clear(); RefreshAll(); return; }
        if ((ModifierKeys & Keys.Control) == 0 && !_selected.Contains(region.RegionId)) _selected.Clear(); _selected.Add(region.RegionId); _dragRegion = region.RegionId;
        if (_tool == Tool.Polygon) { _dragVertex = region.Polygon.Select((v,i)=>(i,d:(v.X-p.X)*(v.X-p.X)+(v.Y-p.Y)*(v.Y-p.Y))).OrderBy(x=>x.d).First().i; }
        else { var b = region.BoundingBox; if (Math.Abs(p.X-b.Right)<12/_zoom && Math.Abs(p.Y-b.Bottom)<12/_zoom) _dragVertex = -2; }
        RefreshAll();
    }
    private void CanvasMouseMove(object? sender, MouseEventArgs e) { if (_dragStart is null) return; _dragCurrent = Source(e.Location); _canvas.Invalidate(); }
    private void CanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (_dragStart is null) return; var end = Source(e.Location); var start = _dragStart.Value;
        if (_tool == Tool.Create) { var box = RectangleF.FromLTRB(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Max(start.X,end.X),Math.Max(start.Y,end.Y)); if (box.Width > 5 && box.Height > 5) _session.Create(GeometryV2.RectanglePolygon(box)); }
        else if (_dragRegion is not null) { var r = _session.Regions.Single(x => x.RegionId == _dragRegion); if (_tool == Tool.Polygon && _dragVertex >= 0) { var poly = r.Polygon.ToArray(); poly[_dragVertex] = end; _session.EditPolygon(r.RegionId, poly); } else if (_dragVertex == -2) { var b = r.BoundingBox; _session.Resize(r.RegionId, RectangleF.FromLTRB(b.Left,b.Top,Math.Max(b.Left+5,end.X),Math.Max(b.Top+5,end.Y))); } else _session.Move(r.RegionId, end.X-start.X, end.Y-start.Y); }
        _dragStart = _dragCurrent = null; _dragRegion = null; _dragVertex = -1; RefreshAll();
    }
    private void DeleteSelected() { if (_selected.Count == 0) return; _session.Delete(_selected); _selected.Clear(); RefreshAll(); }
    private void MergeSelected() { _session.Merge(_selected); _selected.Clear(); RefreshAll(); }
    private void SplitSelected(bool vertical) { if (_selected.Count != 1) return; var r = _session.Regions.Single(x => _selected.Contains(x.RegionId)); var b=r.BoundingBox; PointF[][] parts = vertical ? [GeometryV2.RectanglePolygon(new(b.X,b.Y,b.Width/2,b.Height)),GeometryV2.RectanglePolygon(new(b.X+b.Width/2,b.Y,b.Width/2,b.Height))] : [GeometryV2.RectanglePolygon(new(b.X,b.Y,b.Width,b.Height/2)),GeometryV2.RectanglePolygon(new(b.X,b.Y+b.Height/2,b.Width,b.Height/2))]; _session.Split(r.RegionId, parts); _selected.Clear(); RefreshAll(); }
    private void ApplyRole() { if (_syncing || _role.SelectedItem is null) return; var role=Enum.Parse<RegionRoleType>(_role.SelectedItem.ToString()!); foreach(var id in _selected.ToArray()) _session.SetRole(id,role); RefreshAll(); }
    private void ApplyFlag(bool ignore) { if (_syncing) return; foreach(var id in _selected.ToArray()) { if(ignore) _session.SetIgnored(id,_ignore.Checked); else _session.SetPreserveOriginal(id,_preserve.Checked); } RefreshAll(); }
    private async Task RunRegionOcr() { if (_selected.Count != 1) return; var r=_session.Regions.Single(x=>_selected.Contains(x.RegionId)); Enabled=false; try { _session.ReplaceOcr(r.RegionId, await _ocrRegion(r,CancellationToken.None)); RefreshAll(); } finally { Enabled=true; } }
    private async Task RunVisual() { if (_selected.Count != 1) return; var r=_session.Regions.Single(x=>_selected.Contains(x.RegionId)); Enabled=false; try { var v=await _visualRegion(r,CancellationToken.None); if(v is not null && v.VisualRoleHint!=RegionRoleType.Unknown) _session.SetRole(r.RegionId,v.VisualRoleHint); RefreshAll(); } finally { Enabled=true; } }
    private void ResetSelected() { if (_selected.Count != 1) return; var id=_selected.Single(); var auto=_automatic.FirstOrDefault(x=>x.RegionId==id); if(auto is not null) _session.ResetSelected(id,auto); RefreshAll(); }
    protected override void Dispose(bool disposing) { if(disposing) _image.Dispose(); base.Dispose(disposing); }
    private sealed record RegionListItem(RecognitionRegion Region) { public override string ToString() => $"#{Region.ReadingOrder} {Region.RoleType} {Region.RegionId[4..10]}  {Region.StructuredText}"; }
}
