// BetterDesktop.Shell.Dock — 应用提取器自定义分组持久化
// 技术库资产：506-app-enumeration 的 AppGrabber"分类持久化"范式
// （cairoshell 原版用 Applications.xml，本仓库约定统一 JSON——语义一致：组名 → 成员主键集合）。
// 存储：%APPDATA%\BetterDesktop\app_groups.json，原子写（tmp + Move overwrite）。
// 主键 = DockItemId.ToString()（与固定列表同一业务主键规则，跨会话稳定）。

using System.IO;
using System.Text.Json;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>应用提取器自定义分组：组名 ↔ 成员主键的持久化存取。</summary>
internal sealed class AppGroupStore
{
    private sealed class GroupFile
    {
        public List<GroupDef> Groups { get; set; } = new();
    }

    private sealed class GroupDef
    {
        public string Name { get; set; } = "";
        public List<string> Members { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<string> _order = new();                                       // 组显示顺序（区分大小写去靠 _members 键）
    private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.OrdinalIgnoreCase); // 组名 → 主键集
    private readonly Dictionary<string, string> _groupOf = new(StringComparer.Ordinal);                 // 主键 → 组名

    public AppGroupStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop", "app_groups.json");
        Load();
    }

    /// <summary>现有分组名（按创建顺序）。</summary>
    public IReadOnlyList<string> Groups
    {
        get
        {
            lock (_gate)
            {
                return _order.ToArray();
            }
        }
    }

    /// <summary>指定应用所在分组（不在任何分组时 null）。</summary>
    public string? GetGroupOf(DockItemId id)
    {
        lock (_gate)
        {
            return _groupOf.TryGetValue(id.ToString(), out var group) ? group : null;
        }
    }

    /// <summary>创建空分组（已存在返回 false）。立即持久化。</summary>
    public bool CreateGroup(string group)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(group) || _members.ContainsKey(group))
            {
                return false;
            }

            _members[group] = new HashSet<string>(StringComparer.Ordinal);
            _order.Add(group);
            Save();
            return true;
        }
    }

    /// <summary>把应用移入分组（自动脱离原分组）；分组不存在则创建。立即持久化。</summary>
    public void MoveToGroup(DockItemId id, string group)
    {
        lock (_gate)
        {
            RemoveFromGroupCore(id);
            var key = id.ToString();
            if (!_members.TryGetValue(group, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _members[group] = set;
                _order.Add(group);
            }

            set.Add(key);
            _groupOf[key] = group;
            Save();
        }
    }

    /// <summary>把应用从所在分组移出（无分组则无操作）。立即持久化。</summary>
    public void RemoveFromGroup(DockItemId id)
    {
        lock (_gate)
        {
            if (RemoveFromGroupCore(id))
            {
                Save();
            }
        }
    }

    /// <summary>删除整个分组（成员回"未分组"）。立即持久化。分组不存在则无操作。</summary>
    public void DeleteGroup(string group)
    {
        lock (_gate)
        {
            if (!_members.Remove(group))
            {
                return;
            }

            _order.Remove(group);
            var doomed = new List<string>();
            foreach (var (key, g) in _groupOf)
            {
                if (string.Equals(g, group, StringComparison.OrdinalIgnoreCase))
                {
                    doomed.Add(key);
                }
            }

            foreach (var key in doomed)
            {
                _groupOf.Remove(key);
            }

            Save();
        }
    }

    /// <summary>核心移出（调用方持锁；不落盘，由外层决定是否 Save）。</summary>
    private bool RemoveFromGroupCore(DockItemId id)
    {
        var key = id.ToString();
        if (!_groupOf.Remove(key, out var group))
        {
            return false;
        }

        if (_members.TryGetValue(group, out var set))
        {
            set.Remove(key);
            if (set.Count == 0)
            {
                _members.Remove(group);
                _order.Remove(group);
            }
        }

        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<GroupFile>(File.ReadAllText(_path));
            if (file is null)
            {
                return;
            }

            foreach (var def in file.Groups)
            {
                // 只过滤无名组；空组（新建未移入成员）保留，避免"建完即消失"
                if (string.IsNullOrWhiteSpace(def.Name))
                {
                    continue;
                }

                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in def.Members)
                {
                    set.Add(key);
                    _groupOf[key] = def.Name;
                }

                _members[def.Name] = set;
                _order.Add(def.Name);
            }
        }
        catch
        {
            // 损坏/不可读按空分组处理（下次保存自然重建）；不阻断窗口加载
        }
    }

    private void Save()
    {
        try
        {
            var file = new GroupFile();
            foreach (var group in _order)
            {
                file.Groups.Add(new GroupDef
                {
                    Name = group,
                    Members = new List<string>(_members[group])
                });
            }

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 落盘失败静默（M10）：内存态仍可用，下次操作再试
        }
    }
}
