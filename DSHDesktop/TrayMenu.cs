using System;
using System.Drawing;
using System.IO;
using WinForms = System.Windows.Forms;
// 只给 GlyphImage 用到的 WPF 类型起别名，不整片引入 WPF 命名空间：否则 Color / Point /
// FontFamily / Pen 这些名字会和 System.Drawing 撞车（编译期 CS0104 已证实）。
using Point = System.Windows.Point;
using FlowDirection = System.Windows.FlowDirection;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using FontFamily = System.Windows.Media.FontFamily;
using Typeface = System.Windows.Media.Typeface;
using FontStyles = System.Windows.FontStyles;
using FontWeights = System.Windows.FontWeights;
using FontStretches = System.Windows.FontStretches;
using FormattedText = System.Windows.Media.FormattedText;
using DrawingVisual = System.Windows.Media.DrawingVisual;
using PixelFormats = System.Windows.Media.PixelFormats;
using RenderTargetBitmap = System.Windows.Media.Imaging.RenderTargetBitmap;
using PngBitmapEncoder = System.Windows.Media.Imaging.PngBitmapEncoder;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace DSHDesktop;

/// <summary>
/// 托盘右键菜单：深色主题 + 图标字体字形，与主界面同一套配色。
///
/// 为什么自绘：WinForms 的 ContextMenuStrip 默认是浅色系统外观，在这个深色应用里
/// 出现一次浅色菜单非常割裂（2026-09-12「所有未优化界面优化为现代化风格」）。
/// 这里只改渲染与配色，不改交互语义。
///
/// 颜色必须写字面量：WinForms 读不到 WPF 的 <c>--dsw-*</c> / <c>{StaticResource}</c>，
/// 所以下面的值与 MainWindow.xaml 的令牌保持镜像（改一处要同步另一处）。
/// </summary>
internal static class TrayMenu
{
    private static readonly Color Surface = Color.FromArgb(0x1B, 0x1F, 0x26);
    private static readonly Color BorderColor = Color.FromArgb(0x2A, 0x30, 0x37);
    private static readonly Color TextColor = Color.FromArgb(0xE9, 0xED, 0xF2);
    private static readonly Color MutedColor = Color.FromArgb(0x8C, 0x94, 0xA2);
    private static readonly Color HoverColor = Color.FromArgb(0x23, 0x28, 0x2E);
    private static readonly Color GlyphColor = Color.FromArgb(0xC8, 0xCD, 0xD6);

    /// <summary>建一个空菜单（渲染器/字体/边距都配好），调用方往里加项目。</summary>
    public static WinForms.ContextMenuStrip Create()
    {
        return new WinForms.ContextMenuStrip
        {
            Renderer = new TrayRenderer(),
            Font = new Font("Segoe UI", 9f),
            BackColor = Surface,
            ForeColor = TextColor,
            ShowImageMargin = true,
            DropShadowEnabled = true,
            Padding = new WinForms.Padding(0, 6, 0, 6),
        };
    }

    /// <summary>加一条带图标字形的项目。</summary>
    public static WinForms.ToolStripMenuItem Item(string text, uint glyph, Action onClick, string? tooltip = null)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            ForeColor = TextColor,
            Image = GlyphImage(glyph, GlyphColor),
            ImageScaling = WinForms.ToolStripItemImageScaling.None,
            Padding = new WinForms.Padding(2, 3, 4, 3),
        };
        if (tooltip != null) item.ToolTipText = tooltip;
        item.Click += (_, __) => onClick();
        return item;
    }

    /// <summary>改一条项目的外观（用于随状态变化的「启动 / 停止服务」）。</summary>
    public static void Restyle(WinForms.ToolStripMenuItem item, string text, uint glyph, bool enabled = true, Color? color = null)
    {
        item.Text = text;
        item.Enabled = enabled;
        item.Image = GlyphImage(glyph, color ?? GlyphColor);
    }

    public static WinForms.ToolStripSeparator Separator()
    {
        return new WinForms.ToolStripSeparator { ForeColor = BorderColor };
    }

    /// <summary>
    /// 把一个图标字体码位渲染成 WinForms 用的位图。用 WPF 的 FormattedText 画（与界面同源），
    /// 再编码成 PNG 让 WinForms 解码——两条 UI 栈共用一个字形来源，不必引图标包。
    /// </summary>
    private static Image? GlyphImage(uint codepoint, Color color, int size = 16)
    {
        try
        {
            var face = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            var text = new FormattedText(char.ConvertFromUtf32((int)codepoint),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                face, 13, brush, 1.0);

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            using var decoded = new Bitmap(stream);
            return new Bitmap(decoded);   // 复制一份，避免依赖已关闭的流
        }
        catch
        {
            return null;   // 画不出来就不带图标，绝不让菜单建不出来
        }
    }

    /// <summary>深色配色表。</summary>
    private sealed class TrayColorTable : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => BorderColor;
        public override Color MenuItemBorder => HoverColor;
        public override Color MenuItemSelected => HoverColor;
        public override Color MenuItemSelectedGradientBegin => HoverColor;
        public override Color MenuItemSelectedGradientEnd => HoverColor;
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => BorderColor;
    }

    /// <summary>深色渲染器：文字/快捷键/箭头都按令牌上色。</summary>
    private sealed class TrayRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public TrayRenderer() : base(new TrayColorTable()) { }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item?.Enabled == false ? MutedColor : TextColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = MutedColor;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            // 默认会画一条亮边；改成与浮层一致的描边色
            using var pen = new Pen(BorderColor);
            var bounds = e.ToolStrip.ClientRectangle;
            e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }
    }
}
