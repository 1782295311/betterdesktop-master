using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.PluginSdk;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 皮肤管理器（纯逻辑，无 UI 依赖）：枚举内置配色预设 + 用户本地图片皮肤，统一激活/移除。
/// 所有激活经 <see cref="IAppearanceService"/> 现有 setter 落地（复用 Changed 广播与持久化），
/// 不引入新状态、不破坏 DWM 生命周期。
/// </summary>
public sealed class SkinManager
{
    private const string SkinsDirRelative = "BetterDesktop\\skins";
    private readonly IAppearanceService _appearance;

    public SkinManager(IAppearanceService appearance)
    {
        _appearance = appearance;
    }

    /// <summary>内置配色预设名（无图，驱动 WindowTint/Accent/描边；具体值由 AppearanceService 单一真相源维护）。</summary>
    private static readonly IReadOnlySet<string> Presets = new HashSet<string>
    {
        "graphite", "midnight", "sakura", "mint"
    };

    /// <summary>枚举所有可用皮肤（内置预设 + 用户图片目录扫描）。</summary>
    public IReadOnlyList<SkinEntry> List()
    {
        var entries = new List<SkinEntry>();

        // 内置配色预设
        foreach (var key in Presets)
        {
            entries.Add(new SkinEntry
            {
                Id = "preset:" + key,
                Name = PresetDisplayName(key),
                Kind = SkinKind.Preset,
                ThumbnailPath = null // 预设用色块，无缩略图文件
            });
        }

        // 用户图片：扫描 %APPDATA%\BetterDesktop\skins\
        var dir = UserSkinsDir;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsSupportedImage))
            {
                entries.Add(new SkinEntry
                {
                    Id = "img:" + file,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Kind = SkinKind.Image,
                    ThumbnailPath = file
                });
            }
        }

        return entries;
    }

    /// <summary>激活指定皮肤：img: 前缀激活图片皮肤；preset: 前缀激活配色预设（整体覆盖对应 appearance.* 键）。</summary>
    public void Apply(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _appearance.SkinActive = "";
            return;
        }

        if (id.StartsWith("img:", StringComparison.Ordinal))
        {
            var path = id.Substring(4);
            if (!File.Exists(path))
            {
                // 图片失效：清除激活，不抛异常（UI 列表应已剔除，此处兜底）。
                _appearance.SkinActive = "";
                return;
            }
            _appearance.SkinActive = id;
            return;
        }

        if (id.StartsWith("preset:", StringComparison.Ordinal))
        {
            var key = id.Substring(7);
            // 预设应用逻辑已下沉到 AppearanceService.SkinActive setter（单一真相源）。
            // 此处仅校验预设是否存在，存在则交由 setter 整体覆盖对应 appearance.* 键。
            if (!Presets.Contains(key))
            {
                _appearance.SkinActive = "";
                return;
            }
            _appearance.SkinActive = id;
            return;
        }

        // 未知格式：清除
        _appearance.SkinActive = "";
    }

    /// <summary>移除用户图片皮肤（仅 img: 有效）。默认不删文件，仅从激活与逻辑列表移除；
    /// 调用方可另行决定是否删除物理文件。预设皮肤不可移除。</summary>
    public void Remove(string id)
    {
        if (!id.StartsWith("img:", StringComparison.Ordinal)) return;
        if (_appearance.SkinActive == id)
        {
            _appearance.SkinActive = "";
        }
        // 图片文件保留在 skins 目录由用户管理；如需物理删除由上层确认后 File.Delete。
    }

    /// <summary>当前激活皮肤 ID（空串=无）。</summary>
    public string ActiveId => _appearance.SkinActive;

    private static string UserSkinsDir
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, SkinsDirRelative);
        }
    }

    private static bool IsSupportedImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";
    }

    private static string PresetDisplayName(string key) => key switch
    {
        "graphite" => "石墨灰",
        "midnight" => "午夜蓝",
        "sakura" => "樱粉",
        "mint" => "薄荷绿",
        _ => key
    };
}

/// <summary>皮肤列表项（UI 与逻辑共用）。</summary>
public sealed class SkinEntry
{
    /// <summary>皮肤 ID（img:&lt;path&gt; 或 preset:&lt;name&gt;）。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>皮肤种类。</summary>
    public SkinKind Kind { get; init; }

    /// <summary>缩略图路径（图片皮肤=原图；预设=null，UI 用色块呈现）。</summary>
    public string? ThumbnailPath { get; init; }
}
