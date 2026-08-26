using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BetterDesktop.Shell.Core.Animation;

/// <summary>
/// 动画服务实现。
/// </summary>
public sealed class AnimationService : IAnimationService
{
    /// <inheritdoc />
    public Storyboard CreateBounceAnimation(UIElement element, double bounceHeight = 20, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(500);

        // 创建弹跳动画
        var bounceAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = actualDuration,
            RepeatBehavior = new RepeatBehavior(3) // 弹跳 3 次
        };

        // 添加关键帧
        bounceAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        bounceAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-bounceHeight, KeyTime.FromPercent(0.3), new BounceEase()));
        bounceAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.6)));
        bounceAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-bounceHeight * 0.7, KeyTime.FromPercent(0.8), new BounceEase()));
        bounceAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        Storyboard.SetTarget(bounceAnimation, element);
        Storyboard.SetTargetProperty(bounceAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        storyboard.Children.Add(bounceAnimation);
        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateMinimizeAnimation(Window window, Point targetPosition, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(300);

        // 创建缩放动画
        var scaleXAnimation = new DoubleAnimation(1, 0.1, actualDuration);
        var scaleYAnimation = new DoubleAnimation(1, 0.1, actualDuration);

        // 创建位移动画
        var translateXAnimation = new DoubleAnimation(0, targetPosition.X - window.Left, actualDuration);
        var translateYAnimation = new DoubleAnimation(0, targetPosition.Y - window.Top, actualDuration);

        // 创建透明度动画
        var opacityAnimation = new DoubleAnimation(1, 0, actualDuration);

        Storyboard.SetTarget(scaleXAnimation, window);
        Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

        Storyboard.SetTarget(scaleYAnimation, window);
        Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        Storyboard.SetTarget(translateXAnimation, window);
        Storyboard.SetTargetProperty(translateXAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

        Storyboard.SetTarget(translateYAnimation, window);
        Storyboard.SetTargetProperty(translateYAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        Storyboard.SetTarget(opacityAnimation, window);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("(UIElement.Opacity)"));

        storyboard.Children.Add(scaleXAnimation);
        storyboard.Children.Add(scaleYAnimation);
        storyboard.Children.Add(translateXAnimation);
        storyboard.Children.Add(translateYAnimation);
        storyboard.Children.Add(opacityAnimation);

        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateWindowOpenAnimation(Window window, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(200);

        // 创建缩放动画
        var scaleXAnimation = new DoubleAnimation(0.8, 1, actualDuration);
        var scaleYAnimation = new DoubleAnimation(0.8, 1, actualDuration);

        // 创建透明度动画
        var opacityAnimation = new DoubleAnimation(0, 1, actualDuration);

        Storyboard.SetTarget(scaleXAnimation, window);
        Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

        Storyboard.SetTarget(scaleYAnimation, window);
        Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        Storyboard.SetTarget(opacityAnimation, window);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("(UIElement.Opacity)"));

        storyboard.Children.Add(scaleXAnimation);
        storyboard.Children.Add(scaleYAnimation);
        storyboard.Children.Add(opacityAnimation);

        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateWindowCloseAnimation(Window window, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(200);

        // 创建缩放动画
        var scaleXAnimation = new DoubleAnimation(1, 0.8, actualDuration);
        var scaleYAnimation = new DoubleAnimation(1, 0.8, actualDuration);

        // 创建透明度动画
        var opacityAnimation = new DoubleAnimation(1, 0, actualDuration);

        Storyboard.SetTarget(scaleXAnimation, window);
        Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

        Storyboard.SetTarget(scaleYAnimation, window);
        Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        Storyboard.SetTarget(opacityAnimation, window);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("(UIElement.Opacity)"));

        storyboard.Children.Add(scaleXAnimation);
        storyboard.Children.Add(scaleYAnimation);
        storyboard.Children.Add(opacityAnimation);

        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateFadeInAnimation(UIElement element, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(200);

        var opacityAnimation = new DoubleAnimation(0, 1, actualDuration);

        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("(UIElement.Opacity)"));

        storyboard.Children.Add(opacityAnimation);
        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateFadeOutAnimation(UIElement element, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(200);

        var opacityAnimation = new DoubleAnimation(1, 0, actualDuration);

        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("(UIElement.Opacity)"));

        storyboard.Children.Add(opacityAnimation);
        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateSlideInAnimation(UIElement element, SlideDirection fromDirection, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(300);

        var translateXAnimation = new DoubleAnimation();
        var translateYAnimation = new DoubleAnimation();

        switch (fromDirection)
        {
            case SlideDirection.Left:
                translateXAnimation = new DoubleAnimation(-100, 0, actualDuration);
                break;
            case SlideDirection.Right:
                translateXAnimation = new DoubleAnimation(100, 0, actualDuration);
                break;
            case SlideDirection.Up:
                translateYAnimation = new DoubleAnimation(-100, 0, actualDuration);
                break;
            case SlideDirection.Down:
                translateYAnimation = new DoubleAnimation(100, 0, actualDuration);
                break;
        }

        Storyboard.SetTarget(translateXAnimation, element);
        Storyboard.SetTargetProperty(translateXAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

        Storyboard.SetTarget(translateYAnimation, element);
        Storyboard.SetTargetProperty(translateYAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        storyboard.Children.Add(translateXAnimation);
        storyboard.Children.Add(translateYAnimation);

        return storyboard;
    }

    /// <inheritdoc />
    public Storyboard CreateSlideOutAnimation(UIElement element, SlideDirection toDirection, TimeSpan? duration = null)
    {
        var storyboard = new Storyboard();
        var actualDuration = duration ?? TimeSpan.FromMilliseconds(300);

        var translateXAnimation = new DoubleAnimation();
        var translateYAnimation = new DoubleAnimation();

        switch (toDirection)
        {
            case SlideDirection.Left:
                translateXAnimation = new DoubleAnimation(0, -100, actualDuration);
                break;
            case SlideDirection.Right:
                translateXAnimation = new DoubleAnimation(0, 100, actualDuration);
                break;
            case SlideDirection.Up:
                translateYAnimation = new DoubleAnimation(0, -100, actualDuration);
                break;
            case SlideDirection.Down:
                translateYAnimation = new DoubleAnimation(0, 100, actualDuration);
                break;
        }

        Storyboard.SetTarget(translateXAnimation, element);
        Storyboard.SetTargetProperty(translateXAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

        Storyboard.SetTarget(translateYAnimation, element);
        Storyboard.SetTargetProperty(translateYAnimation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        storyboard.Children.Add(translateXAnimation);
        storyboard.Children.Add(translateYAnimation);

        return storyboard;
    }
}
