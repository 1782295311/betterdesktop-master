using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Hotkeys.Contracts;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>扫描到的全局热键占用类别。</summary>
internal enum HotkeyScanKind
{
    /// <summary>Windows 自带系统热键，或**系统保留的修饰键组合**（由 F24 校准判定，见 <see cref="HotkeyScanner"/>）。
    /// 二者都不属于"第三方软件占用"，UI 不得把它们混进第三方清单（会造成大量假阳性）。</summary>
    System,

    /// <summary>BetterDesktop 自家（含外部进程：面板 / 截图 / 引擎），在注册表里有声明条目。</summary>
    Ours,

    /// <summary>其他程序占用——**归属未知**（系统不公开 RegisterHotKey 的占用者，这是硬边界）。</summary>
    ThirdParty,
}

/// <summary>一条扫描结果（键位 + 归类）。</summary>
internal sealed record HotkeyScanEntry(string Spec, HotkeyScanKind Kind, string? Owner, string? Description);

/// <summary>扫描汇总（供 UI 显示计数与耗时）。</summary>
internal sealed record HotkeyScanResult(
    IReadOnlyList<HotkeyScanEntry> Entries,
    int ProbedCount,
    long ElapsedMs)
{
    public IEnumerable<HotkeyScanEntry> ThirdParty =>
        Entries.Where(e => e.Kind == HotkeyScanKind.ThirdParty);
}

/// <summary>
/// 全局热键占用扫描器（**唯一可行的动态枚举手段**）。
/// <para>
/// 【原理】Windows 不公开"谁注册了哪个全局热键"的 API（win32k 的内部热键表不可读），
/// 但 <c>RegisterHotKey</c> 对已被占用的组合返回 <c>ERROR_HOTKEY_ALREADY_REGISTERED(1409)</c>。
/// 因此"逐个候选组合试注册 → 成功即立刻注销 / 失败即被占用"是本机唯一能拿到真实占用清单的办法。
/// </para>
/// <para>
/// 【诚实边界】只能发现经 <c>RegisterHotKey</c> 注册的全局热键。大量软件（输入法、截图工具、
/// QQ/微信的默认键）用 <c>WH_KEYBOARD_LL</c> 低级钩子实现，**探测不到**——这类必须在对应软件
/// 的设置里查看，UI 上必须写明，不得让用户以为"没列出来就是没被占用"。
/// </para>
/// <para>【安全性】探测失败即说明键位已被占，我们不做任何抢占；探测成功会立刻注销，不长期持有。
/// 全过程在后台线程完成（注册/注销必须同线程）。</para>
/// </summary>
internal static class HotkeyScanner
{
    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED（winerror.h 1409 / 0x581）——被占用。</summary>
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>候选主键（WPF Key 枚举名，与 HotkeySpec 书写形式一致）。</summary>
    private static readonly string[] KeyNames =
    {
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Space", "Tab", "Back", "Enter", "Escape",
        "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown",
        "Insert", "Delete",
        "OemPeriod", "OemComma", "OemMinus", "OemPlus", "Oem3",
    };

    /// <summary>扫描全部候选组合（约 15 组修饰键 × 70 主键 ≈ 1000 次探测，实测 &lt; 1.5s）。</summary>
    /// <param name="registry">用于把占用键位归类到自家声明（避免把引擎/截图键误报成"第三方"）。</param>
    public static HotkeyScanResult Scan(IHotkeyRegistryService? registry, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 自家/已声明条目按 canonical 建索引（跨程序集只读 GetAll，零副作用）
        var oursByCanonical = new Dictionary<string, HotkeyBinding>(StringComparer.OrdinalIgnoreCase);
        if (registry is not null)
        {
            foreach (var view in registry.GetAll())
            {
                if (HotkeySpec.TryParse(view.Binding.Chord.Spec, out _, out _, out var canonical)
                    && canonical.Length > 0
                    && !oursByCanonical.ContainsKey(canonical))
                {
                    oursByCanonical[canonical] = view.Binding;
                }
            }
        }

        var entries = new List<HotkeyScanEntry>();
        int id = 0x7100;
        int probed = 0;

        // ── 校准（必须有，否则大量假阳性）──
        // Windows 对某些**修饰键组合本身**保留（不是某个具体键被占），此时试注册任何主键都会返回
        // 同一个错误码 1409，与"被其他软件占用"无法区分。真机实测（2026-09-16）：
        //   Win+F24 / Ctrl+Win+F24 / Ctrl+Shift+Win+F24 → 全部 1409（系统保留）
        //   Alt+Win+F24 / Shift+Win+F24 / Ctrl+Alt+Shift+Win+F24 / Ctrl+Alt+Shift+F24 → 可注册
        // 故先用"几乎不可能被任何程序注册"的 F24 探针，逐个判定 15 种修饰键组合是否被系统保留；
        // 保留的组合下所有"占用"一律归"系统保留"，**绝不列为第三方软件**。
        var reservedMasks = new bool[16];
        for (int mask = 1; mask <= 0xF; mask++)
        {
            if (!TryVirtualKey("F24", out var calibVk)
                || !HotkeySpec.TryParse(
                    HotkeySpec.Build((mask & 0x1) != 0, (mask & 0x2) != 0, (mask & 0x4) != 0, (mask & 0x8) != 0, "F24"),
                    out var calibMods, out _, out _))
            {
                continue;
            }

            id++;
            if (NativeMethods.RegisterHotKey(IntPtr.Zero, id, calibMods, calibVk))
            {
                _ = NativeMethods.UnregisterHotKey(IntPtr.Zero, id);
            }
            else
            {
                reservedMasks[mask] = true;
            }
        }

        for (int mask = 1; mask <= 0xF; mask++)
        {
            bool ctrl = (mask & 0x1) != 0;
            bool shift = (mask & 0x2) != 0;
            bool alt = (mask & 0x4) != 0;
            bool win = (mask & 0x8) != 0;

            foreach (var keyName in KeyNames)
            {
                if (ct.IsCancellationRequested)
                {
                    return new HotkeyScanResult(entries, probed, sw.ElapsedMilliseconds);
                }

                if (!TryVirtualKey(keyName, out var vk)
                    || !HotkeySpec.TryParse(
                        HotkeySpec.Build(ctrl, shift, alt, win, keyName),
                        out var modifiers, out _, out var canonical))
                {
                    continue;
                }

                probed++;
                id++;
                int thisId = id;
                bool ok = NativeMethods.RegisterHotKey(IntPtr.Zero, thisId, modifiers, vk);
                if (ok)
                {
                    // 空闲：立刻注销，不长期持有（探测成功 + 立刻释放 = 零副作用）
                    _ = NativeMethods.UnregisterHotKey(IntPtr.Zero, thisId);
                    continue;
                }

                if (Marshal.GetLastWin32Error() != ErrorHotkeyAlreadyRegistered)
                {
                    continue; // 其它错误（非法组合等）不入清单
                }

                if (reservedMasks[mask])
                {
                    entries.Add(new HotkeyScanEntry(
                        canonical, HotkeyScanKind.System, HotkeyDeclarations.SystemOwner, "Windows 系统保留组合"));
                    continue;
                }

                var system = HotkeyDeclarations.FindSystemBySpec(canonical);
                if (system is not null)
                {
                    entries.Add(new HotkeyScanEntry(canonical, HotkeyScanKind.System, HotkeyDeclarations.SystemOwner, system.Description));
                    continue;
                }

                if (oursByCanonical.TryGetValue(canonical, out var binding))
                {
                    entries.Add(new HotkeyScanEntry(canonical, HotkeyScanKind.Ours, binding.Owner, binding.Description));
                    continue;
                }

                entries.Add(new HotkeyScanEntry(canonical, HotkeyScanKind.ThirdParty, null, null));
            }
        }

        return new HotkeyScanResult(entries, probed, sw.ElapsedMilliseconds);
    }

    /// <summary>单个组合的占用探测（"检测快捷键"卡用）：true = 已被占用。</summary>
    public static bool IsGloballyTaken(string spec)
    {
        if (!HotkeySpec.TryParse(spec, out var modifiers, out var mainKey, out _)
            || !TryVirtualKey(mainKey, out var vk))
        {
            return false;
        }

        int id = 0x7F00 + (Environment.TickCount & 0xFF);
        if (NativeMethods.RegisterHotKey(IntPtr.Zero, id, modifiers, vk))
        {
            _ = NativeMethods.UnregisterHotKey(IntPtr.Zero, id);
            return false;
        }
        return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered;
    }

    private static bool TryVirtualKey(string keyName, out uint vk)
    {
        vk = 0;
        if (!Enum.TryParse<Key>(keyName, out var key) || key == Key.None)
        {
            return false;
        }
        int value = KeyInterop.VirtualKeyFromKey(key);
        if (value <= 0)
        {
            return false;
        }
        vk = (uint)value;
        return true;
    }
}
