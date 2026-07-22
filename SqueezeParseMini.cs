using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace SqueezeParseMini
{

    /// <summary>
    /// Data for a single row in the bar display.
    /// </summary>
    public struct SqueezeEntry
    {
        public string Name;
        public double Value;      // the metric being displayed
        public double MaxValue;   // top value in the current set, used to scale bar width
        public bool IsSelf;
        public Color? BarColorOverride; // set by the plugin each tick based on the single/palette color mode
        public int CureCount;
        public string ValueTextOverride; // when set, used verbatim instead of the normal value/suffix/percent/cures formatting (used by the Hybrid metric)
    }

    /// <summary>
    /// A combatant (or, for zone-wide metrics, an aggregated combatant name)
    /// paired with its computed metric value for the current tick. Used to
    /// rank/sort without relying on LINQ.
    /// </summary>
    public struct CombatantScore
    {
        public string Name;
        public double Value;
        public double SecondaryValue; // used by the Hybrid metric to carry HPS alongside the primary DPS value
        public int CureCount;
    }

    public enum ParseMetric
    {
        EncDPS,
        DPS,
        Damage,
        EncHPS,
        HPSCures,
        Healed,
        PersonalDPS,
        PersonalHPS,
        ZoneDPS,
        ZoneDamage,
        ZoneHPS,
        ZoneHPSCures,
        ZoneHealed,
        HybridDpsHpsCures,
        HybridHpsDpsCures
    }

    public enum TextColorMode
    {
        Auto,
        Fixed
    }

    /// <summary>
    /// Pure rendering logic for the header (encounter/zone name + elapsed
    /// time, up to 3 lines) and the bars themselves - either landscape rows
    /// (stacked top to bottom) or portrait columns (arranged left to right),
    /// sized relative to the top value in the current set. Only as many
    /// rows/columns are drawn as there are actual entries.
    ///
    /// This is a plain class, not a Control: SqueezeOverlayForm renders it
    /// into an off-screen 32bpp ARGB bitmap and pushes that to the screen
    /// via UpdateLayeredWindow (through LayeredWindowNative), which is what
    /// gives real per-pixel alpha instead of the color-key based
    /// transparency an ordinary WinForms Control would be limited to.
    /// </summary>
    public class SqueezeBarsRenderer
    {
        private List<SqueezeEntry> _entries = new List<SqueezeEntry>();
        private string _headerText;

        public Color BarBackColor;
        public Color BarBorderColor;
        public TextColorMode TextMode;
        public Color FixedTextColor;
        public Font LabelFont;
        public int BarHeight;
        public int BarSpacing;
        public int BarGap; // gap between adjacent bars/columns; 0 = touching
        public string MetricSuffix; // e.g. " dps"
        public bool ShowBorders;
        public bool ShowPercent;
        public bool UseGradient;
        public bool ShowCureCount;
        public bool Portrait;

        // Self-highlighting (the local player's own row/column).
        public Color? SelfBarColorOverride;
        public Color? SelfTextColorOverride;
        public bool HighlightSelf;
        public Color HighlightColor;

        public int HeaderHeight;
        public bool ShowHeaderBackground;
        public Color HeaderBackColor;
        public TextColorMode HeaderTextMode;
        public Color HeaderFixedTextColor;
        public Font HeaderFont;

        public int Width; // landscape: desired overlay width. portrait: max column height budget.

        public bool ShowUnlockedOutline;

        public SqueezeBarsRenderer()
        {
            BarBackColor = Color.FromArgb(255, 30, 30, 30);
            BarBorderColor = Color.FromArgb(255, 60, 60, 60);
            TextMode = TextColorMode.Auto;
            FixedTextColor = Color.White;
            LabelFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            BarHeight = 22;
            BarGap = 0;
            BarSpacing = 4;
            MetricSuffix = "";
            ShowBorders = true;
            ShowPercent = false;
            UseGradient = true;
            ShowCureCount = false;
            Portrait = false;

            SelfBarColorOverride = null;
            SelfTextColorOverride = null;
            HighlightSelf = false;
            HighlightColor = Color.Yellow;

            HeaderHeight = 51;
            ShowHeaderBackground = false;
            HeaderBackColor = Color.FromArgb(220, 30, 30, 30);
            HeaderTextMode = TextColorMode.Auto;
            HeaderFixedTextColor = Color.White;
            HeaderFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            _headerText = "";

            Width = 260;
            ShowUnlockedOutline = false;
        }

        /// <summary>
        /// Replace the header text and displayed rows.
        /// </summary>
        public void SetData(List<SqueezeEntry> entries, string headerText)
        {
            _entries = entries ?? new List<SqueezeEntry>();
            _headerText = headerText ?? "";
        }

        /// <summary>
        /// Total pixel width needed. Landscape uses the configured overlay
        /// width directly; portrait grows with the number of columns, but
        /// never shrinks below MinPortraitWidth - with only 1-2 columns the
        /// header text needs more room than the columns alone provide.
        /// </summary>
        public const int MinPortraitWidth = 220;

        public int MeasureWidth()
        {
            if (!Portrait) return Width;
            int barsWidth = _entries.Count * (BarHeight + BarGap);
            int contentWidth = BarSpacing + barsWidth + BarSpacing;
            return Math.Max(contentWidth, MinPortraitWidth);
        }

        /// <summary>
        /// Total pixel height needed. Landscape grows with the number of
        /// rows; portrait uses the configured max column height budget.
        /// </summary>
        public int MeasureHeight()
        {
            if (Portrait)
                return BarSpacing + HeaderHeight + Width + BarSpacing;

            int barsHeight = _entries.Count * (BarHeight + BarGap);
            return BarSpacing + HeaderHeight + barsHeight + BarSpacing;
        }

        public void Draw(Graphics g, int width, int height)
        {
            // Everything drawn is axis-aligned rectangles, so antialiasing
            // isn't needed - and skipping it avoids GDI+ softening the
            // shared edge between two adjacent flush bars into a faint
            // visible seam. TextRenderingHint (below) is independent and
            // still gives antialiased text.
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (Portrait)
                DrawPortraitLayout(g, width, height);
            else
                DrawLandscapeLayout(g, width, height);

            if (ShowUnlockedOutline)
            {
                using (var pen = new Pen(Color.FromArgb(200, Color.Yellow), 2f))
                {
                    g.DrawRectangle(pen, 1, 1, width - 3, height - 3);
                }
            }
        }

        private void DrawLandscapeLayout(Graphics g, int width, int height)
        {
            int fullWidth = width - BarSpacing * 2;
            if (fullWidth < 10) fullWidth = 10;

            int y = BarSpacing;
            var headerRect = new Rectangle(BarSpacing, y, fullWidth, HeaderHeight);
            DrawHeader(g, headerRect);
            y += HeaderHeight;

            foreach (var entry in _entries)
            {
                var trackRect = new Rectangle(BarSpacing, y, fullWidth, BarHeight);
                DrawBar(g, trackRect, entry);
                y += BarHeight + BarGap;
            }
        }

        private void DrawPortraitLayout(Graphics g, int width, int height)
        {
            int fullWidth = width - BarSpacing * 2;
            if (fullWidth < 10) fullWidth = 10;

            var headerRect = new Rectangle(BarSpacing, BarSpacing, fullWidth, HeaderHeight);
            DrawHeader(g, headerRect);

            int columnTop = BarSpacing + HeaderHeight;
            int columnHeight = height - columnTop - BarSpacing;
            if (columnHeight < 10) columnHeight = 10;

            int columnsTotalWidth = _entries.Count * (BarHeight + BarGap);
            int extraSpace = fullWidth - columnsTotalWidth;
            int x = BarSpacing + (extraSpace > 0 ? extraSpace / 2 : 0);
            foreach (var entry in _entries)
            {
                var columnRect = new Rectangle(x, columnTop, BarHeight, columnHeight);
                DrawRotatedBar(g, columnRect, entry);
                x += BarHeight + BarGap;
            }
        }

        /// <summary>
        /// Draws a portrait column by literally reusing DrawBar under a
        /// rotated coordinate transform, so the bar and its text are
        /// guaranteed pixel-identical to a landscape bar - just rotated as
        /// one rigid unit (90 degrees counter-clockwise: what was the left/
        /// name end ends up at the bottom of the column, what was the
        /// right/value end ends up at the top, matching a value growing
        /// upward like a normal column chart).
        /// </summary>
        private void DrawRotatedBar(Graphics g, Rectangle columnRect, SqueezeEntry entry)
        {
            Matrix savedTransform = g.Transform;

            g.TranslateTransform(columnRect.X, columnRect.Bottom);
            g.RotateTransform(-90f);

            var localRect = new Rectangle(0, 0, columnRect.Height, columnRect.Width);
            DrawBar(g, localRect, entry);

            g.Transform = savedTransform;
        }

        private void DrawHeader(Graphics g, Rectangle headerRect)
        {
            if (ShowHeaderBackground)
            {
                using (var bgBrush = new SolidBrush(HeaderBackColor))
                {
                    g.FillRectangle(bgBrush, headerRect);
                }
            }

            if (string.IsNullOrEmpty(_headerText)) return;

            Color textColor;
            if (HeaderTextMode == TextColorMode.Auto)
                textColor = ShowHeaderBackground ? (IsLightColor(HeaderBackColor) ? Color.Black : Color.White) : Color.White;
            else
                textColor = HeaderFixedTextColor;

            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

            using (var textBrush = new SolidBrush(textColor))
            using (var shadowBrush = new SolidBrush(ShadowFor(textColor)))
            {
                var shadowRect = headerRect;
                shadowRect.Offset(1, 1);
                g.DrawString(_headerText, HeaderFont, shadowBrush, shadowRect, format);
                g.DrawString(_headerText, HeaderFont, textBrush, headerRect, format);
            }
        }

        private void DrawBar(Graphics g, Rectangle trackRect, SqueezeEntry entry)
        {
            using (var trackBrush = new SolidBrush(BarBackColor))
            {
                g.FillRectangle(trackBrush, trackRect);
            }

            double pct = entry.MaxValue > 0 ? entry.Value / entry.MaxValue : 0;
            pct = Math.Max(0, Math.Min(1, pct));
            int fillWidth = (int)(trackRect.Width * pct);

            Color barColor;
            if (entry.IsSelf && SelfBarColorOverride.HasValue)
                barColor = SelfBarColorOverride.Value;
            else if (entry.BarColorOverride.HasValue)
                barColor = entry.BarColorOverride.Value;
            else
                barColor = Color.Gray;

            if (fillWidth > 0)
            {
                var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillWidth, trackRect.Height);

                if (UseGradient)
                {
                    using (var fillBrush = new LinearGradientBrush(
                               fillRect,
                               ControlPaint.Light(barColor, 0.15f),
                               ControlPaint.Dark(barColor, 0.10f),
                               LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(fillBrush, fillRect);
                    }
                }
                else
                {
                    using (var fillBrush = new SolidBrush(barColor))
                    {
                        g.FillRectangle(fillBrush, fillRect);
                    }
                }
            }

            if (ShowBorders)
            {
                using (var borderPen = new Pen(BarBorderColor, 1f))
                {
                    g.DrawRectangle(borderPen, trackRect.X, trackRect.Y, trackRect.Width - 1, trackRect.Height - 1);
                }
            }

            if (entry.IsSelf && HighlightSelf)
            {
                using (var hlPen = new Pen(HighlightColor, 2f))
                {
                    g.DrawRectangle(hlPen, trackRect.X, trackRect.Y, trackRect.Width - 1, trackRect.Height - 1);
                }
            }

            string valueText = BuildValueText(entry, pct);
            string label = string.IsNullOrEmpty(entry.Name) ? "" : entry.Name;

            var textRect = Rectangle.Inflate(trackRect, -8, 0);
            int fillRightEdge = trackRect.X + fillWidth;

            Color nameBackground = (fillWidth > 0 && textRect.Left < fillRightEdge) ? barColor : BarBackColor;
            Color valueBackground = (fillWidth > 0 && textRect.Right < fillRightEdge) ? barColor : BarBackColor;

            Color nameColor = ResolveTextColor(nameBackground);
            Color valueColor = ResolveTextColor(valueBackground);

            if (entry.IsSelf && SelfTextColorOverride.HasValue)
            {
                nameColor = SelfTextColorOverride.Value;
                valueColor = SelfTextColorOverride.Value;
            }

            var nameFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            var valueFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            var shadowRect = textRect;
            shadowRect.Offset(1, 1);

            using (var nameShadowBrush = new SolidBrush(ShadowFor(nameColor)))
                g.DrawString(label, LabelFont, nameShadowBrush, shadowRect, nameFormat);
            using (var valueShadowBrush = new SolidBrush(ShadowFor(valueColor)))
                g.DrawString(valueText, LabelFont, valueShadowBrush, shadowRect, valueFormat);

            using (var nameBrush = new SolidBrush(nameColor))
                g.DrawString(label, LabelFont, nameBrush, textRect, nameFormat);
            using (var valueBrush = new SolidBrush(valueColor))
                g.DrawString(valueText, LabelFont, valueBrush, textRect, valueFormat);
        }

        private string BuildValueText(SqueezeEntry entry, double pct)
        {
            if (!string.IsNullOrEmpty(entry.ValueTextOverride))
                return entry.ValueTextOverride;

            string valueText = FormatValue(entry.Value) + MetricSuffix;
            if (ShowPercent && entry.MaxValue > 0)
                valueText += " (" + Math.Round(pct * 100) + "%)";
            if (ShowCureCount)
                valueText += " " + entry.CureCount + "c";
            return valueText;
        }

        private Color ResolveTextColor(Color background)
        {
            if (TextMode == TextColorMode.Auto)
                return IsLightColor(background) ? Color.Black : Color.White;
            return FixedTextColor;
        }

        private static bool IsLightColor(Color c)
        {
            double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            return luminance > 140;
        }

        private static Color ShadowFor(Color textColor)
        {
            return IsLightColor(textColor)
                ? Color.FromArgb(160, Color.Black)
                : Color.FromArgb(160, Color.White);
        }

        public static string FormatValue(double v)
        {
            if (v >= 1000000) return (v / 1000000).ToString("0.##") + "m";
            if (v >= 1000) return (v / 1000).ToString("0.#") + "k";
            return v.ToString("0");
        }
    }

    /// <summary>
    /// Shared Win32 plumbing for real per-pixel-alpha layered windows
    /// (UpdateLayeredWindow), used by both SqueezeOverlayForm (each parse
    /// window) and GridOverlayForm (the full-screen alignment grid), so the
    /// P/Invoke surface only needs to exist and be gotten right once.
    /// </summary>
    internal static class LayeredWindowNative
    {
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TRANSPARENT = 0x20;
        private const int GWL_EXSTYLE = -20;

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int w, int h) { cx = w; cy = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        /// <summary>
        /// Composites whatever the given draw callback renders into a
        /// 32bpp ARGB bitmap and pushes it to the given window via
        /// UpdateLayeredWindow, with alpha (0-255) applied uniformly across
        /// the whole thing.
        /// </summary>
        public static void Push(IntPtr hwnd, int left, int top, int width, int height, int alpha, Action<Graphics> draw)
        {
            if (width < 1) width = 1;
            if (height < 1) height = 1;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                memDc = CreateCompatibleDC(screenDc);

                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;

                IntPtr bits;
                hBitmap = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;

                oldBitmap = SelectObject(memDc, hBitmap);

                using (var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    draw(g);
                }

                var topLoc = new POINT(left, top);
                var size = new SIZE(width, height);
                var srcLoc = new POINT(0, 0);

                int clampedAlpha = alpha;
                if (clampedAlpha < 0) clampedAlpha = 0;
                if (clampedAlpha > 255) clampedAlpha = 255;

                var blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = (byte)clampedAlpha;
                blend.AlphaFormat = AC_SRC_ALPHA;

                UpdateLayeredWindow(hwnd, screenDc, ref topLoc, ref size, memDc, ref srcLoc, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static void ApplyClickThrough(IntPtr hwnd, bool enable)
        {
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            style = enable ? (style | WS_EX_TRANSPARENT) : (style & ~WS_EX_TRANSPARENT);
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }

        public static void BeginDrag(IntPtr hwnd)
        {
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }
    }

    /// <summary>
    /// Borderless, always-on-top overlay window - one per parse window.
    /// Uses a true per-pixel-alpha layered window (via LayeredWindowNative)
    /// rather than Form.TransparencyKey.
    ///
    /// Overall opacity (the per-window opacity slider, plus the idle-fade
    /// feature) is applied uniformly across the whole composited bitmap
    /// rather than baked into individual element colors, so it reliably
    /// affects everything (bars, borders, text) instead of just whatever
    /// sliver of background happens to be showing.
    ///
    /// Movement is gated behind IsUnlocked: when locked, the window is
    /// click-through so it never steals mouse focus from the game. When
    /// unlocked, click-through is removed and the whole window can be
    /// dragged from anywhere to reposition it.
    /// </summary>
    public class SqueezeOverlayForm : Form
    {
        private readonly SqueezeBarsRenderer _bars;
        public SqueezeBarsRenderer Bars { get { return _bars; } }

        /// <summary>Base user-configured opacity (0-255).</summary>
        public int OverlayOpacity;

        /// <summary>Multiplier (0.0-1.0) applied on top of OverlayOpacity, driven by the idle-fade feature.</summary>
        public double FadeMultiplier;

        private bool _isUnlocked;
        public bool IsUnlocked
        {
            get { return _isUnlocked; }
            set
            {
                _isUnlocked = value;
                LayeredWindowNative.ApplyClickThrough(Handle, !value);
                _bars.ShowUnlockedOutline = value;
                UpdateVisual();
            }
        }

        public SqueezeOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(260, 160);

            OverlayOpacity = 255;
            FadeMultiplier = 1.0;

            _bars = new SqueezeBarsRenderer();

            MouseDown += SqueezeOverlayForm_MouseDown;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= LayeredWindowNative.WS_EX_LAYERED;
                // Start click-through by default (locked state) until unlocked.
                cp.ExStyle |= LayeredWindowNative.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        /// <summary>
        /// Re-measures content, resizes the window if needed, and pushes a
        /// freshly composited frame to the screen. Call this whenever the
        /// displayed data or appearance settings change.
        /// </summary>
        public void UpdateVisual()
        {
            if (!IsHandleCreated) return;

            int width = Math.Max(1, _bars.MeasureWidth());
            int height = Math.Max(1, _bars.MeasureHeight());

            if (Width != width || Height != height)
            {
                Width = width;
                Height = height;
            }

            int combinedAlpha = (int)(OverlayOpacity * FadeMultiplier);

            LayeredWindowNative.Push(Handle, Left, Top, width, height, combinedAlpha, delegate(Graphics g)
            {
                _bars.Draw(g, width, height);
            });
        }

        private void SqueezeOverlayForm_MouseDown(object sender, MouseEventArgs e)
        {
            if (!IsUnlocked || e.Button != MouseButtons.Left) return;
            LayeredWindowNative.BeginDrag(Handle);
        }

        protected override bool ShowWithoutActivation
        {
            // Prevents the overlay from stealing focus / bringing itself in front
            // of the game window when its visual updates.
            get { return true; }
        }
    }

    /// <summary>
    /// Full-virtual-screen, click-through, yellow alignment grid shown
    /// while any parse window is unlocked, to help line windows up against
    /// each other. Shared across all parse windows (one grid, not one per
    /// window) since only one is usually being repositioned at a time.
    /// </summary>
    public class GridOverlayForm : Form
    {
        public GridOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= LayeredWindowNative.WS_EX_LAYERED;
                cp.ExStyle |= LayeredWindowNative.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void ShowGrid()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(1, bounds.Width);
            Height = Math.Max(1, bounds.Height);

            if (!Visible) Show();
            UpdateVisual();
        }

        public void UpdateVisual()
        {
            if (!IsHandleCreated) return;
            int width = Width;
            int height = Height;
            LayeredWindowNative.Push(Handle, Left, Top, width, height, 255, delegate(Graphics g)
            {
                DrawGrid(g, width, height);
            });
        }

        private static void DrawGrid(Graphics g, int width, int height)
        {
            const int minorSpacing = 40;
            const int majorEvery = 5; // every 5th minor line is "major" -> 200px increments

            int centerX = width / 2;
            int centerY = height / 2;

            using (var minorPen = new Pen(Color.FromArgb(35, Color.Yellow), 1f))
            using (var majorPen = new Pen(Color.FromArgb(70, Color.Yellow), 1f))
            using (var centerPen = new Pen(Color.FromArgb(120, Color.Yellow), 1.5f))
            {
                int index = 0;
                for (int x = 0; x < width; x += minorSpacing, index++)
                {
                    Pen pen = (index % majorEvery == 0) ? majorPen : minorPen;
                    g.DrawLine(pen, x, 0, x, height);
                }

                index = 0;
                for (int y = 0; y < height; y += minorSpacing, index++)
                {
                    Pen pen = (index % majorEvery == 0) ? majorPen : minorPen;
                    g.DrawLine(pen, 0, y, width, y);
                }

                // True screen-center lines, boldest of all, drawn last so
                // they sit on top of the regular grid.
                g.DrawLine(centerPen, centerX, 0, centerX, height);
                g.DrawLine(centerPen, 0, centerY, width, centerY);
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }

    /// <summary>
    /// Running totals for one combatant across every encounter seen so far
    /// in the current zone, used for the zone-wide metrics. A plain class
    /// (not a struct) so accumulation via dictionary lookup mutates the
    /// shared instance in place.
    /// </summary>
    public class ZoneAggregate
    {
        public double Damage;
        public double Healed;
        public double DurationSeconds;
        public int CureDispels;
    }

    /// <summary>
    /// Everything for one independent parse window: its own overlay, its
    /// own settings tab, its own timer tick logic. Multiple instances can
    /// run side by side (e.g. one tuned for DPS, another for healing).
    /// </summary>
    public class ParseWindowController
    {
        public TabPage Page;
        public SqueezeOverlayForm Overlay;
        public Button BtnRemoveWindow;
        public GroupBox SelfGroupBox;

        private DateTime _lastCombatTime;
        private readonly string _defaultName;

        // ----- Window controls -----
        private CheckBox _chkShowOverlay;
        private TextBox _txtTabName;
        private Button _btnRenameTab;
        private Button _btnResetPosition;

        // ----- Data controls -----
        private NumericUpDown _nudTopCount;
        private ComboBox _cmbMetric;

        // ----- Title bar controls -----
        private CheckBox _chkHeaderLine1;
        private CheckBox _chkHeaderLine2;
        private CheckBox _chkHeaderLine3;
        private CheckBox _chkShowHeaderBackground;
        private ComboBox _cmbHeaderBackColor;
        private ComboBox _cmbHeaderTextColor;

        // ----- Bars controls -----
        private NumericUpDown _nudOverlayWidth;
        private NumericUpDown _nudBarHeight;
        private NumericUpDown _nudBarGap;
        private TrackBar _trkOpacity;
        private CheckBox _chkShowBorders;
        private CheckBox _chkUseGradient;
        private CheckBox _chkShowPercent;
        private CheckBox _chkPortraitMode;

        // ----- Colors controls -----
        private ComboBox _cmbBarBackColor;
        private ComboBox _cmbTextColor;
        private ComboBox _cmbColorMode;
        private ComboBox _cmbColorStyle;
        private ComboBox _cmbSingleColor;
        private ComboBox _cmbPalette;

        // ----- Self controls -----
        private CheckBox _chkSelfBarColorEnabled;
        private ComboBox _cmbSelfBarColor;
        private CheckBox _chkSelfTextColorEnabled;
        private ComboBox _cmbSelfTextColor;
        private CheckBox _chkHighlightSelf;
        private ComboBox _cmbHighlightColor;

        // ----- Auto-fade controls -----
        private CheckBox _chkFadeEnabled;
        private NumericUpDown _nudFadeSeconds;

        public ParseWindowController(string defaultName)
        {
            _defaultName = defaultName;
            _lastCombatTime = DateTime.Now;

            Overlay = new SqueezeOverlayForm();
            Overlay.Left = 200;
            Overlay.Top = 200;
        }

        public void Dispose()
        {
            if (Overlay != null && !Overlay.IsDisposed)
            {
                Overlay.Close();
                Overlay.Dispose();
            }
        }

        // ----- Named color palettes (shared across all windows) -----

        // "Auto-Detect" occupies index 0 in both labels and values (value unused for that slot).
        private static readonly string[] TextColorLabels = new string[]
        {
            "Auto-Detect", "White", "Black", "Red", "Green", "Blue", "Yellow", "Orange",
            "Purple", "Cyan", "Magenta", "Pink", "Gray", "Lime", "Teal", "Gold", "Silver", "Brown", "Navy"
        };

        private static readonly Color[] TextColorValues = new Color[]
        {
            Color.Empty,
            Color.White,
            Color.Black,
            Color.FromArgb(255, 230, 60, 60),
            Color.FromArgb(255, 70, 200, 90),
            Color.FromArgb(255, 90, 140, 255),
            Color.FromArgb(255, 235, 220, 60),
            Color.FromArgb(255, 240, 150, 50),
            Color.FromArgb(255, 175, 110, 230),
            Color.FromArgb(255, 80, 220, 220),
            Color.FromArgb(255, 230, 90, 200),
            Color.FromArgb(255, 250, 140, 180),
            Color.FromArgb(255, 190, 190, 190),
            Color.FromArgb(255, 160, 255, 90),
            Color.FromArgb(255, 70, 210, 190),
            Color.FromArgb(255, 235, 195, 70),
            Color.FromArgb(255, 215, 215, 215),
            Color.FromArgb(255, 175, 120, 80),
            Color.FromArgb(255, 100, 130, 210)
        };

        private static readonly string[] BarBackColorLabels = new string[]
        {
            "Grey", "Black", "Dark Blue", "Dark Red", "Dark Green", "Dark Purple", "Navy", "Charcoal", "White", "Light Grey"
        };

        private static readonly Color[] BarBackColorValues = new Color[]
        {
            Color.FromArgb(255, 30, 30, 30),
            Color.FromArgb(255, 10, 10, 10),
            Color.FromArgb(255, 15, 25, 55),
            Color.FromArgb(255, 55, 15, 15),
            Color.FromArgb(255, 15, 45, 20),
            Color.FromArgb(255, 40, 15, 55),
            Color.FromArgb(255, 10, 15, 40),
            Color.FromArgb(255, 45, 45, 45),
            Color.FromArgb(255, 235, 235, 235),
            Color.FromArgb(255, 180, 180, 180)
        };

        private static readonly string[] NamedColorLabels = new string[]
        {
            "Blue", "Green", "Red", "Orange", "Purple", "Teal", "Pink", "Yellow", "Cyan", "Gray", "White"
        };

        private static readonly Color[] RichNamedColors = new Color[]
        {
            Color.RoyalBlue, Color.ForestGreen, Color.Crimson, Color.DarkOrange, Color.MediumPurple,
            Color.Teal, Color.DeepPink, Color.Goldenrod, Color.DarkTurquoise, Color.DimGray, Color.WhiteSmoke
        };

        private static readonly Color[] PastelNamedColors = new Color[]
        {
            Color.FromArgb(255, 168, 200, 245),
            Color.FromArgb(255, 178, 225, 185),
            Color.FromArgb(255, 245, 180, 180),
            Color.FromArgb(255, 250, 210, 160),
            Color.FromArgb(255, 210, 190, 235),
            Color.FromArgb(255, 175, 225, 220),
            Color.FromArgb(255, 240, 190, 220),
            Color.FromArgb(255, 240, 225, 160),
            Color.FromArgb(255, 185, 220, 240),
            Color.FromArgb(255, 225, 225, 225),
            Color.FromArgb(255, 255, 255, 255)
        };

        private static readonly Color[] RichPalette = new Color[]
        {
            Color.FromArgb(255, 15, 70, 165),
            Color.FromArgb(255, 15, 110, 50),
            Color.FromArgb(255, 165, 25, 25),
            Color.FromArgb(255, 100, 30, 150),
            Color.FromArgb(255, 180, 90, 10),
            Color.FromArgb(255, 10, 120, 120),
            Color.FromArgb(255, 150, 20, 110),
            Color.FromArgb(255, 150, 120, 10),
            Color.FromArgb(255, 20, 100, 160),
            Color.FromArgb(255, 160, 30, 80)
        };

        private static readonly Color[] PastelPalette = new Color[]
        {
            Color.FromArgb(255, 168, 200, 245),
            Color.FromArgb(255, 178, 225, 185),
            Color.FromArgb(255, 245, 180, 180),
            Color.FromArgb(255, 210, 190, 235),
            Color.FromArgb(255, 250, 210, 160),
            Color.FromArgb(255, 175, 225, 220),
            Color.FromArgb(255, 240, 190, 220),
            Color.FromArgb(255, 240, 225, 160),
            Color.FromArgb(255, 185, 220, 240),
            Color.FromArgb(255, 240, 195, 205)
        };

        private static readonly Color[] IbmColorblindPalette = new Color[]
        {
            Color.FromArgb(255, 100, 143, 255),
            Color.FromArgb(255, 120, 94, 240),
            Color.FromArgb(255, 220, 38, 127),
            Color.FromArgb(255, 254, 97, 0),
            Color.FromArgb(255, 255, 176, 0)
        };

        private static readonly Color[] WongColorblindPalette = new Color[]
        {
            Color.FromArgb(255, 0, 0, 0),
            Color.FromArgb(255, 230, 159, 0),
            Color.FromArgb(255, 86, 180, 233),
            Color.FromArgb(255, 0, 158, 115),
            Color.FromArgb(255, 240, 228, 66),
            Color.FromArgb(255, 0, 114, 178),
            Color.FromArgb(255, 213, 94, 0),
            Color.FromArgb(255, 204, 121, 167)
        };

        private static readonly Color[] TolQualitativePalette = new Color[]
        {
            Color.FromArgb(255, 51, 34, 136),
            Color.FromArgb(255, 136, 204, 238),
            Color.FromArgb(255, 68, 170, 153),
            Color.FromArgb(255, 17, 119, 51),
            Color.FromArgb(255, 153, 153, 51),
            Color.FromArgb(255, 221, 204, 119),
            Color.FromArgb(255, 204, 102, 119),
            Color.FromArgb(255, 170, 68, 153)
        };

        private static readonly Color[] TolBrightPalette = new Color[]
        {
            Color.FromArgb(255, 68, 119, 170),
            Color.FromArgb(255, 238, 102, 119),
            Color.FromArgb(255, 34, 136, 51),
            Color.FromArgb(255, 204, 187, 68),
            Color.FromArgb(255, 102, 204, 238),
            Color.FromArgb(255, 170, 51, 119),
            Color.FromArgb(255, 187, 187, 187)
        };

        private Color[] SelectedPalette()
        {
            switch (_cmbPalette.SelectedIndex)
            {
                case 1: return PastelPalette;
                case 2: return IbmColorblindPalette;
                case 3: return WongColorblindPalette;
                case 4: return TolQualitativePalette;
                case 5: return TolBrightPalette;
                default: return RichPalette;
            }
        }

        private Color SelectedSingleColor()
        {
            Color[] set = _cmbColorStyle.SelectedIndex == 1 ? PastelNamedColors : RichNamedColors;
            int idx = _cmbSingleColor.SelectedIndex;
            if (idx < 0 || idx >= set.Length) return set[0];
            return set[idx];
        }

        private Color SelectedSelfBarColor()
        {
            Color[] set = _cmbColorStyle.SelectedIndex == 1 ? PastelNamedColors : RichNamedColors;
            int idx = _cmbSelfBarColor.SelectedIndex;
            if (idx < 0 || idx >= set.Length) return set[0];
            return set[idx];
        }

        private static void ResolveTextColorSelection(int selectedIndex, out TextColorMode mode, out Color fixedColor)
        {
            if (selectedIndex <= 0 || selectedIndex >= TextColorValues.Length)
            {
                mode = TextColorMode.Auto;
                fixedColor = Color.White;
                return;
            }
            mode = TextColorMode.Fixed;
            fixedColor = TextColorValues[selectedIndex];
        }

        private static int ClampIndex(int idx, int length)
        {
            if (idx < 0 || idx >= length) return 0;
            return idx;
        }

        // ----- Settings tab layout -----

        public void BuildUI(TabPage page)
        {
            Page = page;
            Page.Text = _defaultName;

            var scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            Page.Controls.Add(scrollPanel);

            var mainFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = true,
                MaximumSize = new Size(0, 620),
                Padding = new Padding(8)
            };
            scrollPanel.Controls.Add(mainFlow);

            // ----- This window -----
            var windowGroup = CreateSectionGroupBox("This Window");
            var windowLayout = CreateSectionLayout(windowGroup);

            var lblTabName = new Label { Text = "Tab name:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _txtTabName = new TextBox { Text = _defaultName, Width = 120 };
            AddRow(windowLayout, lblTabName, _txtTabName);

            _btnRenameTab = new Button { Text = "Rename tab", AutoSize = true };
            AddRow(windowLayout, _btnRenameTab, null);

            _chkShowOverlay = new CheckBox { Name = "chkShowOverlay", Text = "Show overlay", AutoSize = true, Checked = true };
            AddRow(windowLayout, _chkShowOverlay, null);

            _btnResetPosition = new Button { Text = "Reset overlay position", AutoSize = true };
            AddRow(windowLayout, _btnResetPosition, null);

            BtnRemoveWindow = new Button { Text = "Remove this parse window", AutoSize = true };
            AddRow(windowLayout, BtnRemoveWindow, null);

            mainFlow.Controls.Add(windowGroup);

            // ----- Data -----
            var dataGroup = CreateSectionGroupBox("Data");
            var dataLayout = CreateSectionLayout(dataGroup);

            var lblMetric = new Label { Text = "Metric:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbMetric = new ComboBox { Name = "cmbMetric", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            _cmbMetric.Items.AddRange(new object[] { "EncDPS", "DMG", "ZoneDPS", "ZoneDMG", "PersonalDPS", "EncHPS", "HPS + Cures", "HLD", "ZoneHPS", "ZoneHPS + Cures", "ZoneHLD", "PersonalHPS", "Hybrid DPS+HPS+Cures", "Hybrid HPS+DPS+Cures" });
            _cmbMetric.SelectedIndex = 0;
            AddRow(dataLayout, lblMetric, _cmbMetric);

            var lblTopCount = new Label { Text = "Top rows shown:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _nudTopCount = new NumericUpDown { Name = "nudTopCount", Minimum = 1, Maximum = 24, Value = 6, Width = 60 };
            AddRow(dataLayout, lblTopCount, _nudTopCount);

            mainFlow.Controls.Add(dataGroup);

            // ----- Title Bar -----
            var headerGroup = CreateSectionGroupBox("Title Bar");
            var headerLayout = CreateSectionLayout(headerGroup);

            _chkHeaderLine1 = new CheckBox { Name = "chkHeaderLine1", Text = "Line 1: time | encounter", AutoSize = true, Checked = true };
            AddRow(headerLayout, _chkHeaderLine1, null);

            _chkHeaderLine2 = new CheckBox { Name = "chkHeaderLine2", Text = "Line 2: dmg | dps", AutoSize = true, Checked = true };
            AddRow(headerLayout, _chkHeaderLine2, null);

            _chkHeaderLine3 = new CheckBox { Name = "chkHeaderLine3", Text = "Line 3: hld | hps | cures", AutoSize = true, Checked = true };
            AddRow(headerLayout, _chkHeaderLine3, null);

            _chkShowHeaderBackground = new CheckBox { Name = "chkShowHeaderBackground", Text = "Show title bar background", AutoSize = true };
            AddRow(headerLayout, _chkShowHeaderBackground, null);

            var lblHeaderBackColor = new Label { Text = "Title bar background:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbHeaderBackColor = new ComboBox { Name = "cmbHeaderBackColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Enabled = false };
            foreach (var label in BarBackColorLabels)
                _cmbHeaderBackColor.Items.Add(label);
            _cmbHeaderBackColor.SelectedIndex = 0;
            AddRow(headerLayout, lblHeaderBackColor, _cmbHeaderBackColor);

            var lblHeaderTextColor = new Label { Text = "Title text color:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbHeaderTextColor = new ComboBox { Name = "cmbHeaderTextColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            foreach (var label in TextColorLabels)
                _cmbHeaderTextColor.Items.Add(label);
            _cmbHeaderTextColor.SelectedIndex = 0;
            AddRow(headerLayout, lblHeaderTextColor, _cmbHeaderTextColor);

            mainFlow.Controls.Add(headerGroup);

            // ----- Bars -----
            var barsGroup = CreateSectionGroupBox("Bars");
            var barsLayout = CreateSectionLayout(barsGroup);

            var lblWidth = new Label { Text = "Overlay width:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _nudOverlayWidth = new NumericUpDown { Name = "nudOverlayWidth", Minimum = 150, Maximum = 800, Value = 260, Width = 60 };
            AddRow(barsLayout, lblWidth, _nudOverlayWidth);

            var lblBarHeight = new Label { Text = "Bar height:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _nudBarHeight = new NumericUpDown { Name = "nudBarHeight", Minimum = 14, Maximum = 60, Value = 22, Width = 60 };
            AddRow(barsLayout, lblBarHeight, _nudBarHeight);

            var lblBarGap = new Label { Text = "Bar spacing:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _nudBarGap = new NumericUpDown { Name = "nudBarGap", Minimum = 0, Maximum = 20, Value = 0, Width = 60 };
            AddRow(barsLayout, lblBarGap, _nudBarGap);

            var lblOpacity = new Label { Text = "Overlay opacity:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _trkOpacity = new TrackBar { Name = "trkOpacity", Minimum = 20, Maximum = 255, Value = 255, Width = 150, TickStyle = TickStyle.None };
            AddRow(barsLayout, lblOpacity, _trkOpacity);

            _chkShowBorders = new CheckBox { Name = "chkShowBorders", Text = "Show bar borders", AutoSize = true, Checked = true };
            AddRow(barsLayout, _chkShowBorders, null);

            _chkUseGradient = new CheckBox { Name = "chkUseGradient", Text = "Gradient bar fill (unchecked = solid color)", AutoSize = true, Checked = true };
            AddRow(barsLayout, _chkUseGradient, null);

            _chkShowPercent = new CheckBox { Name = "chkShowPercent", Text = "Show % of top parser", AutoSize = true };
            AddRow(barsLayout, _chkShowPercent, null);

            _chkPortraitMode = new CheckBox { Name = "chkPortraitMode", Text = "Portrait mode (vertical columns)", AutoSize = true };
            AddRow(barsLayout, _chkPortraitMode, null);

            mainFlow.Controls.Add(barsGroup);

            // ----- Colors -----
            var colorsGroup = CreateSectionGroupBox("Colors");
            var colorsLayout = CreateSectionLayout(colorsGroup);

            var lblBarBackColor = new Label { Text = "Bar background:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbBarBackColor = new ComboBox { Name = "cmbBarBackColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            foreach (var label in BarBackColorLabels)
                _cmbBarBackColor.Items.Add(label);
            _cmbBarBackColor.SelectedIndex = 0;
            AddRow(colorsLayout, lblBarBackColor, _cmbBarBackColor);

            var lblTextColor = new Label { Text = "Bar text color:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbTextColor = new ComboBox { Name = "cmbTextColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            foreach (var label in TextColorLabels)
                _cmbTextColor.Items.Add(label);
            _cmbTextColor.SelectedIndex = 0;
            AddRow(colorsLayout, lblTextColor, _cmbTextColor);

            var lblColorMode = new Label { Text = "Bar coloring:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbColorMode = new ComboBox { Name = "cmbColorMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            _cmbColorMode.Items.AddRange(new object[] { "Single Color", "Palette" });
            _cmbColorMode.SelectedIndex = 1;
            AddRow(colorsLayout, lblColorMode, _cmbColorMode);

            var lblColorStyle = new Label { Text = "Single color tint:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbColorStyle = new ComboBox { Name = "cmbColorStyle", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            _cmbColorStyle.Items.AddRange(new object[] { "Rich", "Pastel" });
            _cmbColorStyle.SelectedIndex = 0;
            AddRow(colorsLayout, lblColorStyle, _cmbColorStyle);

            var lblSingleColor = new Label { Text = "Single color:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbSingleColor = new ComboBox { Name = "cmbSingleColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
            foreach (var label in NamedColorLabels)
                _cmbSingleColor.Items.Add(label);
            _cmbSingleColor.SelectedIndex = 0;
            AddRow(colorsLayout, lblSingleColor, _cmbSingleColor);

            var lblPalette = new Label { Text = "Palette:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbPalette = new ComboBox { Name = "cmbPalette", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            _cmbPalette.Items.AddRange(new object[] { "Rich", "Pastel", "IBM Colorblind Safe", "Wong Colorblind Safe", "Tol Qualitative", "Tol Bright" });
            _cmbPalette.SelectedIndex = 0;
            AddRow(colorsLayout, lblPalette, _cmbPalette);

            mainFlow.Controls.Add(colorsGroup);

            // ----- Self (find-me-quickly options) -----
            var selfGroup = CreateSectionGroupBox("Self");
            var selfLayout = CreateSectionLayout(selfGroup);

            _chkSelfBarColorEnabled = new CheckBox { Name = "chkSelfBarColorEnabled", Text = "Custom color for my bar", AutoSize = true };
            AddRow(selfLayout, _chkSelfBarColorEnabled, null);

            var lblSelfBarColor = new Label { Text = "My bar color:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbSelfBarColor = new ComboBox { Name = "cmbSelfBarColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Enabled = false };
            foreach (var label in NamedColorLabels)
                _cmbSelfBarColor.Items.Add(label);
            _cmbSelfBarColor.SelectedIndex = 0;
            AddRow(selfLayout, lblSelfBarColor, _cmbSelfBarColor);

            _chkSelfTextColorEnabled = new CheckBox { Name = "chkSelfTextColorEnabled", Text = "Custom text color for me", AutoSize = true };
            AddRow(selfLayout, _chkSelfTextColorEnabled, null);

            var lblSelfTextColor = new Label { Text = "My text color:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbSelfTextColor = new ComboBox { Name = "cmbSelfTextColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Enabled = false };
            foreach (var label in TextColorLabels)
                _cmbSelfTextColor.Items.Add(label);
            _cmbSelfTextColor.SelectedIndex = 0;
            AddRow(selfLayout, lblSelfTextColor, _cmbSelfTextColor);

            _chkHighlightSelf = new CheckBox { Name = "chkHighlightSelf", Text = "Highlight me with a border", AutoSize = true };
            AddRow(selfLayout, _chkHighlightSelf, null);

            var lblHighlightColor = new Label { Text = "Highlight color:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _cmbHighlightColor = new ComboBox { Name = "cmbHighlightColor", DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Enabled = false };
            foreach (var label in TextColorLabels)
                _cmbHighlightColor.Items.Add(label);
            _cmbHighlightColor.SelectedIndex = 6; // "Yellow"
            AddRow(selfLayout, lblHighlightColor, _cmbHighlightColor);

            mainFlow.Controls.Add(selfGroup);
            SelfGroupBox = selfGroup;

            // ----- Auto-fade -----
            var fadeGroup = CreateSectionGroupBox("Auto-Fade");
            var fadeLayout = CreateSectionLayout(fadeGroup);

            _chkFadeEnabled = new CheckBox { Name = "chkFadeEnabled", Text = "Fade out when idle", AutoSize = true };
            AddRow(fadeLayout, _chkFadeEnabled, null);

            var lblFadeSeconds = new Label { Text = "Fade after (seconds):", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };
            _nudFadeSeconds = new NumericUpDown { Name = "nudFadeSeconds", Minimum = 5, Maximum = 600, Value = 30, Width = 60 };
            AddRow(fadeLayout, lblFadeSeconds, _nudFadeSeconds);

            mainFlow.Controls.Add(fadeGroup);

            // ----- Wire events -----
            _btnRenameTab.Click += (s, e) => { Page.Text = string.IsNullOrEmpty(_txtTabName.Text) ? _defaultName : _txtTabName.Text; };
            _chkShowOverlay.CheckedChanged += (s, e) => { ApplyVisibility(); };
            _btnResetPosition.Click += (s, e) => { Overlay.Left = 200; Overlay.Top = 200; };
            _nudTopCount.ValueChanged += (s, e) => ApplyAppearanceSettings();
            _cmbMetric.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _nudOverlayWidth.ValueChanged += (s, e) => ApplyAppearanceSettings();
            _nudBarHeight.ValueChanged += (s, e) => ApplyAppearanceSettings();
            _nudBarGap.ValueChanged += (s, e) => ApplyAppearanceSettings();
            _trkOpacity.ValueChanged += (s, e) => ApplyAppearanceSettings();
            _chkShowBorders.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _chkUseGradient.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _chkPortraitMode.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _cmbColorStyle.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _cmbTextColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _cmbColorMode.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _cmbSingleColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _cmbPalette.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _cmbBarBackColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _chkShowPercent.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _chkFadeEnabled.CheckedChanged += (s, e) => { };
            _chkHeaderLine1.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _chkHeaderLine2.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _chkHeaderLine3.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _chkShowHeaderBackground.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _cmbHeaderBackColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _cmbHeaderTextColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _chkSelfBarColorEnabled.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _cmbSelfBarColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _chkSelfTextColorEnabled.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _cmbSelfTextColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();
            _chkHighlightSelf.CheckedChanged += (s, e) => ApplyAppearanceSettings();
            _cmbHighlightColor.SelectedIndexChanged += (s, e) => ApplyAppearanceSettings();

            ApplyAppearanceSettings();
            ApplyVisibility();
            Overlay.UpdateVisual();
        }

        private GroupBox CreateSectionGroupBox(string title)
        {
            return new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(360, 0),
                Margin = new Padding(0, 0, 16, 8)
            };
        }

        private TableLayoutPanel CreateSectionLayout(GroupBox owner)
        {
            // Deliberately NOT docked: a Dock=Top child inside an AutoSize
            // parent creates a circular sizing dependency in WinForms that
            // collapses both to almost nothing. A fixed Location with the
            // child AutoSizing on its own is the reliable pattern.
            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 0,
                Location = new Point(10, 20)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            owner.Controls.Add(layout);
            return layout;
        }

        private void AddRow(TableLayoutPanel layout, Control left, Control right)
        {
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int row = layout.RowCount - 1;
            layout.Controls.Add(left, 0, row);
            if (right != null)
                layout.Controls.Add(right, 1, row);
            else
                layout.SetColumnSpan(left, 2);
        }

        // ----- Behavior -----

        private void ApplyVisibility()
        {
            if (_chkShowOverlay.Checked)
            {
                if (!Overlay.Visible)
                    Overlay.Show();
            }
            else
            {
                Overlay.Hide();
            }
        }

        private void ApplyAppearanceSettings()
        {
            Overlay.Bars.Width = (int)_nudOverlayWidth.Value;
            Overlay.Bars.BarHeight = (int)_nudBarHeight.Value;
            Overlay.Bars.BarGap = (int)_nudBarGap.Value;
            Overlay.Bars.ShowBorders = _chkShowBorders.Checked;
            Overlay.Bars.UseGradient = _chkUseGradient.Checked;
            Overlay.Bars.ShowPercent = _chkShowPercent.Checked;
            Overlay.Bars.Portrait = _chkPortraitMode.Checked;

            ParseMetric metric = SelectedMetric();
            Overlay.Bars.MetricSuffix = GetMetricSuffix(metric);
            Overlay.Bars.ShowCureCount = (metric == ParseMetric.HPSCures || metric == ParseMetric.ZoneHPSCures);

            Overlay.OverlayOpacity = _trkOpacity.Value;

            int headerLineCount = 0;
            if (_chkHeaderLine1.Checked) headerLineCount++;
            if (_chkHeaderLine2.Checked) headerLineCount++;
            if (_chkHeaderLine3.Checked) headerLineCount++;
            Overlay.Bars.HeaderHeight = headerLineCount * 17;

            Overlay.Bars.BarBackColor = BarBackColorValues[ClampIndex(_cmbBarBackColor.SelectedIndex, BarBackColorValues.Length)];

            Overlay.Bars.ShowHeaderBackground = _chkShowHeaderBackground.Checked;
            Overlay.Bars.HeaderBackColor = BarBackColorValues[ClampIndex(_cmbHeaderBackColor.SelectedIndex, BarBackColorValues.Length)];

            TextColorMode barTextMode;
            Color barFixedColor;
            ResolveTextColorSelection(_cmbTextColor.SelectedIndex, out barTextMode, out barFixedColor);
            Overlay.Bars.TextMode = barTextMode;
            Overlay.Bars.FixedTextColor = barFixedColor;

            TextColorMode headerTextMode;
            Color headerFixedColor;
            ResolveTextColorSelection(_cmbHeaderTextColor.SelectedIndex, out headerTextMode, out headerFixedColor);
            Overlay.Bars.HeaderTextMode = headerTextMode;
            Overlay.Bars.HeaderFixedTextColor = headerFixedColor;

            Overlay.Bars.SelfBarColorOverride = _chkSelfBarColorEnabled.Checked ? (Color?)SelectedSelfBarColor() : null;

            if (_chkSelfTextColorEnabled.Checked)
            {
                TextColorMode selfTextMode;
                Color selfTextColor;
                ResolveTextColorSelection(_cmbSelfTextColor.SelectedIndex, out selfTextMode, out selfTextColor);
                Overlay.Bars.SelfTextColorOverride = selfTextMode == TextColorMode.Fixed ? (Color?)selfTextColor : null;
            }
            else
            {
                Overlay.Bars.SelfTextColorOverride = null;
            }

            Overlay.Bars.HighlightSelf = _chkHighlightSelf.Checked;
            TextColorMode hlMode;
            Color hlColor;
            ResolveTextColorSelection(_cmbHighlightColor.SelectedIndex, out hlMode, out hlColor);
            Overlay.Bars.HighlightColor = hlMode == TextColorMode.Fixed ? hlColor : Color.Yellow;

            bool singleColorMode = _cmbColorMode.SelectedIndex == 0;
            _cmbSingleColor.Enabled = singleColorMode;
            _cmbColorStyle.Enabled = true; // also used by Self bar color / self section tint
            _cmbPalette.Enabled = !singleColorMode;
            _cmbHeaderBackColor.Enabled = _chkShowHeaderBackground.Checked;
            _cmbSelfBarColor.Enabled = _chkSelfBarColorEnabled.Checked;
            _cmbSelfTextColor.Enabled = _chkSelfTextColorEnabled.Checked;
            _cmbHighlightColor.Enabled = _chkHighlightSelf.Checked;

            Overlay.UpdateVisual();
        }

        private ParseMetric SelectedMetric()
        {
            switch (_cmbMetric.SelectedIndex)
            {
                case 1: return ParseMetric.Damage;
                case 2: return ParseMetric.ZoneDPS;
                case 3: return ParseMetric.ZoneDamage;
                case 4: return ParseMetric.PersonalDPS;
                case 5: return ParseMetric.EncHPS;
                case 6: return ParseMetric.HPSCures;
                case 7: return ParseMetric.Healed;
                case 8: return ParseMetric.ZoneHPS;
                case 9: return ParseMetric.ZoneHPSCures;
                case 10: return ParseMetric.ZoneHealed;
                case 11: return ParseMetric.PersonalHPS;
                case 12: return ParseMetric.HybridDpsHpsCures;
                case 13: return ParseMetric.HybridHpsDpsCures;
                default: return ParseMetric.EncDPS;
            }
        }

        private static bool IsPersonalMetric(ParseMetric metric)
        {
            return metric == ParseMetric.PersonalDPS || metric == ParseMetric.PersonalHPS;
        }

        private static bool IsZoneMetric(ParseMetric metric)
        {
            return metric == ParseMetric.ZoneDPS || metric == ParseMetric.ZoneDamage
                || metric == ParseMetric.ZoneHPS || metric == ParseMetric.ZoneHealed
                || metric == ParseMetric.ZoneHPSCures;
        }

        private static string GetMetricSuffix(ParseMetric metric)
        {
            switch (metric)
            {
                case ParseMetric.EncDPS:
                case ParseMetric.DPS:
                case ParseMetric.ZoneDPS:
                case ParseMetric.PersonalDPS:
                    return " dps";
                case ParseMetric.EncHPS:
                case ParseMetric.HPSCures:
                case ParseMetric.ZoneHPS:
                case ParseMetric.ZoneHPSCures:
                case ParseMetric.PersonalHPS:
                    return " hps";
                default:
                    return "";
            }
        }

        private void UpdateFadeState(bool inCombat)
        {
            if (inCombat)
            {
                _lastCombatTime = DateTime.Now;
                Overlay.FadeMultiplier = 1.0;
                return;
            }

            if (!_chkFadeEnabled.Checked)
            {
                Overlay.FadeMultiplier = 1.0;
                return;
            }

            double idleSeconds = (DateTime.Now - _lastCombatTime).TotalSeconds;
            double fadeAfter = (double)_nudFadeSeconds.Value;

            if (idleSeconds < fadeAfter)
            {
                Overlay.FadeMultiplier = 1.0;
                return;
            }

            const double fadeDurationSeconds = 3.0;
            double fadeElapsed = idleSeconds - fadeAfter;
            double multiplier = 1.0 - Math.Min(1.0, fadeElapsed / fadeDurationSeconds);
            if (multiplier < 0) multiplier = 0;
            Overlay.FadeMultiplier = multiplier;
        }

        /// <summary>
        /// One refresh cycle for this window: pulls current ACT data,
        /// ranks/aggregates per this window's own metric selection, and
        /// pushes a fresh frame. Called by the plugin's shared timer for
        /// every window each tick.
        /// </summary>
        public void Tick(bool inCombat)
        {
            if (!_chkShowOverlay.Checked) return;

            try
            {
                UpdateFadeState(inCombat);

                var zone = ActGlobals.oFormActMain.ActiveZone;
                EncounterData encounter = null;
                if (zone != null)
                    encounter = zone.ActiveEncounter;

                ParseMetric metric = SelectedMetric();
                bool zoneWide = IsZoneMetric(metric);
                bool personal = IsPersonalMetric(metric);
                bool isHybridDpsFirst = metric == ParseMetric.HybridDpsHpsCures;
                bool isHybridHpsFirst = metric == ParseMetric.HybridHpsDpsCures;
                bool isHybrid = isHybridDpsFirst || isHybridHpsFirst;
                string selfName = ActGlobals.charName;

                List<CombatantScore> scored;

                if (zoneWide)
                {
                    if (zone == null)
                    {
                        Overlay.Bars.SetData(new List<SqueezeEntry>(), "No active zone");
                        Overlay.UpdateVisual();
                        return;
                    }
                    scored = BuildZoneWideScores(zone, metric);
                }
                else if (personal)
                {
                    if (encounter == null)
                    {
                        Overlay.Bars.SetData(new List<SqueezeEntry>(), "No active encounter");
                        Overlay.UpdateVisual();
                        return;
                    }

                    CombatantData self = encounter.GetCombatant(selfName);
                    scored = BuildPersonalAbilityScores(self, metric);
                }
                else if (isHybrid)
                {
                    if (encounter == null)
                    {
                        Overlay.Bars.SetData(new List<SqueezeEntry>(), "No active encounter");
                        Overlay.UpdateVisual();
                        return;
                    }

                    scored = new List<CombatantScore>();
                    foreach (CombatantData c in encounter.Items.Values)
                    {
                        if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                        double primary = isHybridDpsFirst ? c.EncDPS : c.EncHPS;
                        double secondary = isHybridDpsFirst ? c.EncHPS : c.EncDPS;
                        if (primary <= 0) continue;
                        scored.Add(new CombatantScore { Name = c.Name, Value = primary, SecondaryValue = secondary, CureCount = c.CureDispels });
                    }
                }
                else
                {
                    if (encounter == null)
                    {
                        Overlay.Bars.SetData(new List<SqueezeEntry>(), "No active encounter");
                        Overlay.UpdateVisual();
                        return;
                    }

                    scored = new List<CombatantScore>();
                    foreach (CombatantData c in encounter.Items.Values)
                    {
                        if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                        double val = GetMetricValue(c, metric);
                        if (val <= 0) continue;
                        scored.Add(new CombatantScore { Name = c.Name, Value = val, CureCount = c.CureDispels });
                    }
                }

                int topCount = (int)_nudTopCount.Value;

                scored.Sort(delegate(CombatantScore a, CombatantScore b)
                {
                    return b.Value.CompareTo(a.Value);
                });

                if (scored.Count > topCount)
                    scored.RemoveRange(topCount, scored.Count - topCount);

                double maxValue = scored.Count > 0 ? scored[0].Value : 0;

                bool multiColor = _cmbColorMode.SelectedIndex == 1;
                Color[] palette = SelectedPalette();
                Color singleColor = SelectedSingleColor();

                var entries = new List<SqueezeEntry>();
                for (int i = 0; i < scored.Count; i++)
                {
                    CombatantScore x = scored[i];

                    string hybridText = null;
                    if (isHybrid)
                    {
                        string firstLabel = isHybridDpsFirst ? "dps" : "hps";
                        string secondLabel = isHybridDpsFirst ? "hps" : "dps";
                        hybridText = SqueezeBarsRenderer.FormatValue(x.Value) + " " + firstLabel
                            + " | " + SqueezeBarsRenderer.FormatValue(x.SecondaryValue) + " " + secondLabel
                            + " | " + x.CureCount + "c";
                    }

                    entries.Add(new SqueezeEntry
                    {
                        Name = x.Name,
                        Value = x.Value,
                        MaxValue = maxValue,
                        IsSelf = string.Equals(x.Name, selfName, StringComparison.OrdinalIgnoreCase),
                        CureCount = x.CureCount,
                        ValueTextOverride = hybridText,
                        BarColorOverride = multiColor ? (Color?)palette[i % palette.Length] : (Color?)singleColor
                    });
                }

                string headerText;
                if (encounter != null)
                {
                    string title = string.IsNullOrEmpty(encounter.Title) ? encounter.ZoneName : encounter.Title;

                    int totalCures = 0;
                    foreach (CombatantData c in encounter.Items.Values)
                    {
                        if (c != null) totalCures += c.CureDispels;
                    }
                    double totalHps = encounter.Duration.TotalSeconds > 0
                        ? encounter.Healed / encounter.Duration.TotalSeconds
                        : 0;

                    string line1 = encounter.DurationS + " | " + title;
                    string line2 = SqueezeBarsRenderer.FormatValue(encounter.Damage) + " dmg"
                        + " | " + SqueezeBarsRenderer.FormatValue(encounter.DPS) + " dps";
                    string line3 = SqueezeBarsRenderer.FormatValue(encounter.Healed) + " hld"
                        + " | " + SqueezeBarsRenderer.FormatValue(totalHps) + " hps"
                        + " | " + totalCures + "c";

                    var headerLines = new List<string>();
                    if (_chkHeaderLine1.Checked) headerLines.Add(line1);
                    if (_chkHeaderLine2.Checked) headerLines.Add(line2);
                    if (_chkHeaderLine3.Checked) headerLines.Add(line3);
                    headerText = string.Join("\n", headerLines.ToArray());
                }
                else
                {
                    headerText = zone != null ? zone.ZoneName : "No active encounter";
                }

                Overlay.Bars.SetData(entries, headerText);
                Overlay.UpdateVisual();
            }
            catch (Exception ex)
            {
                if (ActGlobals.oFormActMain != null)
                    ActGlobals.oFormActMain.WriteExceptionLog(ex, "SqueezeParseMini: refresh failed");
            }
        }

        private static List<CombatantScore> BuildZoneWideScores(ZoneData zone, ParseMetric metric)
        {
            var totals = new Dictionary<string, ZoneAggregate>(StringComparer.OrdinalIgnoreCase);

            foreach (EncounterData enc in zone.Items)
            {
                if (enc == null) continue;

                foreach (CombatantData c in enc.Items.Values)
                {
                    if (c == null || string.IsNullOrEmpty(c.Name)) continue;

                    ZoneAggregate agg;
                    if (!totals.TryGetValue(c.Name, out agg))
                    {
                        agg = new ZoneAggregate();
                        totals[c.Name] = agg;
                    }

                    agg.Damage += c.Damage;
                    agg.Healed += c.Healed;
                    agg.DurationSeconds += c.Duration.TotalSeconds;
                    agg.CureDispels += c.CureDispels;
                }
            }

            var scored = new List<CombatantScore>();
            foreach (KeyValuePair<string, ZoneAggregate> kvp in totals)
            {
                ZoneAggregate agg = kvp.Value;
                double value;

                switch (metric)
                {
                    case ParseMetric.ZoneDamage:
                        value = agg.Damage;
                        break;
                    case ParseMetric.ZoneHealed:
                        value = agg.Healed;
                        break;
                    case ParseMetric.ZoneHPS:
                    case ParseMetric.ZoneHPSCures:
                        value = agg.DurationSeconds > 0 ? agg.Healed / agg.DurationSeconds : 0;
                        break;
                    default:
                        value = agg.DurationSeconds > 0 ? agg.Damage / agg.DurationSeconds : 0;
                        break;
                }

                if (value <= 0) continue;
                scored.Add(new CombatantScore { Name = kvp.Key, Value = value, CureCount = agg.CureDispels });
            }

            return scored;
        }

        private static List<CombatantScore> BuildPersonalAbilityScores(CombatantData self, ParseMetric metric)
        {
            var scored = new List<CombatantScore>();
            if (self == null) return scored;

            SortedList<string, AttackType> abilities = metric == ParseMetric.PersonalHPS
                ? FindOutgoingAbilities(self, "Healed (Out)", "heal")
                : FindOutgoingAbilities(self, "Outgoing Damage", "damage");

            if (abilities == null) return scored;

            foreach (AttackType at in abilities.Values)
            {
                if (at == null || string.IsNullOrEmpty(at.Type)) continue;
                double val = at.EncDPS;
                if (val <= 0) continue;
                scored.Add(new CombatantScore { Name = at.Type, Value = val });
            }

            return scored;
        }

        private static SortedList<string, AttackType> FindOutgoingAbilities(CombatantData c, string exactName, string fallbackContains)
        {
            if (c == null || c.Items == null) return null;

            foreach (DamageTypeData dtd in c.Items.Values)
            {
                if (dtd == null || string.IsNullOrEmpty(dtd.Type)) continue;
                if (string.Equals(dtd.Type, exactName, StringComparison.OrdinalIgnoreCase))
                    return dtd.Items;
            }

            if (!string.IsNullOrEmpty(fallbackContains))
            {
                foreach (DamageTypeData dtd in c.Items.Values)
                {
                    if (dtd == null || string.IsNullOrEmpty(dtd.Type)) continue;
                    if (dtd.Type.ToLowerInvariant().Contains(fallbackContains))
                        return dtd.Items;
                }
            }

            return null;
        }

        private static double GetMetricValue(CombatantData c, ParseMetric metric)
        {
            switch (metric)
            {
                case ParseMetric.DPS: return c.DPS;
                case ParseMetric.Damage: return c.Damage;
                case ParseMetric.EncHPS:
                case ParseMetric.HPSCures:
                    return c.EncHPS;
                case ParseMetric.Healed: return c.Healed;
                default: return c.EncDPS;
            }
        }

        // ----- Settings persistence -----

        public void SaveToXml(System.Xml.XmlElement parent)
        {
            System.Xml.XmlDocument doc = parent.OwnerDocument;
            System.Xml.XmlElement node = doc.CreateElement("ParseWindow");
            parent.AppendChild(node);

            AttachChildNode(node, "TabName", Page.Text);
            AttachChildNode(node, "ShowOverlay", _chkShowOverlay.Checked.ToString());
            AttachChildNode(node, "TopCount", _nudTopCount.Value.ToString());
            AttachChildNode(node, "Metric", _cmbMetric.SelectedIndex.ToString());
            AttachChildNode(node, "OverlayWidth", _nudOverlayWidth.Value.ToString());
            AttachChildNode(node, "BarHeight", _nudBarHeight.Value.ToString());
            AttachChildNode(node, "BarGap", _nudBarGap.Value.ToString());
            AttachChildNode(node, "Opacity", _trkOpacity.Value.ToString());
            AttachChildNode(node, "ShowBorders", _chkShowBorders.Checked.ToString());
            AttachChildNode(node, "UseGradient", _chkUseGradient.Checked.ToString());
            AttachChildNode(node, "PortraitMode", _chkPortraitMode.Checked.ToString());
            AttachChildNode(node, "ColorStyle", _cmbColorStyle.SelectedIndex.ToString());
            AttachChildNode(node, "TextColor", _cmbTextColor.SelectedIndex.ToString());
            AttachChildNode(node, "ColorMode", _cmbColorMode.SelectedIndex.ToString());
            AttachChildNode(node, "SingleColor", _cmbSingleColor.SelectedIndex.ToString());
            AttachChildNode(node, "Palette", _cmbPalette.SelectedIndex.ToString());
            AttachChildNode(node, "BarBackColor", _cmbBarBackColor.SelectedIndex.ToString());
            AttachChildNode(node, "ShowPercent", _chkShowPercent.Checked.ToString());
            AttachChildNode(node, "FadeEnabled", _chkFadeEnabled.Checked.ToString());
            AttachChildNode(node, "FadeSeconds", _nudFadeSeconds.Value.ToString());
            AttachChildNode(node, "HeaderLine1", _chkHeaderLine1.Checked.ToString());
            AttachChildNode(node, "HeaderLine2", _chkHeaderLine2.Checked.ToString());
            AttachChildNode(node, "HeaderLine3", _chkHeaderLine3.Checked.ToString());
            AttachChildNode(node, "ShowHeaderBackground", _chkShowHeaderBackground.Checked.ToString());
            AttachChildNode(node, "HeaderBackColor", _cmbHeaderBackColor.SelectedIndex.ToString());
            AttachChildNode(node, "HeaderTextColor", _cmbHeaderTextColor.SelectedIndex.ToString());
            AttachChildNode(node, "SelfBarColorEnabled", _chkSelfBarColorEnabled.Checked.ToString());
            AttachChildNode(node, "SelfBarColor", _cmbSelfBarColor.SelectedIndex.ToString());
            AttachChildNode(node, "SelfTextColorEnabled", _chkSelfTextColorEnabled.Checked.ToString());
            AttachChildNode(node, "SelfTextColor", _cmbSelfTextColor.SelectedIndex.ToString());
            AttachChildNode(node, "HighlightSelf", _chkHighlightSelf.Checked.ToString());
            AttachChildNode(node, "HighlightColor", _cmbHighlightColor.SelectedIndex.ToString());
            AttachChildNode(node, "OverlayX", Overlay.Left.ToString());
            AttachChildNode(node, "OverlayY", Overlay.Top.ToString());
        }

        public void LoadFromXml(System.Xml.XmlNode node)
        {
            string tabName = RetrieveString(node, "TabName", _defaultName);
            _txtTabName.Text = tabName;
            Page.Text = tabName;

            _chkShowOverlay.Checked = RetrieveBool(node, "ShowOverlay", _chkShowOverlay.Checked);
            _nudTopCount.Value = RetrieveDecimal(node, "TopCount", _nudTopCount.Value, _nudTopCount.Minimum, _nudTopCount.Maximum);
            _cmbMetric.SelectedIndex = RetrieveInt(node, "Metric", _cmbMetric.SelectedIndex, 0, _cmbMetric.Items.Count - 1);
            _nudOverlayWidth.Value = RetrieveDecimal(node, "OverlayWidth", _nudOverlayWidth.Value, _nudOverlayWidth.Minimum, _nudOverlayWidth.Maximum);
            _nudBarHeight.Value = RetrieveDecimal(node, "BarHeight", _nudBarHeight.Value, _nudBarHeight.Minimum, _nudBarHeight.Maximum);
            _nudBarGap.Value = RetrieveDecimal(node, "BarGap", _nudBarGap.Value, _nudBarGap.Minimum, _nudBarGap.Maximum);
            _trkOpacity.Value = RetrieveInt(node, "Opacity", _trkOpacity.Value, _trkOpacity.Minimum, _trkOpacity.Maximum);
            _chkShowBorders.Checked = RetrieveBool(node, "ShowBorders", _chkShowBorders.Checked);
            _chkUseGradient.Checked = RetrieveBool(node, "UseGradient", _chkUseGradient.Checked);
            _chkPortraitMode.Checked = RetrieveBool(node, "PortraitMode", _chkPortraitMode.Checked);
            _cmbColorStyle.SelectedIndex = RetrieveInt(node, "ColorStyle", _cmbColorStyle.SelectedIndex, 0, _cmbColorStyle.Items.Count - 1);
            _cmbTextColor.SelectedIndex = RetrieveInt(node, "TextColor", _cmbTextColor.SelectedIndex, 0, _cmbTextColor.Items.Count - 1);
            _cmbColorMode.SelectedIndex = RetrieveInt(node, "ColorMode", _cmbColorMode.SelectedIndex, 0, _cmbColorMode.Items.Count - 1);
            _cmbSingleColor.SelectedIndex = RetrieveInt(node, "SingleColor", _cmbSingleColor.SelectedIndex, 0, _cmbSingleColor.Items.Count - 1);
            _cmbPalette.SelectedIndex = RetrieveInt(node, "Palette", _cmbPalette.SelectedIndex, 0, _cmbPalette.Items.Count - 1);
            _cmbBarBackColor.SelectedIndex = RetrieveInt(node, "BarBackColor", _cmbBarBackColor.SelectedIndex, 0, _cmbBarBackColor.Items.Count - 1);
            _chkShowPercent.Checked = RetrieveBool(node, "ShowPercent", _chkShowPercent.Checked);
            _chkFadeEnabled.Checked = RetrieveBool(node, "FadeEnabled", _chkFadeEnabled.Checked);
            _nudFadeSeconds.Value = RetrieveDecimal(node, "FadeSeconds", _nudFadeSeconds.Value, _nudFadeSeconds.Minimum, _nudFadeSeconds.Maximum);
            _chkHeaderLine1.Checked = RetrieveBool(node, "HeaderLine1", _chkHeaderLine1.Checked);
            _chkHeaderLine2.Checked = RetrieveBool(node, "HeaderLine2", _chkHeaderLine2.Checked);
            _chkHeaderLine3.Checked = RetrieveBool(node, "HeaderLine3", _chkHeaderLine3.Checked);
            _chkShowHeaderBackground.Checked = RetrieveBool(node, "ShowHeaderBackground", _chkShowHeaderBackground.Checked);
            _cmbHeaderBackColor.SelectedIndex = RetrieveInt(node, "HeaderBackColor", _cmbHeaderBackColor.SelectedIndex, 0, _cmbHeaderBackColor.Items.Count - 1);
            _cmbHeaderTextColor.SelectedIndex = RetrieveInt(node, "HeaderTextColor", _cmbHeaderTextColor.SelectedIndex, 0, _cmbHeaderTextColor.Items.Count - 1);
            _chkSelfBarColorEnabled.Checked = RetrieveBool(node, "SelfBarColorEnabled", _chkSelfBarColorEnabled.Checked);
            _cmbSelfBarColor.SelectedIndex = RetrieveInt(node, "SelfBarColor", _cmbSelfBarColor.SelectedIndex, 0, _cmbSelfBarColor.Items.Count - 1);
            _chkSelfTextColorEnabled.Checked = RetrieveBool(node, "SelfTextColorEnabled", _chkSelfTextColorEnabled.Checked);
            _cmbSelfTextColor.SelectedIndex = RetrieveInt(node, "SelfTextColor", _cmbSelfTextColor.SelectedIndex, 0, _cmbSelfTextColor.Items.Count - 1);
            _chkHighlightSelf.Checked = RetrieveBool(node, "HighlightSelf", _chkHighlightSelf.Checked);
            _cmbHighlightColor.SelectedIndex = RetrieveInt(node, "HighlightColor", _cmbHighlightColor.SelectedIndex, 0, _cmbHighlightColor.Items.Count - 1);
            Overlay.Left = RetrieveInt(node, "OverlayX", Overlay.Left, -10000, 10000);
            Overlay.Top = RetrieveInt(node, "OverlayY", Overlay.Top, -10000, 10000);

            ApplyAppearanceSettings();
            ApplyVisibility();
        }

        private static void AttachChildNode(System.Xml.XmlElement parent, string name, string value)
        {
            System.Xml.XmlElement child = parent.OwnerDocument.CreateElement(name);
            child.InnerText = value ?? "";
            parent.AppendChild(child);
        }

        private static string RetrieveString(System.Xml.XmlNode root, string name, string defaultVal)
        {
            System.Xml.XmlNode node = root.SelectSingleNode(name);
            if (node == null || string.IsNullOrEmpty(node.InnerText)) return defaultVal;
            return node.InnerText;
        }

        private static bool RetrieveBool(System.Xml.XmlNode root, string name, bool defaultVal)
        {
            System.Xml.XmlNode node = root.SelectSingleNode(name);
            if (node == null) return defaultVal;
            bool result;
            if (bool.TryParse(node.InnerText, out result)) return result;
            return defaultVal;
        }

        private static int RetrieveInt(System.Xml.XmlNode root, string name, int defaultVal, int min, int max)
        {
            System.Xml.XmlNode node = root.SelectSingleNode(name);
            if (node == null) return defaultVal;
            int result;
            if (!int.TryParse(node.InnerText, out result)) return defaultVal;
            if (result < min) result = min;
            if (result > max) result = max;
            return result;
        }

        private static decimal RetrieveDecimal(System.Xml.XmlNode root, string name, decimal defaultVal, decimal min, decimal max)
        {
            System.Xml.XmlNode node = root.SelectSingleNode(name);
            if (node == null) return defaultVal;
            decimal result;
            if (!decimal.TryParse(node.InnerText, out result)) return defaultVal;
            if (result < min) result = min;
            if (result > max) result = max;
            return result;
        }
    }

    public class SqueezeParseMiniPlugin : IActPluginV1
    {
        // Bump this with every release you push, and keep version.txt in the
        // repo (see VersionCheckUrl below) in sync with it.
        private const string CurrentVersion = "1.0.0";

        // Fill these in with your actual GitHub repo details once it's set up:
        // - VersionCheckUrl should point at a plain text file containing just
        //   the version number (e.g. "1.0.1"), nothing else.
        // - DownloadUrl should point at the raw .cs source of the latest release.
        private const string VersionCheckUrl = "https://raw.githubusercontent.com/YOUR_USERNAME/YOUR_REPO/main/version.txt";
        private const string DownloadUrl = "https://raw.githubusercontent.com/YOUR_USERNAME/YOUR_REPO/main/SqueezeParseMini.cs";

        private Label _pluginStatusText;
        private string _settingsFilePath;

        private TabControl _tabControl;
        private readonly List<ParseWindowController> _windows = new List<ParseWindowController>();
        private GridOverlayForm _gridOverlay;
        private Timer _refreshTimer;
        private int _nextWindowNumber;

        private Button _btnAddWindow;
        private CheckBox _chkUnlockAll;
        private GroupBox _generalGroup;
        private Button _btnSaveSettings;

        private Label _lblVersion;
        private Label _lblUpdateStatus;
        private Button _btnCheckUpdate;
        private Button _btnDownloadUpdate;
        private TextBox _txtLocalFilePath;
        private string _latestKnownVersion;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _pluginStatusText = pluginStatusText;
            _nextWindowNumber = 1;

            _gridOverlay = new GridOverlayForm();

            _settingsFilePath = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName, "Config", "SqueezeParseMini.config.xml");

            BuildRootUI(pluginScreenSpace);

            if (!LoadSettings())
            {
                // Fresh install / no saved file yet - start with one default window.
                AddWindow(null);
            }

            // Defer until after WinForms has actually laid out the tab content -
            // during InitPlugin the plugin tab may not have a real size yet.
            if (pluginScreenSpace.IsHandleCreated)
                pluginScreenSpace.BeginInvoke(new MethodInvoker(SyncGeneralWidth));
            else
                SyncGeneralWidth();

            CheckForUpdates();

            _refreshTimer = new Timer { Interval = 500 };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();

            _pluginStatusText.Text = "SqueezeParseMini: initialized";
        }

        public void DeInitPlugin()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
            }

            SaveSettings();

            foreach (ParseWindowController w in _windows)
            {
                w.Dispose();
            }

            if (_gridOverlay != null && !_gridOverlay.IsDisposed)
            {
                _gridOverlay.Close();
                _gridOverlay.Dispose();
            }

            _pluginStatusText.Text = "SqueezeParseMini: closed";
        }

        private void BuildRootUI(TabPage pluginScreenSpace)
        {
            pluginScreenSpace.Text = "SqueezeParseMini";

            // TabControl (Fill) is added to the parent BEFORE General (Top).
            // WinForms resolves docked siblings in reverse of add order - the
            // last-added docked control claims its edge first - so adding
            // Fill first and Top last is what keeps them from overlapping
            // (adding Top first was what let the TabControl's Fill claim the
            // whole client area and cover the tab strip).
            _tabControl = new TabControl { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed };
            _tabControl.DrawItem += TabControl_DrawItem;
            _tabControl.SelectedIndexChanged += (s, e) => SyncGeneralWidth();
            pluginScreenSpace.Controls.Add(_tabControl);

            var generalBand = new Panel { Dock = DockStyle.Top, Height = 130 };

            _generalGroup = new GroupBox
            {
                Text = "General",
                Location = new Point(8, 4),
                Width = 400, // placeholder - SyncGeneralWidth() sizes this to match the Self section once laid out
                Height = 120,
                Padding = new Padding(8)
            };

            var generalFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true
            };

            _btnAddWindow = new Button { Text = "Add parse window", AutoSize = true, Margin = new Padding(0, 4, 12, 0) };
            _btnAddWindow.Click += (s, e) => { AddWindow(null); SaveSettings(); SyncGeneralWidth(); };
            generalFlow.Controls.Add(_btnAddWindow);

            _btnSaveSettings = new Button { Text = "Save settings", AutoSize = true, Margin = new Padding(0, 4, 12, 0) };
            _btnSaveSettings.Click += (s, e) =>
            {
                SaveSettings();
                _pluginStatusText.Text = "SqueezeParseMini: settings saved";
            };
            generalFlow.Controls.Add(_btnSaveSettings);

            _chkUnlockAll = new CheckBox { Text = "Unlock to move (all windows)", AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
            _chkUnlockAll.CheckedChanged += (s, e) =>
            {
                foreach (ParseWindowController w in _windows)
                {
                    w.Overlay.IsUnlocked = _chkUnlockAll.Checked;
                }

                if (_chkUnlockAll.Checked)
                    _gridOverlay.ShowGrid();
                else
                    _gridOverlay.Hide();
            };
            generalFlow.Controls.Add(_chkUnlockAll);

            _lblVersion = new Label { Text = "Version: " + CurrentVersion, AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            generalFlow.Controls.Add(_lblVersion);

            _btnCheckUpdate = new Button { Text = "Check for updates", AutoSize = true, Margin = new Padding(12, 4, 12, 0) };
            _btnCheckUpdate.Click += (s, e) => CheckForUpdates();
            generalFlow.Controls.Add(_btnCheckUpdate);

            _lblUpdateStatus = new Label { Text = "Not checked yet", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 8, 12, 0) };
            generalFlow.Controls.Add(_lblUpdateStatus);

            _btnDownloadUpdate = new Button { Text = "Download update", AutoSize = true, Enabled = false, Margin = new Padding(0, 4, 12, 0) };
            _btnDownloadUpdate.Click += (s, e) => DownloadUpdate();
            generalFlow.Controls.Add(_btnDownloadUpdate);

            var lblFilePath = new Label { Text = "Plugin file path:", AutoSize = true, Margin = new Padding(12, 8, 4, 0) };
            generalFlow.Controls.Add(lblFilePath);

            _txtLocalFilePath = new TextBox { Width = 280, Margin = new Padding(0, 4, 0, 0) };
            _txtLocalFilePath.TextChanged += (s, e) => SaveSettings();
            generalFlow.Controls.Add(_txtLocalFilePath);

            _generalGroup.Controls.Add(generalFlow);
            generalBand.Controls.Add(_generalGroup);
            pluginScreenSpace.Controls.Add(generalBand);
        }

        /// <summary>
        /// Resizes General so its right edge lines up with the right edge of
        /// the currently selected tab's "Self" section, instead of guessing
        /// a fixed column count (sections wrap into a different number of
        /// columns depending on total content height).
        /// </summary>
        private void SyncGeneralWidth()
        {
            if (_generalGroup == null || _tabControl.SelectedTab == null) return;

            ParseWindowController active = null;
            foreach (ParseWindowController w in _windows)
            {
                if (w.Page == _tabControl.SelectedTab) { active = w; break; }
            }
            if (active == null || active.SelfGroupBox == null) return;

            int measuredRight = active.SelfGroupBox.Right;
            if (measuredRight < 300) return; // not laid out yet - try again later

            _generalGroup.Width = measuredRight + 16;
        }

        private ParseWindowController AddWindow(string presetName)
        {
            string name = presetName;
            if (string.IsNullOrEmpty(name))
            {
                name = "Parse Window " + _nextWindowNumber;
            }
            _nextWindowNumber++;

            var page = new TabPage(name);
            _tabControl.TabPages.Add(page);

            var controller = new ParseWindowController(name);
            controller.BuildUI(page);
            controller.Overlay.IsUnlocked = _chkUnlockAll != null && _chkUnlockAll.Checked;

            controller.BtnRemoveWindow.Click += (s, e) => RemoveWindow(controller);

            _windows.Add(controller);
            return controller;
        }

        private void RemoveWindow(ParseWindowController controller)
        {
            if (_windows.Count <= 1)
            {
                MessageBox.Show("At least one parse window has to stay - add another one first if you want to replace this one.", "SqueezeParseMini", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult result = MessageBox.Show(
                "Remove \"" + controller.Page.Text + "\"? This closes its overlay and deletes its settings.",
                "SqueezeParseMini", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            _windows.Remove(controller);
            _tabControl.TabPages.Remove(controller.Page);
            controller.Dispose();
            SaveSettings();
            SyncGeneralWidth();
        }

        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _tabControl.TabPages.Count) return;

            TabPage page = _tabControl.TabPages[e.Index];
            bool selected = e.Index == _tabControl.SelectedIndex;

            Color backColor = selected ? Color.FromArgb(255, 60, 60, 60) : Color.FromArgb(255, 40, 40, 40);
            using (var backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }

            TextRenderer.DrawText(e.Graphics, page.Text, _tabControl.Font, e.Bounds, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            bool inCombat = false;
            try { inCombat = ActGlobals.oFormActMain.InCombat; }
            catch { inCombat = false; }

            foreach (ParseWindowController w in _windows)
            {
                w.Tick(inCombat);
            }
        }

        // ----- Settings persistence (one file, one <ParseWindow> block per window) -----

        /// <summary>Returns true if an existing settings file was found and loaded.</summary>
        private bool LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath)) return false;

                var doc = new System.Xml.XmlDocument();
                doc.Load(_settingsFilePath);

                System.Xml.XmlNode generalNode = doc.SelectSingleNode("Settings/General/PluginFilePath");
                if (generalNode != null)
                    _txtLocalFilePath.Text = generalNode.InnerText;

                System.Xml.XmlNodeList windowNodes = doc.SelectNodes("Settings/ParseWindows/ParseWindow");
                if (windowNodes == null || windowNodes.Count == 0) return false;

                foreach (System.Xml.XmlNode node in windowNodes)
                {
                    ParseWindowController controller = AddWindow("Parse Window " + _nextWindowNumber);
                    controller.LoadFromXml(node);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (ActGlobals.oFormActMain != null)
                    ActGlobals.oFormActMain.WriteExceptionLog(ex, "SqueezeParseMini: failed loading settings");
                return false;
            }
        }

        private void SaveSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var doc = new System.Xml.XmlDocument();
                doc.AppendChild(doc.CreateXmlDeclaration("1.0", null, null));
                System.Xml.XmlElement root = doc.CreateElement("Settings");
                doc.AppendChild(root);

                System.Xml.XmlElement generalNode = doc.CreateElement("General");
                root.AppendChild(generalNode);
                System.Xml.XmlElement filePathNode = doc.CreateElement("PluginFilePath");
                filePathNode.InnerText = _txtLocalFilePath.Text ?? "";
                generalNode.AppendChild(filePathNode);

                System.Xml.XmlElement windowsNode = doc.CreateElement("ParseWindows");
                root.AppendChild(windowsNode);

                foreach (ParseWindowController w in _windows)
                {
                    w.SaveToXml(windowsNode);
                }

                doc.Save(_settingsFilePath);
            }
            catch (Exception ex)
            {
                if (ActGlobals.oFormActMain != null)
                    ActGlobals.oFormActMain.WriteExceptionLog(ex, "SqueezeParseMini: failed saving settings");
            }
        }

        // ----- Update checking -----

        /// <summary>
        /// Fetches VersionCheckUrl (expected to contain just a version
        /// number) on a background thread and updates the status label when
        /// it comes back. Safe to call both passively on load and from the
        /// "Check for updates" button.
        /// </summary>
        private void CheckForUpdates()
        {
            _lblUpdateStatus.Text = "Checking...";
            _lblUpdateStatus.ForeColor = Color.Gray;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string remoteVersion = null;
                string errorMessage = null;

                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    using (var client = new WebClient())
                    {
                        client.Headers.Add("User-Agent", "SqueezeParseMini");
                        remoteVersion = client.DownloadString(VersionCheckUrl).Trim();
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                }

                if (_lblUpdateStatus.IsHandleCreated)
                {
                    _lblUpdateStatus.Invoke(new MethodInvoker(delegate
                    {
                        ApplyUpdateCheckResult(remoteVersion, errorMessage);
                    }));
                }
            });
        }

        private void ApplyUpdateCheckResult(string remoteVersion, string errorMessage)
        {
            if (errorMessage != null || string.IsNullOrEmpty(remoteVersion))
            {
                _lblUpdateStatus.Text = "Couldn't check for updates";
                _lblUpdateStatus.ForeColor = Color.Gray;
                _btnDownloadUpdate.Enabled = false;
                return;
            }

            _latestKnownVersion = remoteVersion;

            if (string.Equals(remoteVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                _lblUpdateStatus.Text = "Up to date (v" + CurrentVersion + ")";
                _lblUpdateStatus.ForeColor = Color.Green;
                _btnDownloadUpdate.Enabled = false;
            }
            else
            {
                _lblUpdateStatus.Text = "Update available: v" + remoteVersion;
                _lblUpdateStatus.ForeColor = Color.OrangeRed;
                _btnDownloadUpdate.Enabled = true;
            }
        }

        /// <summary>
        /// Downloads the latest source and overwrites the local .cs file at
        /// the path entered in "Plugin file path". Since a running plugin
        /// can't reload its own already-compiled code, this always ends with
        /// a prompt to disable/re-enable the plugin in ACT to pick it up.
        /// </summary>
        private void DownloadUpdate()
        {
            string path = _txtLocalFilePath.Text;
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("Enter the path to your SqueezeParseMini.cs file first.", "SqueezeParseMini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnDownloadUpdate.Enabled = false;
            _lblUpdateStatus.Text = "Downloading...";
            _lblUpdateStatus.ForeColor = Color.Gray;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string errorMessage = null;

                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    using (var client = new WebClient())
                    {
                        client.Headers.Add("User-Agent", "SqueezeParseMini");
                        string content = client.DownloadString(DownloadUrl);
                        File.WriteAllText(path, content);
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                }

                if (_lblUpdateStatus.IsHandleCreated)
                {
                    _lblUpdateStatus.Invoke(new MethodInvoker(delegate
                    {
                        if (errorMessage != null)
                        {
                            _lblUpdateStatus.Text = "Update failed";
                            _lblUpdateStatus.ForeColor = Color.Red;
                            _btnDownloadUpdate.Enabled = true;
                            MessageBox.Show("Update failed: " + errorMessage, "SqueezeParseMini", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            _lblUpdateStatus.Text = "Downloaded v" + _latestKnownVersion;
                            _lblUpdateStatus.ForeColor = Color.Green;
                            MessageBox.Show(
                                "SqueezeParseMini has been updated to v" + _latestKnownVersion + " on disk.\n\n" +
                                "Go to ACT's Plugins -> Plugin Listing tab, uncheck SqueezeParseMini, then check it again to load the new version.",
                                "SqueezeParseMini - Update Downloaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }));
                }
            });
        }
    }
}
