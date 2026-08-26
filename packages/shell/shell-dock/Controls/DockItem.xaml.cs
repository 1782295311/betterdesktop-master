using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Controls;

/// <summary>
/// Dock 项目用户控件。
/// </summary>
public partial class DockItem : UserControl
{
    public static readonly DependencyProperty ItemDataProperty =
        DependencyProperty.Register(nameof(ItemData), typeof(DockItemData), typeof(DockItem),
            new PropertyMetadata(null, OnItemDataChanged));

    public static readonly DependencyProperty IconSourceProperty =
        DependencyProperty.Register(nameof(IconSource), typeof(System.Windows.Media.ImageSource), typeof(DockItem),
            new PropertyMetadata(null, OnIconSourceChanged));

    public DockItemData? ItemData
    {
        get => (DockItemData?)GetValue(ItemDataProperty);
        set => SetValue(ItemDataProperty, value);
    }

    public System.Windows.Media.ImageSource? IconSource
    {
        get => (System.Windows.Media.ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public DockItem()
    {
        InitializeComponent();
        DataContext = this;

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private static void OnItemDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DockItem control && e.NewValue is DockItemData data)
        {
            // 数据变化时先刷新占位文本与标签。
            control.IconText.Text = string.IsNullOrWhiteSpace(data.Name) ? "?" : data.Name[..1];
            control.LabelText.Text = data.Name;

            // 新数据进入时先回落到占位状态，等 IconSource 异步注入后再切换。
            control.IconImage.Source = null;
            control.IconImage.Visibility = Visibility.Collapsed;
            control.IconText.Visibility = Visibility.Visible;
        }
    }

    private static void OnIconSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DockItem control)
        {
            if (e.NewValue is System.Windows.Media.ImageSource image)
            {
                control.IconImage.Source = image;
                control.IconImage.Visibility = Visibility.Visible;
                control.IconText.Visibility = Visibility.Collapsed;
            }
            else
            {
                control.IconImage.Source = null;
                control.IconImage.Visibility = Visibility.Collapsed;
                control.IconText.Visibility = Visibility.Visible;
            }
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        // 显示背景
        var fadeIn = new DoubleAnimation(0, 0.3, TimeSpan.FromMilliseconds(200));
        BackgroundBorder.BeginAnimation(OpacityProperty, fadeIn);

        // 显示标签
        var labelFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        LabelText.BeginAnimation(OpacityProperty, labelFadeIn);

        // 放大动画
        var scaleTransform = new ScaleTransform(1.0, 1.0);
        RenderTransform = scaleTransform;
        RenderTransformOrigin = new Point(0.5, 0.5);

        var scaleX = new DoubleAnimation(1.0, 1.2, TimeSpan.FromMilliseconds(200));
        var scaleY = new DoubleAnimation(1.0, 1.2, TimeSpan.FromMilliseconds(200));
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        // 向上移动
        var translateTransform = new TranslateTransform(0, 0);
        RenderTransform = new TransformGroup
        {
            Children = { scaleTransform, translateTransform }
        };

        var moveUp = new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(200));
        translateTransform.BeginAnimation(TranslateTransform.YProperty, moveUp);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 隐藏背景
        var fadeOut = new DoubleAnimation(0.3, 0, TimeSpan.FromMilliseconds(200));
        BackgroundBorder.BeginAnimation(OpacityProperty, fadeOut);

        // 隐藏标签
        var labelFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        LabelText.BeginAnimation(OpacityProperty, labelFadeOut);

        // 恢复大小和位置
        var scaleTransform = RenderTransform as TransformGroup;
        if (scaleTransform != null)
        {
            foreach (var child in scaleTransform.Children)
            {
                if (child is ScaleTransform scale)
                {
                    var scaleX = new DoubleAnimation(1.2, 1.0, TimeSpan.FromMilliseconds(200));
                    var scaleY = new DoubleAnimation(1.2, 1.0, TimeSpan.FromMilliseconds(200));
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
                }
                else if (child is TranslateTransform translate)
                {
                    var moveDown = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(200));
                    translate.BeginAnimation(TranslateTransform.YProperty, moveDown);
                }
            }
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点击动画：缩小再恢复
        var scaleTransform = RenderTransform as TransformGroup;
        if (scaleTransform != null)
        {
            foreach (var child in scaleTransform.Children)
            {
                if (child is ScaleTransform scale)
                {
                    var scaleX = new DoubleAnimation(1.2, 0.9, TimeSpan.FromMilliseconds(100));
                    var scaleY = new DoubleAnimation(1.2, 0.9, TimeSpan.FromMilliseconds(100));
                    scaleX.AutoReverse = true;
                    scaleY.AutoReverse = true;
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
                }
            }
        }

        // 显示活动指示器
        var indicatorFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        ActiveIndicator.BeginAnimation(OpacityProperty, indicatorFadeIn);
    }
}
