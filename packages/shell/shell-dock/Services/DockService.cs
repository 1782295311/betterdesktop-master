using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 服务实现。
/// </summary>
public class DockService : IDockService
{
    private readonly List<DockItemData> _items = new();
    private readonly object _lock = new();

    public IReadOnlyList<DockItemData> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList().AsReadOnly();
            }
        }
    }

    public void AddItem(DockItemData item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        lock (_lock)
        {
            // 按主键去重
            if (_items.Any(i => i.Id == item.Id))
            {
                throw new InvalidOperationException($"Item '{item.Id}' already exists.");
            }

            _items.Add(item);
        }
    }

    public void RemoveItem(DockItemId id)
    {
        if (id == default) throw new ArgumentException("Id cannot be empty.", nameof(id));

        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                _items.Remove(item);
            }
        }
    }

    public void UpdateItem(DockItemData item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        lock (_lock)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            if (index >= 0)
            {
                _items[index] = item;
            }
        }
    }

    public void ReorderItems(IReadOnlyList<DockItemId> order)
    {
        if (order == null) throw new ArgumentNullException(nameof(order));

        lock (_lock)
        {
            var reordered = new List<DockItemData>();
            foreach (var id in order)
            {
                var item = _items.FirstOrDefault(i => i.Id == id);
                if (item != null)
                {
                    reordered.Add(item);
                }
            }

            // 未在排序列表中的项目追加到末尾
            foreach (var item in _items)
            {
                if (!reordered.Contains(item))
                {
                    reordered.Add(item);
                }
            }

            _items.Clear();
            _items.AddRange(reordered);
        }
    }

    public void SetItemActive(DockItemId id, bool isActive)
    {
        if (id == default) throw new ArgumentException("Id cannot be empty.", nameof(id));

        lock (_lock)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index >= 0)
            {
                var item = _items[index];
                _items[index] = item with { IsRunning = isActive };
            }
        }
    }
}
