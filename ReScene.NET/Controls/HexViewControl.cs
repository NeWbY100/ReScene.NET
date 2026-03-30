using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReScene.NET.Services;

namespace ReScene.NET.Controls;

public class HexViewControl : UserControl
{
    private const double LineHeight = 18;

    private static readonly Typeface MonoTypeface = new("Cascadia Mono, Consolas, Courier New, monospace");
    private static readonly double Dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;

    private static readonly double CharWidth = new FormattedText(
        "0000000000", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        MonoTypeface, 12, Brushes.Black, Dpi).Width / 10;

    private static readonly double AddressWidth = 10 * CharWidth;

    // Computed from BytesPerLine: each byte is "XX " except the last which has no trailing space
    private double HexWidth => (BytesPerLine * 3 - 1) * CharWidth;
    private double AsciiWidth => BytesPerLine * CharWidth;

    private double _gap1 = CharWidth;
    private double _gap2 = CharWidth;

    private double HexStartX => AddressWidth + _gap1;
    private double AsciiStartX => AddressWidth + _gap1 + HexWidth + _gap2;
    private double TotalContentWidth => AsciiStartX + AsciiWidth + 20;

    public static readonly DependencyProperty DataSourceProperty =
        DependencyProperty.Register(nameof(DataSource), typeof(IHexDataSource), typeof(HexViewControl),
            new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty BlockOffsetProperty =
        DependencyProperty.Register(nameof(BlockOffset), typeof(long), typeof(HexViewControl),
            new PropertyMetadata(0L, OnDataChanged));

    public static readonly DependencyProperty BlockLengthProperty =
        DependencyProperty.Register(nameof(BlockLength), typeof(long), typeof(HexViewControl),
            new PropertyMetadata(0L, OnDataChanged));

    public static readonly DependencyProperty SelectionOffsetProperty =
        DependencyProperty.Register(nameof(SelectionOffset), typeof(long), typeof(HexViewControl),
            new PropertyMetadata(-1L, OnSelectionOffsetChanged));

    public static readonly DependencyProperty SelectionLengthProperty =
        DependencyProperty.Register(nameof(SelectionLength), typeof(long), typeof(HexViewControl),
            new PropertyMetadata(0L, OnSelectionLengthChanged));

    public static readonly DependencyProperty BytesPerLineProperty =
        DependencyProperty.Register(nameof(BytesPerLine), typeof(int), typeof(HexViewControl),
            new PropertyMetadata(16, OnBytesPerLineChanged, CoerceBytesPerLine));

    private readonly HexCanvas _canvas;
    private readonly ScrollViewer _scrollViewer;
    private readonly HexColumnHeader _columnHeader;

    public IHexDataSource? DataSource
    {
        get => (IHexDataSource?)GetValue(DataSourceProperty);
        set => SetValue(DataSourceProperty, value);
    }

    public long BlockOffset
    {
        get => (long)GetValue(BlockOffsetProperty);
        set => SetValue(BlockOffsetProperty, value);
    }

    public long BlockLength
    {
        get => (long)GetValue(BlockLengthProperty);
        set => SetValue(BlockLengthProperty, value);
    }

    public long SelectionOffset
    {
        get => (long)GetValue(SelectionOffsetProperty);
        set => SetValue(SelectionOffsetProperty, value);
    }

    public long SelectionLength
    {
        get => (long)GetValue(SelectionLengthProperty);
        set => SetValue(SelectionLengthProperty, value);
    }

    public int BytesPerLine
    {
        get => (int)GetValue(BytesPerLineProperty);
        set => SetValue(BytesPerLineProperty, value);
    }

    private static object CoerceBytesPerLine(DependencyObject _, object baseValue)
    {
        int val = (int)baseValue;
        return Math.Clamp(val, 1, 128);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs _)
    {
        if (d is HexViewControl c)
        {
            c.RefreshCanvas();
        }
    }

    private static void OnSelectionOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs _)
    {
        if (d is HexViewControl c)
        {
            c._canvas.ClearMouseSelection();
            c._canvas.InvalidateVisual();
        }
    }

    private static void OnSelectionLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs _)
    {
        if (d is HexViewControl c)
        {
            c.OnSelectionChanged();
        }
    }

    private static void OnBytesPerLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs _)
    {
        if (d is HexViewControl c)
        {
            c.RefreshCanvas();
            c._columnHeader.InvalidateVisual();
        }
    }

    public HexViewControl()
    {
        _canvas = new HexCanvas(this)
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _canvas
        };

        _scrollViewer.ScrollChanged += (_, _) =>
        {
            _canvas.InvalidateVisual();
            _columnHeader!.RenderTransform = new TranslateTransform(-_scrollViewer.HorizontalOffset, 0);
        };

        _columnHeader = new HexColumnHeader(this)
        {
            Height = LineHeight
        };

        var headerClipper = new Border
        {
            ClipToBounds = true,
            Child = _columnHeader
        };

        var headerSeparator = new Border
        {
            Height = 1,
        };
        headerSeparator.SetResourceReference(Border.BackgroundProperty, "BorderSeparator");

        var panel = new DockPanel();
        DockPanel.SetDock(headerClipper, Dock.Top);
        DockPanel.SetDock(headerSeparator, Dock.Top);
        panel.Children.Add(headerClipper);
        panel.Children.Add(headerSeparator);
        panel.Children.Add(_scrollViewer);

        Content = panel;
    }

    private void RefreshCanvas()
    {
        int bytesPerLine = BytesPerLine;
        long blockLen = BlockLength;
        long lineCount = blockLen > 0 ? (blockLen + bytesPerLine - 1) / bytesPerLine : 0;
        _canvas.Height = Math.Max(lineCount * LineHeight, 1);
        _canvas.Width = TotalContentWidth;
        _canvas.InvalidateVisual();
    }

    private void OnSelectionChanged()
    {
        _canvas.InvalidateVisual();

        if (SelectionOffset >= 0 && BlockLength > 0)
        {
            int bytesPerLine = BytesPerLine;
            long relOffset = SelectionOffset - BlockOffset;
            if (relOffset >= 0 && relOffset < BlockLength)
            {
                long lineIndex = relOffset / bytesPerLine;
                double targetY = lineIndex * LineHeight;
                double viewportH = _scrollViewer.ViewportHeight;
                double currentY = _scrollViewer.VerticalOffset;

                if (targetY < currentY || targetY > currentY + viewportH - LineHeight)
                {
                    _scrollViewer.ScrollToVerticalOffset(Math.Max(0, targetY - viewportH / 3));
                }
            }
        }
    }

    private class HexColumnHeader(HexViewControl owner) : FrameworkElement
    {
        private readonly HexViewControl _owner = owner;

        private int _dragIndex;
        private double _dragStartX;
        private double _dragStartGap;

        private const double HitTolerance = 4;
        private static readonly double MinGap = 0.5 * CharWidth;

        private double Divider1X => _owner.HexStartX;
        private double Divider2X => _owner.AsciiStartX;

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);

            var brush = GetBrush("ForegroundSecondary", Brushes.Gray);

            var offsetFmt = new FormattedText("Offset", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, 12, brush, Dpi);
            context.DrawText(offsetFmt, new Point(0, 2));

            int bytesPerLine = _owner.BytesPerLine;
            var sb = new StringBuilder();
            for (int i = 0; i < bytesPerLine; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i.ToString("X2"));
            }

            var hexFmt = new FormattedText(sb.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, 12, brush, Dpi);
            context.DrawText(hexFmt, new Point(_owner.HexStartX, 2));

            var asciiFmt = new FormattedText("ASCII", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, 12, brush, Dpi);
            context.DrawText(asciiFmt, new Point(_owner.AsciiStartX, 2));

            var dividerBrush = GetBrush("BorderMedium", Brushes.Gray);
            var dividerPen = new Pen(dividerBrush, 1);
            double line1X = Divider1X - Math.Min(3, _owner._gap1 / 2);
            double line2X = Divider2X - Math.Min(3, _owner._gap2 / 2);
            context.DrawLine(dividerPen, new Point(line1X, 0), new Point(line1X, ActualHeight));
            context.DrawLine(dividerPen, new Point(line2X, 0), new Point(line2X, ActualHeight));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            var pos = e.GetPosition(this);

            if (_dragIndex != 0)
            {
                double delta = pos.X - _dragStartX;
                double newGap = Math.Max(MinGap, _dragStartGap + delta);
                if (_dragIndex == 1)
                {
                    _owner._gap1 = newGap;
                }
                else
                {
                    _owner._gap2 = newGap;
                }

                _owner.RefreshCanvas();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (Math.Abs(pos.X - Divider1X) <= HitTolerance || Math.Abs(pos.X - Divider2X) <= HitTolerance)
            {
                Cursor = Cursors.SizeWE;
            }
            else
            {
                Cursor = Cursors.Arrow;
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            var pos = e.GetPosition(this);

            if (Math.Abs(pos.X - Divider1X) <= HitTolerance)
            {
                _dragIndex = 1;
                _dragStartX = pos.X;
                _dragStartGap = _owner._gap1;
                CaptureMouse();
                e.Handled = true;
            }
            else if (Math.Abs(pos.X - Divider2X) <= HitTolerance)
            {
                _dragIndex = 2;
                _dragStartX = pos.X;
                _dragStartGap = _owner._gap2;
                CaptureMouse();
                e.Handled = true;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (_dragIndex != 0)
            {
                _dragIndex = 0;
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private Brush GetBrush(string key, Brush fallback)
        {
            var resource = _owner.TryFindResource(key);
            return resource is Brush b ? b : fallback;
        }
    }

    private class HexCanvas : FrameworkElement
    {
        private readonly HexViewControl _owner;

        private long _mouseSelAnchor = -1;
        private long _mouseSelCurrent = -1;
        private bool _isMouseSelecting;
        private bool _isAsciiAreaSelection;

        private byte[] _lineBuffer = new byte[16];

        private long MouseSelStart => _mouseSelAnchor < 0 ? -1 : Math.Min(_mouseSelAnchor, _mouseSelCurrent);
        private long MouseSelEnd => _mouseSelAnchor < 0 ? -1 : Math.Max(_mouseSelAnchor, _mouseSelCurrent);
        private long MouseSelLength => _mouseSelAnchor < 0 ? 0 : MouseSelEnd - MouseSelStart + 1;

        public HexCanvas(HexViewControl owner)
        {
            _owner = owner;
            Focusable = true;
            Cursor = Cursors.IBeam;

            var copyHex = new MenuItem { Header = "Copy as Hex" };
            copyHex.Click += (_, _) => CopyToClipboard(asText: false);

            var copyText = new MenuItem { Header = "Copy as Text" };
            copyText.Click += (_, _) => CopyToClipboard(asText: true);

            var selectAll = new MenuItem { Header = "Select All" };
            selectAll.Click += (_, _) => SelectAll();

            var menu = new ContextMenu { Items = { copyHex, copyText, new Separator(), selectAll } };
            menu.Opened += (_, _) =>
            {
                GetActiveSelection(out long s, out long l);
                bool hasSel = s >= 0 && l > 0;
                copyHex.IsEnabled = hasSel;
                copyText.IsEnabled = hasSel;
            };
            ContextMenu = menu;
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        public void ClearMouseSelection()
        {
            _mouseSelAnchor = -1;
            _mouseSelCurrent = -1;
            _isMouseSelecting = false;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            Focus();
            var pos = e.GetPosition(this);
            long byteOffset = HitTestByte(pos, out bool isAscii);

            if (byteOffset >= 0)
            {
                _mouseSelAnchor = byteOffset;
                _mouseSelCurrent = byteOffset;
                _isMouseSelecting = true;
                _isAsciiAreaSelection = isAscii;
                CaptureMouse();
                InvalidateVisual();
            }

            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isMouseSelecting)
            {
                var pos = e.GetPosition(this);
                long byteOffset = HitTestByte(pos, out _);

                if (byteOffset >= 0 && byteOffset != _mouseSelCurrent)
                {
                    _mouseSelCurrent = byteOffset;
                    InvalidateVisual();
                }

                e.Handled = true;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (_isMouseSelecting)
            {
                _isMouseSelecting = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.C)
                {
                    CopyToClipboard(asText: _isAsciiAreaSelection);
                    e.Handled = true;
                }
                else if (e.Key == Key.A)
                {
                    SelectAll();
                    e.Handled = true;
                }
            }
        }

        private void SelectAll()
        {
            if (_owner.BlockLength > 0)
            {
                _mouseSelAnchor = _owner.BlockOffset;
                _mouseSelCurrent = _owner.BlockOffset + _owner.BlockLength - 1;
                _isAsciiAreaSelection = false;
                InvalidateVisual();
            }
        }

        private void GetActiveSelection(out long selStart, out long selLength)
        {
            selStart = MouseSelStart;
            selLength = MouseSelLength;

            if (selStart < 0 || selLength <= 0)
            {
                selStart = _owner.SelectionOffset;
                selLength = _owner.SelectionLength;
            }
        }

        private const int MaxCopyBytes = 10 * 1024 * 1024; // 10 MB

        private void CopyToClipboard(bool asText)
        {
            GetActiveSelection(out long selStart, out long selLength);

            var source = _owner.DataSource;
            if (selStart < 0 || selLength <= 0 || source is null)
            {
                return;
            }

            long blockOffset = _owner.BlockOffset;
            long relStart = Math.Max(0, selStart - blockOffset);
            long len = Math.Min(selLength, _owner.BlockLength - relStart);
            if (len <= 0)
            {
                return;
            }

            // Cap copy size
            int copyLen = (int)Math.Min(len, MaxCopyBytes);
            byte[] buf = new byte[copyLen];
            int read = source.Read(relStart, buf, 0, copyLen);
            if (read <= 0)
            {
                return;
            }

            string text;
            if (asText)
            {
                var sb = new StringBuilder(read);
                for (int i = 0; i < read; i++)
                {
                    byte b = buf[i];
                    sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
                }

                text = sb.ToString();
            }
            else
            {
                var sb = new StringBuilder(read * 3);
                for (int i = 0; i < read; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(buf[i].ToString("X2"));
                }

                text = sb.ToString();
            }

            Clipboard.SetText(text);
        }

        private long HitTestByte(Point pos, out bool isAsciiArea)
        {
            isAsciiArea = false;

            long blockLen = _owner.BlockLength;
            if (blockLen <= 0)
            {
                return -1;
            }

            int bytesPerLine = _owner.BytesPerLine;

            long line = (long)(pos.Y / LineHeight);
            long totalLines = (blockLen + bytesPerLine - 1) / bytesPerLine;
            if (line < 0 || line >= totalLines)
            {
                return -1;
            }

            double hexStartX = _owner.HexStartX;
            double hexEndX = hexStartX + _owner.HexWidth;
            double asciiStartX = _owner.AsciiStartX;
            double asciiEndX = asciiStartX + _owner.AsciiWidth;

            int byteInLine;

            if (pos.X >= hexStartX && pos.X < hexEndX)
            {
                byteInLine = (int)((pos.X - hexStartX) / (3 * CharWidth));
                byteInLine = Math.Clamp(byteInLine, 0, bytesPerLine - 1);
            }
            else if (pos.X >= asciiStartX && pos.X <= asciiEndX)
            {
                byteInLine = (int)((pos.X - asciiStartX) / CharWidth);
                byteInLine = Math.Clamp(byteInLine, 0, bytesPerLine - 1);
                isAsciiArea = true;
            }
            else
            {
                return -1;
            }

            long lineOffset = line * bytesPerLine + byteInLine;
            if (lineOffset >= blockLen)
            {
                return -1;
            }

            return _owner.BlockOffset + lineOffset;
        }

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);

            var source = _owner.DataSource;
            if (source is null || _owner.BlockLength <= 0)
            {
                return;
            }

            var addressBrush = GetBrush("HexOffsetForeground", Brushes.Gray);
            var hexBrush = GetBrush("HexBytesForeground", Brushes.Black);
            var asciiBrush = GetBrush("HexAsciiForeground", Brushes.DimGray);
            var selectionBrush = GetBrush("HexSelectionBrush", new SolidColorBrush(Color.FromArgb(120, 60, 120, 220)));

            long blockStart = _owner.BlockOffset;
            long blockLen = _owner.BlockLength;
            int bytesPerLine = _owner.BytesPerLine;

            // Ensure line buffer is large enough
            if (_lineBuffer.Length < bytesPerLine)
            {
                _lineBuffer = new byte[bytesPerLine];
            }

            long selStart;
            long selLen;
            if (_mouseSelAnchor >= 0)
            {
                selStart = MouseSelStart;
                selLen = MouseSelLength;
            }
            else
            {
                selStart = _owner.SelectionOffset;
                selLen = _owner.SelectionLength;
            }

            long totalLines = (blockLen + bytesPerLine - 1) / bytesPerLine;

            double scrollY = _owner._scrollViewer.VerticalOffset;
            double viewportH = _owner._scrollViewer.ViewportHeight;
            long firstVisible = Math.Max(0, (long)(scrollY / LineHeight) - 1);
            long lastVisible = Math.Min(totalLines - 1, (long)((scrollY + viewportH) / LineHeight) + 1);

            for (long line = firstVisible; line <= lastVisible; line++)
            {
                double y = line * LineHeight;
                long lineFileOffset = blockStart + line * bytesPerLine;
                long lineDataStart = line * bytesPerLine;
                int lineBytes = (int)Math.Min(bytesPerLine, blockLen - lineDataStart);

                // Selection highlight
                if (selStart >= 0 && selLen > 0)
                {
                    long selEnd = selStart + selLen;
                    long lineEnd = lineFileOffset + bytesPerLine;

                    if (selStart < lineEnd && selEnd > lineFileOffset)
                    {
                        int highlightStart = (int)Math.Max(0, selStart - lineFileOffset);
                        int highlightEnd = (int)Math.Min(bytesPerLine, selEnd - lineFileOffset);

                        double hx1 = _owner.HexStartX + highlightStart * 3 * CharWidth;
                        double hx2 = _owner.HexStartX + (highlightEnd * 3 - 1) * CharWidth;
                        context.DrawRectangle(selectionBrush, null, new Rect(hx1, y, hx2 - hx1, LineHeight));

                        double ax1 = _owner.AsciiStartX + highlightStart * CharWidth;
                        double ax2 = _owner.AsciiStartX + highlightEnd * CharWidth;
                        context.DrawRectangle(selectionBrush, null, new Rect(ax1, y, ax2 - ax1, LineHeight));
                    }
                }

                // Address
                string addr = lineFileOffset.ToString("X8");
                var addrText = new FormattedText(addr, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, 12, addressBrush, Dpi);
                context.DrawText(addrText, new Point(0, y + 2));

                // Read this line's bytes from the data source
                int read = source.Read(lineDataStart, _lineBuffer, 0, lineBytes);
                if (read <= 0)
                {
                    continue;
                }

                var hexBuilder = new StringBuilder(bytesPerLine * 3);
                var asciiBuilder = new StringBuilder(bytesPerLine);

                for (int i = 0; i < read; i++)
                {
                    byte b = _lineBuffer[i];
                    if (i > 0) hexBuilder.Append(' ');
                    hexBuilder.Append(b.ToString("X2"));
                    asciiBuilder.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
                }

                var hexText = new FormattedText(hexBuilder.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, 12, hexBrush, Dpi);
                context.DrawText(hexText, new Point(_owner.HexStartX, y + 2));

                var asciiText = new FormattedText(asciiBuilder.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, 12, asciiBrush, Dpi);
                context.DrawText(asciiText, new Point(_owner.AsciiStartX, y + 2));
            }
        }

        private Brush GetBrush(string resourceKey, Brush fallback)
        {
            var resource = _owner.TryFindResource(resourceKey);
            return resource is Brush brush ? brush : fallback;
        }
    }
}
