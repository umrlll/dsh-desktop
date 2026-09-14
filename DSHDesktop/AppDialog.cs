using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DSHDesktop;

/// <summary>自绘对话框的按钮语义。</summary>
internal enum AppDialogResult
{
    Primary,
    Secondary,
    Cancel,
}

/// <summary>
/// 应用自绘的模态对话框，用来替代系统 <c>MessageBox</c>。
///
/// 为什么：MessageBox 是浅色系统外观（标题栏、按钮、图标全都跟着系统走），出现在这个
/// 深色应用里非常突兀——同一窗口内一会儿是深色浮层、一会儿是浅色弹窗
/// （2026-09-12「所有未优化界面优化为现代化风格」）。这里用与主界面相同的令牌：
/// 深色圆角卡片 + 阴影 + 状态字形（提醒/信息）+ 主/次/收尾三档按钮 + Esc/Enter 语义。
///
/// 按钮外观集中在 <see cref="PrimaryButton"/> / <see cref="SecondaryButton"/> /
/// <see cref="GhostButton"/>，恢复助手等其它自绘界面共用，避免各写一套。
/// </summary>
internal static class AppDialog
{
    private static readonly Color Surface = Color.FromRgb(0x1B, 0x1F, 0x26);
    private static readonly Color BorderColor = Color.FromRgb(0x2A, 0x30, 0x37);
    private static readonly Color TitleColor = Color.FromRgb(0xE9, 0xED, 0xF2);
    private static readonly Color BodyColor = Color.FromRgb(0xC8, 0xCD, 0xD6);
    private static readonly Color Accent = Color.FromRgb(0x56, 0x86, 0xFE);
    private static readonly Color AccentHover = Color.FromRgb(0x6B, 0x96, 0xFF);
    private static readonly Color AccentPressed = Color.FromRgb(0x41, 0x76, 0xE6);
    private static readonly Color Warn = Color.FromRgb(0xF7, 0xAD, 0x31);
    private static readonly Color Info = Color.FromRgb(0x4D, 0x93, 0xF8);
    private static readonly Color Muted = Color.FromRgb(0x8C, 0x94, 0xA2);

    /// <summary>
    /// 弹一个模态对话框并等待选择。<paramref name="primary"/> 是主按钮；
    /// <paramref name="secondary"/> / <paramref name="cancel"/> 为空则不显示
    /// （Esc 关闭一律算 <see cref="AppDialogResult.Cancel"/>，回车算 Primary）。
    /// </summary>
    public static AppDialogResult Show(Window? owner, string title, string message,
        string primary, string? secondary = null, string? cancel = null, bool warning = false)
    {
        var result = AppDialogResult.Cancel;
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 620,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null && owner.IsVisible
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        if (owner != null && owner.IsVisible) window.Owner = owner;

        var card = new Border
        {
            Background = new SolidColorBrush(Surface),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(14),   // 给阴影留出位置
            Effect = new DropShadowEffect
            {
                BlurRadius = 32,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };

        // 头部：状态字形 + 标题。没有系统标题栏，所以整块头部当拖拽把手。
        var head = new DockPanel { LastChildFill = true };
        var glyph = new TextBlock
        {
            Text = warning ? "\uE7BA" : "\uE946",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(warning ? Warn : Info),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);
        head.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(TitleColor),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.MouseLeftButtonDown += (_, __) => { try { window.DragMove(); } catch { } };
        panel.Children.Add(head);

        var body = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(BodyColor),
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        });

        panel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(BorderColor),
            Margin = new Thickness(0, 16, 0, 0),
        });

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var primaryButton = PrimaryButton(primary, () => { result = AppDialogResult.Primary; window.Close(); });
        footer.Children.Add(primaryButton);
        if (secondary != null)
        {
            var secondaryButton = SecondaryButton(secondary, () => { result = AppDialogResult.Secondary; window.Close(); });
            secondaryButton.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(secondaryButton);
        }
        if (cancel != null)
        {
            var cancelButton = GhostButton(cancel, () => { result = AppDialogResult.Cancel; window.Close(); });
            cancelButton.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(cancelButton);
        }
        panel.Children.Add(footer);

        card.Child = panel;
        window.Content = card;

        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = AppDialogResult.Cancel; window.Close(); }
            else if (e.Key == Key.Enter) { result = AppDialogResult.Primary; window.Close(); }
        };
        window.Loaded += (_, __) => window.Activate();

        try
        {
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // 对话框本身失败不该让调用方崩掉：退回系统 MessageBox（保命路径，允许这次变丑）
            DesktopLog.Warn("自绘对话框失败，退回系统对话框: " + DesktopLog.Describe(ex));
            var buttons = cancel != null
                ? MessageBoxButton.YesNoCancel
                : secondary != null ? MessageBoxButton.YesNo : MessageBoxButton.OK;
            var fallback = MessageBox.Show(owner, message, title, buttons,
                warning ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return fallback switch
            {
                MessageBoxResult.Yes => AppDialogResult.Primary,
                MessageBoxResult.No => AppDialogResult.Secondary,
                _ => AppDialogResult.Cancel,
            };
        }

        return result;
    }

    /// <summary>主按钮：品牌蓝实心，用于"推荐 / 会改变状态"的动作。</summary>
    internal static Button PrimaryButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Accent),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            MinHeight = 30,
            Padding = new Thickness(14, 5, 14, 5),
            Cursor = Cursors.Hand,
        };
        button.Template = RoundTemplate(7);
        button.MouseEnter += (_, __) => button.Background = new SolidColorBrush(AccentHover);
        button.MouseLeave += (_, __) => button.Background = new SolidColorBrush(Accent);
        button.PreviewMouseLeftButtonDown += (_, __) => button.Background = new SolidColorBrush(AccentPressed);
        button.Click += (_, __) => onClick();
        return button;
    }

    /// <summary>次级按钮：描边深色。</summary>
    internal static Button SecondaryButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Foreground = new SolidColorBrush(TitleColor),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x43)),
            BorderThickness = new Thickness(1),
            MinHeight = 30,
            Padding = new Thickness(12, 5, 12, 5),
            Cursor = Cursors.Hand,
        };
        button.Template = RoundTemplate(7);
        button.Click += (_, __) => onClick();
        return button;
    }

    /// <summary>收尾按钮：无边框幽灵按钮。</summary>
    internal static Button GhostButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Foreground = new SolidColorBrush(Muted),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinHeight = 30,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = Cursors.Hand,
        };
        button.Template = RoundTemplate(7);
        button.MouseEnter += (_, __) => button.Foreground = new SolidColorBrush(TitleColor);
        button.MouseLeave += (_, __) => button.Foreground = new SolidColorBrush(Muted);
        button.Click += (_, __) => onClick();
        return button;
    }

    /// <summary>圆角按钮模板（默认 WPF 按钮是方角 + 系统描边，与浮层不一致）。</summary>
    private static ControlTemplate RoundTemplate(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
