using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace BetterDesktop.Shell.Core.Animation;

/// <summary>
/// 动画服务接口，提供各种动画效果。
/// </summary>
public interface IAnimationService
{
    /// <summary>
    /// 创建图标弹跳动画。
    /// </summary>
    /// <param name="element">目标元素。</param>
    /// <param name="bounceHeight">弹跳高度。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateBounceAnimation(UIElement element, double bounceHeight = 20, TimeSpan? duration = null);

    /// <summary>
    /// 创建窗口最小化动画（神奇效果）。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="targetPosition">目标位置（通常是 Dock 栏位置）。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateMinimizeAnimation(Window window, Point targetPosition, TimeSpan? duration = null);

    /// <summary>
    /// 创建窗口启动动画。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateWindowOpenAnimation(Window window, TimeSpan? duration = null);

    /// <summary>
    /// 创建窗口关闭动画。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateWindowCloseAnimation(Window window, TimeSpan? duration = null);

    /// <summary>
    /// 创建淡入动画。
    /// </summary>
    /// <param name="element">目标元素。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateFadeInAnimation(UIElement element, TimeSpan? duration = null);

    /// <summary>
    /// 创建淡出动画。
    /// </summary>
    /// <param name="element">目标元素。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateFadeOutAnimation(UIElement element, TimeSpan? duration = null);

    /// <summary>
    /// 创建滑入动画。
    /// </summary>
    /// <param name="element">目标元素。</param>
    /// <param name="fromDirection">滑入方向。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateSlideInAnimation(UIElement element, SlideDirection fromDirection, TimeSpan? duration = null);

    /// <summary>
    /// 创建滑出动画。
    /// </summary>
    /// <param name="element">目标元素。</param>
    /// <param name="toDirection">滑出方向。</param>
    /// <param name="duration">动画持续时间。</param>
    /// <returns>动画故事板。</returns>
    Storyboard CreateSlideOutAnimation(UIElement element, SlideDirection toDirection, TimeSpan? duration = null);
}

/// <summary>
/// 滑动方向。
/// </summary>
public enum SlideDirection
{
    /// <summary>
    /// 从左向右。
    /// </summary>
    Left,

    /// <summary>
    /// 从右向左。
    /// </summary>
    Right,

    /// <summary>
    /// 从上向下。
    /// </summary>
    Up,

    /// <summary>
    /// 从下向上。
    /// </summary>
    Down
}
