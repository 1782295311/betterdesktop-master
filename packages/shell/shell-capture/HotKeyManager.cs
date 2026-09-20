using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using BetterDesktop.Shell.Capture.Core;

namespace BetterDesktop.Shell.Capture;

/// <summary>
/// 全局热键（默认 Win+Shift+B——2026-09-15 真机实测 Win+Shift+S / Win+Shift+A / Ctrl+Shift+A
/// 均被系统或第三方占用，RegisterHotKey 返回 0x581；B 实测空闲）。
/// 可被 %LOCALAPPDATA%\BetterDesktop\capture\settings.json 覆盖：
///   { "hotkey": { "enabled": true, "modifiers": "Win+Shift", "key": "B" } }  停用: enabled=false
/// 窗口走隐藏 HwndSource（无需可见窗口收 WM_HOTKEY）；MOD_NOREPEAT 抑制按住连发。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private const int HotKeyId = 0x4D41; // 'MA'
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private readonly Action _onHotKey;
    private HwndSource? _source;
    private uint _modifiers;
    private uint _vk;
    private bool _registered;

    public HotKeyManager(Action onHotKey)
    {
        _onHotKey = onHotKey;
        (uint mods, uint vk, bool enabled) = LoadSettings();
        _modifiers = mods;
        _vk = vk;
        _enabled = enabled;
    }

    private readonly bool _enabled;

    public bool Enabled => _enabled && _registered;

    public void Register()
    {
        if (!_enabled)
        {
            CaptureLog.Info("热键已按 settings.json 停用");
            return;
        }

        var parameters = new HwndSourceParameters("BetterDesktopCaptureHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        if (!RegisterHotKey(_source.Handle, HotKeyId, _modifiers | MOD_NOREPEAT, _vk))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterHotKey 失败（0x{err:X8}）——可能与系统截图工具冲突");
        }
        _registered = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            _onHotKey();
        }
        return IntPtr.Zero;
    }

    private static (uint Modifiers, uint Vk, bool Enabled) LoadSettings()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(root, "BetterDesktop", "capture", "settings.json");
            if (!File.Exists(path))
            {
                return (MOD_WIN | MOD_SHIFT, 0x42 /* 'B' */, true);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var hotkey = doc.RootElement.TryGetProperty("hotkey", out var h) ? h : default;
            if (hotkey.ValueKind != JsonValueKind.Object)
            {
                return (MOD_WIN | MOD_SHIFT, 0x42 /* 'B' */, true);
            }

            bool enabled = !hotkey.TryGetProperty("enabled", out var en) || en.GetBoolean();
            uint mods = MOD_WIN | MOD_SHIFT;
            if (hotkey.TryGetProperty("modifiers", out var modsEl) && modsEl.ValueKind == JsonValueKind.String)
            {
                mods = ParseModifiers(modsEl.GetString() ?? string.Empty);
            }
            uint vk = 0x42; // 'B'
            if (hotkey.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            {
                var k = keyEl.GetString() ?? string.Empty;
                if (k.Length == 1)
                {
                    vk = char.ToUpperInvariant(k[0]);
                }
            }
            return (mods, vk, enabled);
        }
        catch (Exception ex)
        {
            CaptureLog.Warn($"热键配置读取失败，用默认 Win+Shift+S：{ex.Message}");
            return (MOD_WIN | MOD_SHIFT, 0x53, true);
        }
    }

    private static uint ParseModifiers(string s)
    {
        uint mods = 0;
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mods |= part.ToUpperInvariant() switch
            {
                "WIN" => MOD_WIN,
                "SHIFT" => MOD_SHIFT,
                "CTRL" or "CONTROL" => MOD_CONTROL,
                "ALT" => MOD_ALT,
                _ => 0,
            };
        }
        return mods;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotKeyId);
            _registered = false;
        }
        _source?.Dispose();
    }
}
