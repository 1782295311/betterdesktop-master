// BetterDesktop.Shell.ContextMenus — @dll,-id 资源串解析（M1 管理器显示链）
// 技术力：72-右键菜单/shell-resource-ref-parse——SHLoadIndirectString 失败=空串，调用方兜底键名。

using System;
using System.Runtime.InteropServices;
using System.Text;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.ContextMenus.Services;

public static class ResourceRef
{
    /// <summary>解析 "@dll,-id" 形态；非该形态原样返回；解析失败返回 null（调用方回退键名）。</summary>
    public static string? Resolve(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith('@'))
        {
            return raw;
        }
        try
        {
            var buffer = new StringBuilder(1024);
            var hr = NativeMethods.SHLoadIndirectString(raw, buffer, buffer.Capacity, IntPtr.Zero);
            return hr == 0 && buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

}
