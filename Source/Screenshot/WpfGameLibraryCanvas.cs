using System.Globalization;
using System.Runtime.CompilerServices;
using W = System.Windows;
using WM = System.Windows.Media;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

internal sealed partial class GameLibraryGrid
{
    private WpfLibraryCanvas? wpfCanvas;
    private Size? nativeViewport;
    internal Size ViewportSize => nativeViewport ?? ClientSize;
    private void SetNativeViewport(Size size)
    {
        if (nativeViewport == size) return;
        bool widthChanged = nativeViewport is { } previous && previous.Width != size.Width;
        if (widthChanged) CaptureResizeAnchor();
        nativeViewport = size;
        UpdateScrollRange();
        if (widthChanged) RestoreResizeAnchor();
    }
    public W.FrameworkElement NativeView => wpfCanvas ??= new WpfLibraryCanvas(this);
    private void InitializeWpfCanvas()
    {
        if (!WpfWorkspace.Enabled) return;
        wpfCanvas = new WpfLibraryCanvas(this);
        // Native views belong directly to the WPF tree. Never nest ElementHost
        // inside WindowsFormsHost: multilevel interop is unsupported.
        Invalidated += (_, _) => wpfCanvas?.InvalidateVisual();
        VisibleChanged += (_, _) => { if (Visible) wpfCanvas?.InvalidateVisual(); };
    }
    internal bool NativeViewVisible => wpfCanvas?.IsVisible == true;
    private new bool Capture
    {
        get => NativeViewVisible ? wpfCanvas!.IsMouseCaptured : base.Capture;
        set { if (NativeViewVisible) { if (value) wpfCanvas!.CaptureMouse(); else wpfCanvas!.ReleaseMouseCapture(); } else base.Capture = value; }
    }
    private new bool Focus() => NativeViewVisible ? wpfCanvas!.Focus() : base.Focus();
    public new Point PointToScreen(Point point) => NativeViewVisible ? WpfControlCoordinates.ToScreen(this, point) : base.PointToScreen(point);
    public new Point PointToClient(Point point) => NativeViewVisible ? WpfControlCoordinates.FromScreen(this, point) : base.PointToClient(point);

    // O(visible rows), including overscan. BoundsFor remains the shared source of
    // truth for rendering, hit testing, box selection and keyboard navigation.
    private IEnumerable<int> VisibleTileIndices(Rectangle viewport)
    {
        if (tiles.Length == 0 || viewport.Width <= 0 || viewport.Height <= 0) yield break;
        var cell = Cell;
        int stride = ShelfMode ? cell.Width + S(12) : cell.Height + S(12);
        int start = ShelfMode ? viewport.Left + scrollOffset.X - S(2) : viewport.Top + scrollOffset.Y - S(3);
        int end = ShelfMode ? viewport.Right + scrollOffset.X : viewport.Bottom + scrollOffset.Y;
        int firstRow = Math.Max(0, (int)Math.Floor(start / (double)Math.Max(1, stride)));
        int lastRow = Math.Max(firstRow, end / Math.Max(1, stride));
        int columns = ShelfMode ? 1 : Columns;
        int first = Math.Min(tiles.Length, firstRow * columns);
        int last = Math.Min(tiles.Length - 1, (lastRow + 1) * columns - 1);
        for (int i = first; i <= last; i++) if (BoundsFor(i).IntersectsWith(viewport)) yield return i;
    }

    private sealed class WpfLibraryCanvas : W.FrameworkElement
    {
        private readonly GameLibraryGrid grid;
        private readonly ConditionalWeakTable<LibraryTile, TileDrawing> drawings = new();
        private bool wheelAnimating;
        private int wheelStart, wheelTarget;
        private long wheelStarted;
        private string? fontFamilyName;
        private WM.Typeface regularTypeface = null!, boldTypeface = null!;
        private sealed class TileDrawing { internal int Key; internal WM.DrawingGroup? Drawing; }
        private double Scale => grid.DeviceDpi / 96d;
        private W.Rect Rect(Rectangle r) => new(r.X / Scale, r.Y / Scale, Math.Max(0, r.Width / Scale), Math.Max(0, r.Height / Scale));
        private static WM.SolidColorBrush Brush(Color c) => WpfDrawing.Brush(c);
        private static WM.Pen Pen(Color color, double width = 1) { var pen = new WM.Pen(Brush(color), width); pen.Freeze(); return pen; }
        internal WpfLibraryCanvas(GameLibraryGrid grid)
        {
            this.grid = grid;
            WpfControlCoordinates.Register(grid, this);
            Focusable = true; ClipToBounds = true; SnapsToDevicePixels = true; UseLayoutRounding = true;
            // The hidden WinForms TableLayoutPanel still owns its child's
            // Bounds. Keep the visible viewport independent of that tree.
            WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
            WM.TextOptions.SetTextRenderingMode(this, WM.TextRenderingMode.ClearType);
            WM.RenderOptions.SetBitmapScalingMode(this, WM.BitmapScalingMode.LowQuality);
            W.Automation.AutomationProperties.SetName(this, "游戏库");
            WpfUiLanguage.Property(this,W.Automation.AutomationProperties.NameProperty,"游戏库");
            MouseDown += (_, e) =>
            {
                if (e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2) return;
                StopWheelMotion();
                var point = Point(e.GetPosition(this));
                var button = e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left;
                grid.OnMouseDown(new(button, e.ClickCount, point.X, point.Y, 0));
                if (e.ClickCount == 2) grid.OnMouseDoubleClick(new(button, 2, point.X, point.Y, 0));
                e.Handled = true;
            };
            MouseUp += (_, e) =>
            {
                if (e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2) return;
                var point = Point(e.GetPosition(this));
                grid.OnMouseUp(new(e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left, 1, point.X, point.Y, 0));
                e.Handled = true;
            };
            MouseMove += (_, e) =>
            {
                var point = Point(e.GetPosition(this));
                grid.OnMouseMove(new(e.LeftButton == WI.MouseButtonState.Pressed ? MouseButtons.Left : MouseButtons.None, 0, point.X, point.Y, 0));
                Cursor = grid.Cursor == Cursors.Hand ? WI.Cursors.Hand : WI.Cursors.Arrow;
            };
            MouseLeave += (_, _) => grid.OnMouseLeave(EventArgs.Empty);
            LostMouseCapture += (_, _) => { grid.scrollDragging = false; grid.selecting = false; grid.boxScrollTimer?.Stop(); InvalidateVisual(); };
            MouseWheel += (_, e) =>
            {
                ScrollWheel(e.Delta);
                e.Handled = true;
            };
            Unloaded += (_, _) => StopWheelMotion();
            IsVisibleChanged += (_, _) => { if (!IsVisible) StopWheelMotion(); };
            KeyDown += (_, e) =>
            {
                StopWheelMotion();
                var key = (Keys)WI.KeyInterop.VirtualKeyFromKey(e.Key == WI.Key.System ? e.SystemKey : e.Key);
                var args = new KeyEventArgs(key | ModifierKeys); grid.OnKeyDown(args); e.Handled = args.Handled;
            };
        }
        private void ScrollWheel(int delta)
        {
            int target = grid.WheelTarget(delta, wheelAnimating ? wheelTarget : grid.ScrollValue);
            if (!W.SystemParameters.ClientAreaAnimation)
            { grid.SetScrollValue(target); return; }
            StartWheelMotion(target);
        }
        private void StartWheelMotion(int target)
        {
            wheelStart = grid.ScrollValue; wheelTarget = target;
            if (wheelStart == wheelTarget) { StopWheelMotion(); return; }
            wheelStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (wheelAnimating) return;
            wheelAnimating = true;
            WM.CompositionTarget.Rendering += AdvanceWheel;
        }
        private void AdvanceWheel(object? sender, EventArgs args)
        {
            if (!IsVisible || grid.IsDisposed) { StopWheelMotion(); return; }
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(wheelStarted).TotalMilliseconds;
            double t = Math.Clamp(elapsed / 120, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            grid.SetScrollValue((int)Math.Round(wheelStart + (wheelTarget - wheelStart) * eased), animated: true);
            if (t >= 1) StopWheelMotion();
        }
        internal void StopWheelMotion()
        {
            if (!wheelAnimating) return;
            wheelAnimating = false;
            WM.CompositionTarget.Rendering -= AdvanceWheel;
        }
        private Point Point(W.Point point) => new((int)Math.Round(point.X * Scale), (int)Math.Round(point.Y * Scale));
        protected override W.Size MeasureOverride(W.Size availableSize)
        {
            if (!grid.ShelfMode) return base.MeasureOverride(availableSize);
            // The recent shelf is an auto-sized row. Its height must already
            // reflect this measure's width, including whether its scrollbar is
            // needed, rather than waiting for a later workspace refresh.
            double width = double.IsInfinity(availableSize.Width) ? grid.ViewportSize.Width / Scale : availableSize.Width;
            int pixels = Math.Max(1, (int)Math.Round(width * Scale));
            return new(Math.Max(0, width), grid.ShelfHeightForWidth(pixels) / Scale);
        }
        protected override W.Size ArrangeOverride(W.Size finalSize)
        {
            // Arrange precedes OnRender; SizeChanged is delivered afterwards.
            // Updating here avoids first painting the old viewport and queuing
            // a second render at each resize step.
            grid.SetNativeViewport(new(Math.Max(1, (int)Math.Round(finalSize.Width * Scale)), Math.Max(1, (int)Math.Round(finalSize.Height * Scale))));
            return finalSize;
        }
        protected override W.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new LibraryPeer(this);
        private sealed class LibraryPeer(WpfLibraryCanvas owner) : W.Automation.Peers.FrameworkElementAutomationPeer(owner)
        {
            protected override string GetClassNameCore() => "FusionGameLibrary";
            protected override string GetNameCore() => UiLanguage.Text("游戏库");
            protected override W.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => W.Automation.Peers.AutomationControlType.List;
        }
        protected override void OnRender(WM.DrawingContext dc)
        {
            using var timing = DesktopRenderMetrics.Measure("library-wpf");
            var p = UiTheme.Current;
            // Resolve shared text state once per canvas paint, rather than
            // querying the native font and constructing faces for each label.
            string family = grid.Font.FontFamily.Name;
            if (fontFamilyName != family)
            {
                var fontFamily = WpfDrawing.FontFamily(family);
                regularTypeface = new(fontFamily, W.FontStyles.Normal, W.FontWeights.Normal, W.FontStretches.Normal);
                boldTypeface = new(fontFamily, W.FontStyles.Normal, W.FontWeights.Bold, W.FontStretches.Normal);
                fontFamilyName = family;
            }
            int presentation = HashCode.Combine(p, family, grid.Font.SizeInPoints, Scale, UiLanguage.Current, CultureInfo.CurrentUICulture.Name);
            dc.DrawRectangle(Brush(p.Main), null, new W.Rect(RenderSize));
            if (grid.tiles.Length == 0)
            {
                Text(dc, UiLanguage.Text(grid.EmptyText), new(0, 0, ActualWidth, ActualHeight), p.SecondaryText, center: true, multiline: true);
                return;
            }
            dc.PushClip(new WM.RectangleGeometry(Rect(grid.ContentViewport)));
            foreach (int i in grid.VisibleTileIndices(grid.ContentViewport))
            {
                var tile = grid.tiles[i]; var r = Rect(grid.BoundsFor(i));
                var holder = drawings.GetValue(tile, _ => new TileDrawing());
                bool selected = grid.selection.Contains(tile.Game.Id), hovered = i == grid.hover, running = grid.runningPaths.Contains(tile.Game.ExePath);
                int key = HashCode.Combine(HashCode.Combine(r.Width, r.Height, grid.Portrait, grid.ShelfMode, grid.ListMode, selected, hovered, running),
                    presentation);
                if (holder.Drawing is null || holder.Key != key)
                {
                    var drawing = new WM.DrawingGroup();
                    using (var local = drawing.Open()) DrawTile(local, tile, new W.Rect(0, 0, r.Width, r.Height), selected, hovered, running);
                    drawing.Freeze(); holder.Drawing = drawing; holder.Key = key;
                }
                dc.PushTransform(new WM.TranslateTransform(r.X, r.Y)); dc.DrawDrawing(holder.Drawing); dc.Pop();
            }
            var selectionBox = grid.selectionBox;
            if (grid.selecting && selectionBox.Width > 0 && selectionBox.Height > 0)
                dc.DrawRectangle(Brush(Color.FromArgb(35, p.Accent)), Pen(p.Accent), Rect(selectionBox));
            dc.Pop();
            if (grid.HasScrollBar)
            {
                var thumb = Rect(grid.ScrollThumb); double thickness = grid.scrollDragging || grid.scrollHover ? 6 : 4;
                var ink = grid.ShelfMode ? new W.Rect(thumb.X, thumb.Y + (thumb.Height - thickness) / 2, thumb.Width, thickness)
                    : new W.Rect(thumb.X + (thumb.Width - thickness) / 2, thumb.Y, thickness, thumb.Height);
                dc.DrawRoundedRectangle(Brush(Color.FromArgb(grid.scrollDragging ? 230 : grid.scrollHover ? 185 : 115, p.SecondaryText)), null, ink, thickness / 2, thickness / 2);
            }
            // Decoding happens on the worker and is bounded to the viewport.
            grid.QueueTileImages();
        }
        private void DrawTile(WM.DrawingContext dc, LibraryTile tile, W.Rect r, bool active, bool hover, bool running)
        {
            var p = UiTheme.Current;
            dc.DrawRoundedRectangle(Brush(p.Secondary), Pen(active ? p.Accent : hover ? p.SecondaryText : p.Border, active ? 2 : 1), r, 12, 12);
            double fontHeight = grid.Font.Height / Scale;
            string second = tile.Annotation.DisplayName.Length > 0 ? tile.Annotation.DisplayName : tile.Engine;
            if (grid.ListMode)
            {
                var imageBox = new W.Rect(6, (r.Height - grid.ListCoverHeight / Scale) / 2, grid.ListCoverWidth / Scale, grid.ListCoverHeight / Scale);
                dc.PushClip(new WM.RectangleGeometry(imageBox, 5, 5));
                dc.DrawRectangle(Brush(p.Control), null, imageBox);
                if (tile.Cover is not null) Image(dc, tile.Cover, imageBox, true);
                else Icon(dc, "game", new(imageBox.X + (imageBox.Width - 32) / 2, imageBox.Y + (imageBox.Height - 32) / 2, 32, 32), p.Accent);
                dc.Pop();
                double x = imageBox.Right + 15, height = fontHeight + 6, width = r.Width - x - 20;
                double yText = Math.Max(8, (r.Height - height * (tile.Annotation.Note.Length > 0 ? 3 : 2)) / 2);
                Text(dc, tile.Game.Name, new(x, yText, Math.Max(1, width - (running ? 80 : 0)), height), p.Text, bold: true);
                Text(dc, second, new(x, yText + height, Math.Max(1, width), height), p.SecondaryText);
                if (tile.Annotation.Note.Length > 0) Text(dc, tile.Annotation.Note, new(x, yText + height * 2, Math.Max(1, width), fontHeight + 4), p.SecondaryText);
                if (running) Text(dc, UiLanguage.Text("运行中"), new(Math.Max(0, r.Width - 85), 7, 75, height), Color.SeaGreen);
                return;
            }
            var cover = new W.Rect(5, 5, Math.Max(1, r.Width - 10), Math.Max(1, r.Width * (grid.Portrait && !grid.ShelfMode ? 1.24 : .52) - 5));
            dc.PushClip(new WM.RectangleGeometry(cover, 5, 5));
            dc.DrawRectangle(Brush(p.Control), null, cover);
            if (tile.Cover is not null) Image(dc, tile.Cover, cover, true);
            else Text(dc, UiLanguage.Text("尚无封面"), cover, p.SecondaryText, center: true);
            dc.Pop();
            double y = cover.Bottom + 6, htext = Math.Max(20, fontHeight + 1);
            var icon = new W.Rect(8, y, 24, 24);
            if (tile.Icon is not null) Image(dc, tile.Icon, icon, false);
            else Icon(dc, "game", new(13, y + 5, 14, 14), p.Accent);
            double tx = icon.Right + 9, tw = Math.Max(1, r.Width - tx - 14);
            Text(dc, tile.Game.Name, new(tx, y - 1, Math.Max(1, tw - 2), htext), p.Text, bold: true);
            Text(dc, second, new(tx, y + htext, Math.Max(1, tw - 8), htext), p.SecondaryText);
            if (running) dc.DrawEllipse(Brush(Color.FromArgb(55, 197, 104)), null, new(cover.Left + 12, cover.Top + 12), 4, 4);
            if (tile.Annotation.Note.Length > 0)
            {
                var paper = tile.Annotation.Color == "lavender" ? Color.FromArgb(231, 226, 252) : tile.Annotation.Color == "mint" ? Color.FromArgb(220, 244, 229) : Color.FromArgb(255, 243, 178);
                double width = Math.Min(cover.Width * 2 / 3, Math.Max(76, tile.Annotation.Note.Length * grid.Font.SizeInPoints + 18));
                dc.PushTransform(new WM.TranslateTransform(cover.Right - width - 5, cover.Bottom - 33));
                dc.PushTransform(new WM.RotateTransform(-5));
                dc.DrawRectangle(Brush(Color.FromArgb(30, 0, 0, 0)), null, new(2, 2, width, 32));
                dc.DrawRectangle(Brush(paper), null, new(0, 0, width, 32));
                Text(dc, tile.Annotation.Note, new(6, 0, Math.Max(1, width - 12), 32), Color.FromArgb(68, 61, 46), size: Math.Max(8, grid.Font.SizeInPoints * .9) * 96 / 72);
                dc.Pop(); dc.Pop();
            }
            if (tile.Annotation.Bookmark)
            {
                double x = cover.Right - 22, top = cover.Top;
                var geometry = new WM.StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new(x, top), true, true);
                    path.PolyLineTo(new W.Point[] { new(x + 10, top), new(x + 10, top + 16), new(x + 5, top + 12), new(x, top + 16) }, true, false);
                }
                geometry.Freeze(); dc.DrawGeometry(Brush(p.Accent), null, geometry);
            }
        }
        private readonly Dictionary<(string, int), WM.Imaging.BitmapSource> icons = [];
        private void Icon(WM.DrawingContext dc, string key, W.Rect rect, Color color)
        {
            if (!icons.TryGetValue((key, color.ToArgb()), out var image)) icons[(key, color.ToArgb())] = image = WpfDrawing.Icon(key, color);
            dc.DrawImage(image, rect);
        }
        private static void Image(WM.DrawingContext dc, Bitmap bitmap, W.Rect box, bool crop)
        {
            var source = WpfDrawing.Image(bitmap);
            double ratio = crop ? Math.Max(box.Width / source.PixelWidth, box.Height / source.PixelHeight) : Math.Min(box.Width / source.PixelWidth, box.Height / source.PixelHeight);
            double width = source.PixelWidth * ratio, height = source.PixelHeight * ratio;
            dc.DrawImage(source, new(box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height));
        }
        private void Text(WM.DrawingContext dc, string text, W.Rect box, Color color, bool bold = false, bool center = false, bool multiline = false, double? size = null)
        {
            if (string.IsNullOrEmpty(text) || box.Width <= 0 || box.Height <= 0) return;
            var typeface = bold ? boldTypeface : regularTypeface;
            var formatted = new WM.FormattedText(text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, typeface,
                size ?? grid.Font.SizeInPoints * 96 / 72, Brush(color), Scale)
            { MaxTextWidth = Math.Max(1, box.Width), Trimming = W.TextTrimming.CharacterEllipsis, TextAlignment = center ? W.TextAlignment.Center : W.TextAlignment.Left };
            if (!multiline) formatted.MaxLineCount = 1;
            dc.PushClip(new WM.RectangleGeometry(box));
            dc.DrawText(formatted, new(box.X, box.Y + Math.Max(0, (box.Height - formatted.Height) / 2)));
            dc.Pop();
        }
    }
}
