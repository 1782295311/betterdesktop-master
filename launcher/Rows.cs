// BetterDesktop 启动器 —— 界面行模型（启动步骤行 / 开关行）。
//
// 【为什么单独一个文件】启动器 UI 刻意不做 MVVM 框架那一套：只有两行模型 + 一屏控件，
// 用 ObservableCollection + INotifyPropertyChanged 就够了，少一层框架少一处启动期风险。

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using BetterDesktop.Launcher.Services;

namespace BetterDesktop.Launcher;

/// <summary>启动步骤行（只增不改，构造时定型）。</summary>
internal sealed class StepRow
{
    private readonly Brush _brush;

    private StepRow(string glyph, string title, string detail, Brush brush)
    {
        Glyph = glyph;
        Title = title;
        Detail = detail;
        _brush = brush;
    }

    public string Glyph { get; }

    public string Title { get; }

    public string Detail { get; }

    public Brush Brush => _brush;

    /// <summary>由启动报告的一步构造（含状态 → 字形/颜色映射）。</summary>
    public static StepRow From(BootStep step)
    {
        var (glyph, resourceKey, fallback) = step.State switch
        {
            StepState.Ok => ("✓", "LauncherOk", Color.FromRgb(0x3F, 0xBF, 0x7F)),
            StepState.Warn => ("!", "LauncherWarn", Color.FromRgb(0xE0, 0xA3, 0x3E)),
            _ => ("×", "LauncherFail", Color.FromRgb(0xE0, 0x57, 0x4C)),
        };
        return new StepRow(glyph, step.Title, step.Detail, Resolve(resourceKey, fallback));
    }

    /// <summary>取主题画刷；缺失时退回内置色（绝不因资源键缺失而抛）。</summary>
    internal static Brush Resolve(string resourceKey, Color fallback)
        => Application.Current?.TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>开关行（用户可改；改完由 ToggleApplier 落地并把结果写回 Note）。</summary>
internal sealed class ToggleRow : INotifyPropertyChanged
{
    private bool _isOn;
    private string _note = string.Empty;
    private Visibility _noteVisibility = Visibility.Collapsed;
    private Brush _noteBrush = StepRow.Resolve("LauncherTextDim", Color.FromRgb(0x9A, 0xA3, 0xAE));

    public ToggleRow(FeatureToggle toggle, bool isOn)
    {
        Toggle = toggle;
        _isOn = isOn;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FeatureToggle Toggle { get; }

    public string Label => Toggle.Label;

    public string Description => Toggle.Description;

    /// <summary>用户的选择（TwoWay 绑定）。</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            _isOn = value;
            Raise(nameof(IsOn));
        }
    }

    /// <summary>落地结果说明（空 = 尚无结论）。</summary>
    public string Note
    {
        get => _note;
        private set
        {
            _note = value;
            Raise(nameof(Note));
        }
    }

    public Visibility NoteVisibility
    {
        get => _noteVisibility;
        private set
        {
            _noteVisibility = value;
            Raise(nameof(NoteVisibility));
        }
    }

    public Brush NoteBrush
    {
        get => _noteBrush;
        private set
        {
            _noteBrush = value;
            Raise(nameof(NoteBrush));
        }
    }

    /// <summary>把落地结果标注在本行（成功绿、失败红——失败绝不静默）。</summary>
    public void SetOutcome(ToggleOutcome outcome)
    {
        Note = outcome.Note;
        NoteVisibility = string.IsNullOrEmpty(outcome.Note) ? Visibility.Collapsed : Visibility.Visible;
        NoteBrush = StepRow.Resolve(
            outcome.Applied ? "LauncherOk" : "LauncherFail",
            outcome.Applied ? Color.FromRgb(0x3F, 0xBF, 0x7F) : Color.FromRgb(0xE0, 0x57, 0x4C));
    }

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
