using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Dock;

public sealed class AppGrabberWindow : ShellWindow, IComponentConnector
{
	private enum SortMode
	{
		Alpha,
		Group,
		Favorites
	}

	private enum AppGrabberMode
	{
		Clean,
		AllPrograms
	}

	private readonly IDockAppsService _dockAppsService;

	private readonly IDockIconService _dockIconService;

	private readonly Action<IReadOnlyList<DockItemData>>? _onNewApps;

	private readonly List<DockItemData> _allCandidates = new List<DockItemData>();

	private HashSet<DockItemId> _pinnedIds = new HashSet<DockItemId>();

	private bool _suppressRefresh;

	private SortMode _currentSort = SortMode.Alpha;

	private AppGrabberMode _currentMode = AppGrabberMode.Clean;

	private bool _dragEnabled;

	private readonly HashSet<string> _favoriteApps = new HashSet<string>();

	private readonly Dictionary<string, HashSet<string>> _folders = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

	private readonly List<string> _pinnedFolders = new List<string>();

	private int _renderVersion;

	private const int AllProgramsBatchSize = 6;

	private bool _batchOrdering;

	private readonly Dictionary<DockItemId, int> _batchLabels = new Dictionary<DockItemId, int>();

	private int _batchNext = 1;

	private readonly Dictionary<DockItemId, Border> _batchControls = new Dictionary<DockItemId, Border>();

	private RadioButton? _preBatchFilter;

	private const int IconSize = 44;

	private const int ItemWidth = 96;

	private const int ItemHeight = 96;

	internal Border RootBorder;

	internal TextBox SearchBox;

	internal Button MinButton;

	internal Button CloseButton;

	internal RadioButton ModeClean;

	internal RadioButton ModeAll;

	internal StackPanel FilterSortPanel;

	internal RadioButton FilterAll;

	internal RadioButton FilterPinned;

	internal RadioButton FilterStartMenu;

	internal RadioButton FilterInstalled;

	internal RadioButton SortAlpha;

	internal RadioButton SortGroup;

	internal RadioButton SortFavorites;

	internal Button NewGroupButton;

	internal Button BatchOrderButton;

	internal ScrollViewer ListScroller;

	internal StackPanel ContentPanel;

	internal ScrollViewer FavoritesScroller;

	internal StackPanel FavoritesPanel;

	internal Grid BusyOverlay;

	internal TextBlock BusySpinner;

	internal TextBlock BusyText;

	private bool _contentLoaded;

	protected override bool DefaultTopmost => false;

	protected override bool DefaultShowActivated => true;

	public AppGrabberWindow(IVibrancyService vibrancy, IDockAppsService dockAppsService, IDockIconService dockIconService, IAppearanceService? appearance = null, Action<IReadOnlyList<DockItemData>>? onNewApps = null)
		: base((IAppearanceService)null, (IVibrancyService)null)
	{
		((ShellWindow)this).VibrancyService = vibrancy;
		((ShellWindow)this).AppearanceService = appearance;
		_dockAppsService = dockAppsService;
		_dockIconService = dockIconService;
		_onNewApps = onNewApps;
		InitializeComponent();
		((ShellWindow)this).ChromeBorder = RootBorder;
		((Window)this).WindowStartupLocation = WindowStartupLocation.CenterScreen;
		((FrameworkElement)this).Loaded += OnLoaded;
		((UIElement)this).KeyDown += OnKeyDown;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		LoadFavorites();
		LoadFolders();
		LoadPinnedFolders();
	}

	protected override void OnLoadedCore()
	{
		LoadCandidates();
		ModeClean.IsChecked = true;
		FilterAll.IsChecked = true;
		SortAlpha.IsChecked = true;
		ApplyModeVisibility();
		_dockAppsService.PinnedChanged += OnPinnedChanged;
		((Window)this).Closed += delegate
		{
			_dockAppsService.PinnedChanged -= OnPinnedChanged;
		};
	}

	private static Brush ThemeBrush(string key)
	{
		return (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.White;
	}

	private static void BindTheme(FrameworkElement target, DependencyProperty prop, string key)
	{
		target.SetResourceReference(prop, key);
	}

	protected override void OnAppearanceContentChanged(AppearanceChangedArgs e)
	{
	}

	private void OnPinnedChanged(object? sender, EventArgs e)
	{
		if (!_suppressRefresh)
		{
			RefreshPinnedState();
		}
	}

	private void ShowBusy(string text, string? subText = null)
	{
		if (BusyText != null)
		{
			BusyText.Text = text;
		}
		BusyOverlay.Visibility = Visibility.Visible;
	}

	private void HideBusy()
	{
		BusyOverlay.Visibility = Visibility.Collapsed;
	}

	private void LoadCandidates()
	{
		_renderVersion++;
		_allCandidates.Clear();
		if (_currentMode == AppGrabberMode.AllPrograms)
		{
			Dispatcher uiDispatcher = ((DispatcherObject)Application.Current).Dispatcher;
			ShowBusy("正在扫描磁盘上的程序…", "首次扫描会比较慢，请稍候");
			Task.Run(delegate
			{
				try
				{
					return _dockAppsService.ScanAllPrograms();
				}
				catch
				{
					return new List<DockItemData>();
				}
			}).ContinueWith(delegate(Task<IReadOnlyList<DockItemData>> t)
			{
				uiDispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					try
					{
						_allCandidates.Clear();
						_allCandidates.AddRange(t.Result);
						RefreshPinnedState();
					}
					finally
					{
						HideBusy();
					}
				}, Array.Empty<object>());
			});
			return;
		}
		try
		{
			_allCandidates.AddRange(_dockAppsService.ScanStartMenu());
		}
		catch
		{
		}
		try
		{
			_allCandidates.AddRange(_dockAppsService.ScanInstalledApps());
		}
		catch
		{
		}
		HashSet<DockItemId> seen = new HashSet<DockItemId>();
		_allCandidates.RemoveAll((DockItemData a) => !seen.Add(a.Id));
		RefreshPinnedState();
		try
		{
			IReadOnlyList<DockItemData> newlyInstalledApps = _dockAppsService.GetNewlyInstalledApps();
			if (newlyInstalledApps.Count > 0)
			{
				_onNewApps?.Invoke(newlyInstalledApps);
			}
		}
		catch
		{
		}
	}

	private void RefreshPinnedState()
	{
		_pinnedIds = _dockAppsService.Pinned.Select((DockItemData x) => x.Id).ToHashSet();
		DisplayCurrent();
	}

	private void DisplayCurrent()
	{
		if (ContentPanel == null)
		{
			return;
		}
		if (_currentMode == AppGrabberMode.AllPrograms)
		{
			_dragEnabled = false;
			ShowListMode();
			DisplayAllProgramsByFolder();
			return;
		}
		_dragEnabled = FilterPinned.IsChecked == true && !_batchOrdering;
		ContentPanel.Children.Clear();
		string searchText = SearchBox.Text?.Trim();
		IEnumerable<DockItemData> source = _allCandidates;
		bool flag = false;
		if (FilterPinned.IsChecked == true)
		{
			source = source.Where((DockItemData a) => _pinnedIds.Contains(a.Id));
			flag = true;
		}
		else if (FilterStartMenu.IsChecked == true)
		{
			source = source.Where((DockItemData a) => a.AppType != DockAppType.Url && IsShortcut(a));
			flag = true;
		}
		else if (FilterInstalled.IsChecked == true)
		{
			source = source.Where((DockItemData a) => !IsShortcut(a));
			flag = true;
		}
		if (!string.IsNullOrWhiteSpace(searchText))
		{
			source = source.Where((DockItemData a) => a.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
		}
		List<DockItemData> list = source.ToList();
		ShowListMode();
		if (list.Count == 0)
		{
			TextBlock textBlock = new TextBlock
			{
				Text = "没有匹配的应用",
				FontSize = 14.0,
				Margin = new Thickness(0.0, 24.0, 0.0, 0.0),
				HorizontalAlignment = HorizontalAlignment.Center
			};
			BindTheme(textBlock, TextBlock.ForegroundProperty, "ThemeMutedForeground");
			ContentPanel.Children.Add(textBlock);
			return;
		}
		if (FilterPinned.IsChecked == true)
		{
			if (_batchOrdering)
			{
				_batchControls.Clear();
			}
			IReadOnlyList<DockItemData> readOnlyList = _dockAppsService.Pinned;
			if (!string.IsNullOrWhiteSpace(searchText))
			{
				readOnlyList = readOnlyList.Where((DockItemData a) => a.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();
			}
			ShowListMode();
			if (readOnlyList.Count == 0)
			{
				TextBlock textBlock2 = new TextBlock
				{
					Text = "暂无固定应用（在列表中右键「固定到 Dock」）",
					FontSize = 14.0,
					Margin = new Thickness(0.0, 24.0, 0.0, 0.0),
					HorizontalAlignment = HorizontalAlignment.Center
				};
				BindTheme(textBlock2, TextBlock.ForegroundProperty, "ThemeMutedForeground");
				ContentPanel.Children.Add(textBlock2);
			}
			else
			{
				ContentPanel.Children.Add(MakeGroupHeader("已固定"));
				ContentPanel.Children.Add(MakeAppGrid(readOnlyList));
			}
			return;
		}
		if (!flag && _currentSort != SortMode.Alpha)
		{
			if (_currentSort == SortMode.Group)
			{
				ShowListMode();
				DisplayByGroup(list);
			}
			else
			{
				ShowFavoritesMode();
				DisplayFavoritesPanel(list);
			}
			return;
		}
		List<DockItemData> list2 = list.Where((DockItemData a) => _pinnedIds.Contains(a.Id)).OrderBy<DockItemData, string>((DockItemData a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
		List<DockItemData> list3 = list.Where((DockItemData a) => !_pinnedIds.Contains(a.Id) && IsShortcut(a)).OrderBy<DockItemData, string>((DockItemData a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
		List<DockItemData> list4 = list.Where((DockItemData a) => !_pinnedIds.Contains(a.Id) && !IsShortcut(a)).OrderBy<DockItemData, string>((DockItemData a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
		if (list2.Count > 0)
		{
			ContentPanel.Children.Add(MakeGroupHeader("已固定"));
			ContentPanel.Children.Add(MakeAppGrid(list2));
		}
		if (list3.Count > 0)
		{
			ContentPanel.Children.Add(MakeGroupHeader("开始菜单"));
			ContentPanel.Children.Add(MakeAppGrid(list3));
		}
		if (list4.Count > 0)
		{
			ContentPanel.Children.Add(MakeGroupHeader("已安装程序"));
			ContentPanel.Children.Add(MakeAppGrid(list4));
		}
	}

	private void DisplayByGroup(IReadOnlyList<DockItemData> apps)
	{
		Dictionary<string, List<DockItemData>> groups = new Dictionary<string, List<DockItemData>>(StringComparer.OrdinalIgnoreCase);
		foreach (DockItemData app in apps)
		{
			string folderFor = GetFolderFor(app.Id.ToString());
			string key = (string.IsNullOrWhiteSpace(folderFor) ? "未分类" : folderFor);
			if (!groups.TryGetValue(key, out List<DockItemData> value))
			{
				value = new List<DockItemData>();
				groups[key] = value;
			}
			value.Add(app);
		}
		List<string> first = _pinnedFolders.Where((string g) => groups.ContainsKey(g)).ToList();
		List<string> second = groups.Keys.Where((string g) => !_pinnedFolders.Contains(g)).OrderBy<string, string>((string g) => g, StringComparer.OrdinalIgnoreCase).ToList();
		foreach (string item in first.Concat(second))
		{
			ContentPanel.Children.Add(MakeGroupHeaderWithPin(item));
			ContentPanel.Children.Add(MakeAppGrid(groups[item].OrderBy<DockItemData, string>((DockItemData a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList()));
		}
	}

	private void DisplayFavoritesPanel(IReadOnlyList<DockItemData> apps)
	{
		FavoritesPanel.Children.Clear();
		List<DockItemData> list = apps.Where((DockItemData a) => _favoriteApps.Contains(a.Id.ToString())).OrderBy<DockItemData, string>((DockItemData a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		TextBlock element = new TextBlock
		{
			Text = "★",
			Foreground = Brushes.Gold,
			FontSize = 18.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		TextBlock textBlock = new TextBlock
		{
			Text = "我的收藏",
			FontSize = 18.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		};
		BindTheme(textBlock, TextBlock.ForegroundProperty, "ThemeForeground");
		stackPanel.Children.Add(element);
		stackPanel.Children.Add(textBlock);
		FavoritesPanel.Children.Add(stackPanel);
		if (list.Count == 0)
		{
			TextBlock textBlock2 = new TextBlock
			{
				Text = "还没有收藏的应用。回到「全部」视图，右键任意应用选择「收藏」即可加入此处。",
				FontSize = 13.0,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
				MaxWidth = 640.0
			};
			BindTheme(textBlock2, TextBlock.ForegroundProperty, "ThemeMutedForeground");
			FavoritesPanel.Children.Add(textBlock2);
			return;
		}
		TextBlock textBlock3 = new TextBlock
		{
			Text = $"共 {list.Count} 个收藏 · 单击卡片启动，右键可取消收藏",
			FontSize = 12.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		BindTheme(textBlock3, TextBlock.ForegroundProperty, "ThemeMutedForeground");
		FavoritesPanel.Children.Add(textBlock3);
		WrapPanel wrapPanel = new WrapPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		foreach (DockItemData item in list)
		{
			wrapPanel.Children.Add(CreateItem(item, removable: true));
		}
		FavoritesPanel.Children.Add(wrapPanel);
	}

	private void DisplayFavorites(IReadOnlyList<DockItemData> apps)
	{
		DisplayFavoritesPanel(apps);
	}

	private void DisplayAllProgramsByFolder()
	{
		int version = ++_renderVersion;
		ContentPanel.Children.Clear();
		string searchText = SearchBox.Text?.Trim();
		IEnumerable<DockItemData> source = _allCandidates;
		if (!string.IsNullOrWhiteSpace(searchText))
		{
			source = source.Where((DockItemData a) => a.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
		}
		List<DockItemData> list = source.ToList();
		if (list.Count == 0)
		{
			TextBlock textBlock = new TextBlock
			{
				Text = "未扫描到任何可运行程序",
				FontSize = 14.0,
				Margin = new Thickness(0.0, 24.0, 0.0, 0.0),
				HorizontalAlignment = HorizontalAlignment.Center
			};
			BindTheme(textBlock, TextBlock.ForegroundProperty, "ThemeMutedForeground");
			ContentPanel.Children.Add(textBlock);
			return;
		}
		Dictionary<string, List<DockItemData>> dictionary = new Dictionary<string, List<DockItemData>>(StringComparer.OrdinalIgnoreCase);
		foreach (DockItemData item in list)
		{
			string directoryName = Path.GetDirectoryName(item.TargetPath);
			string key = (string.IsNullOrWhiteSpace(directoryName) ? "(未知位置)" : directoryName);
			if (!dictionary.TryGetValue(key, out var value))
			{
				value = (dictionary[key] = new List<DockItemData>());
			}
			value.Add(item);
		}
		List<(string Folder, List<DockItemData> Apps)> ordered = (from x in dictionary.OrderBy<KeyValuePair<string, List<DockItemData>>, string>((KeyValuePair<string, List<DockItemData>> x) => x.Key, StringComparer.OrdinalIgnoreCase)
			select (Folder: x.Key, Apps: x.Value.OrderBy<DockItemData, string>((DockItemData a) => a.Name, StringComparer.OrdinalIgnoreCase).ToList())).ToList();
		Dispatcher dispatcher = ((DispatcherObject)Application.Current).Dispatcher;
		int index = 0;
		RenderNextBatch();
		void RenderNextBatch()
		{
			if (version == _renderVersion && ((FrameworkElement)this).IsLoaded)
			{
				int num = 0;
				while (num < 6 && index < ordered.Count)
				{
					(string, List<DockItemData>) tuple = ordered[index];
					ContentPanel.Children.Add(MakeGroupHeader(tuple.Item1));
					ContentPanel.Children.Add(MakeAppGrid(tuple.Item2));
					num++;
					index++;
				}
				if (index < ordered.Count)
				{
					dispatcher.BeginInvoke((DispatcherPriority)4, (Delegate)new Action(RenderNextBatch));
				}
			}
		}
	}

	private void ApplyModeVisibility()
	{
		if (FilterSortPanel != null)
		{
			FilterSortPanel.Visibility = ((_currentMode != AppGrabberMode.Clean) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (BatchOrderButton != null)
		{
			BatchOrderButton.Visibility = ((_currentMode != AppGrabberMode.Clean) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (_currentMode != AppGrabberMode.Clean && _batchOrdering)
		{
			ExitBatchOrder();
		}
	}

	private void OnModeCleanClick(object sender, RoutedEventArgs e)
	{
		if (_currentMode != AppGrabberMode.Clean)
		{
			if (_batchOrdering)
			{
				ExitBatchOrder();
			}
			_currentMode = AppGrabberMode.Clean;
			_currentSort = SortMode.Alpha;
			ApplyModeVisibility();
			LoadCandidates();
		}
	}

	private void OnModeAllClick(object sender, RoutedEventArgs e)
	{
		if (_currentMode != AppGrabberMode.AllPrograms)
		{
			if (_batchOrdering)
			{
				ExitBatchOrder();
			}
			_currentMode = AppGrabberMode.AllPrograms;
			ApplyModeVisibility();
			LoadCandidates();
		}
	}

	private void OnBatchOrderClick(object sender, RoutedEventArgs e)
	{
		if (_batchOrdering)
		{
			ExitBatchOrder();
			DisplayCurrent();
			return;
		}
		_batchOrdering = true;
		_batchLabels.Clear();
		_batchControls.Clear();
		_batchNext = 1;
		_preBatchFilter = GetCurrentFilterRadio();
		_currentMode = AppGrabberMode.Clean;
		FilterPinned.IsChecked = true;
		_currentSort = SortMode.Alpha;
		ApplyModeVisibility();
		DisplayCurrent();
		if (BatchOrderButton != null)
		{
			BatchOrderButton.Content = "退出排序";
		}
	}

	private RadioButton GetCurrentFilterRadio()
	{
		if (FilterPinned.IsChecked == true)
		{
			return FilterPinned;
		}
		if (FilterStartMenu.IsChecked == true)
		{
			return FilterStartMenu;
		}
		if (FilterInstalled.IsChecked == true)
		{
			return FilterInstalled;
		}
		return FilterAll;
	}

	private void ExitBatchOrder()
	{
		_batchOrdering = false;
		_batchLabels.Clear();
		foreach (Border value in _batchControls.Values)
		{
			value.Visibility = Visibility.Collapsed;
		}
		_batchControls.Clear();
		_batchNext = 1;
		if (_preBatchFilter != null)
		{
			RadioButton preBatchFilter = _preBatchFilter;
			_preBatchFilter = null;
			preBatchFilter.IsChecked = true;
		}
		if (BatchOrderButton != null)
		{
			BatchOrderButton.Content = "批量排序";
		}
	}

	private void RegisterBatchControl(DockItemId id, Border badge)
	{
		if (!_batchOrdering)
		{
			return;
		}
		_batchControls[id] = badge;
		if (_batchLabels.TryGetValue(id, out var value))
		{
			badge.Visibility = Visibility.Visible;
			if (badge.Child is TextBlock textBlock)
			{
				textBlock.Text = value.ToString();
			}
		}
	}

	private void OnBatchItemClicked(DockItemId id)
	{
		if (!_batchOrdering)
		{
			return;
		}
		if (_batchLabels.TryGetValue(id, out var existing))
		{
			_batchLabels.Remove(id);
			if (_batchControls.TryGetValue(id, out Border value))
			{
				value.Visibility = Visibility.Collapsed;
			}
			List<KeyValuePair<DockItemId, int>> list = _batchLabels.Where((KeyValuePair<DockItemId, int> kvp) => kvp.Value > existing).ToList();
			foreach (KeyValuePair<DockItemId, int> item in list)
			{
				_batchLabels[item.Key] = item.Value - 1;
			}
			_batchNext = _batchLabels.Count + 1;
			RefreshBatchBadges();
			return;
		}
		int value2 = _batchNext++;
		_batchLabels[id] = value2;
		if (_batchControls.TryGetValue(id, out Border value3))
		{
			value3.Visibility = Visibility.Visible;
			if (value3.Child is TextBlock textBlock)
			{
				textBlock.Text = value2.ToString();
			}
		}
		int count = _batchControls.Count;
		int num = _batchControls.Keys.Count((DockItemId k) => _batchLabels.ContainsKey(k));
		if (count > 0 && num == count)
		{
			ApplyBatchOrder();
		}
	}

	private void RefreshBatchBadges()
	{
		foreach (KeyValuePair<DockItemId, Border> batchControl in _batchControls)
		{
			if (_batchLabels.TryGetValue(batchControl.Key, out var value))
			{
				batchControl.Value.Visibility = Visibility.Visible;
				if (batchControl.Value.Child is TextBlock textBlock)
				{
					textBlock.Text = value.ToString();
				}
			}
		}
	}

	private void ApplyBatchOrder()
	{
		List<DockItemId> order = (from kvp in _batchLabels
			orderby kvp.Value
			select kvp.Key).ToList();
		try
		{
			_dockAppsService.Reorder(order);
			_dockAppsService.Save();
		}
		catch
		{
		}
		ExitBatchOrder();
		RefreshPinnedState();
		DisplayCurrent();
	}

	private void ShowListMode()
	{
		if (ListScroller != null)
		{
			ListScroller.Visibility = Visibility.Visible;
		}
		if (FavoritesScroller != null)
		{
			FavoritesScroller.Visibility = Visibility.Collapsed;
		}
	}

	private void ShowFavoritesMode()
	{
		if (ListScroller != null)
		{
			ListScroller.Visibility = Visibility.Collapsed;
		}
		if (FavoritesScroller != null)
		{
			FavoritesScroller.Visibility = Visibility.Visible;
		}
	}

	private FrameworkElement MakeGroupHeaderWithPin(string name)
	{
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 8.0)
		};
		TextBlock textBlock = new TextBlock
		{
			Text = name,
			FontSize = 15.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		};
		BindTheme(textBlock, TextBlock.ForegroundProperty, "ThemeForeground");
		DockPanel.SetDock(textBlock, System.Windows.Controls.Dock.Left);
		bool flag = _pinnedFolders.Contains(name);
		Button button = new Button
		{
			Content = (flag ? "取消置顶" : "置顶"),
			FontSize = 11.0,
			Padding = new Thickness(8.0, 2.0, 8.0, 2.0),
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			Foreground = Brushes.White,
			Background = new SolidColorBrush(Color.FromArgb(204, 26, 26, 30)),
			BorderThickness = new Thickness(0.0)
		};
		if (name.Equals("未分类", StringComparison.OrdinalIgnoreCase))
		{
			button.IsEnabled = false;
			button.Opacity = 0.4;
		}
		else
		{
			button.Click += delegate
			{
				TogglePinFolder(name);
			};
		}
		DockPanel.SetDock(button, System.Windows.Controls.Dock.Right);
		dockPanel.Children.Add(button);
		dockPanel.Children.Add(textBlock);
		return dockPanel;
	}

	private void TogglePinFolder(string folderName)
	{
		if (!string.IsNullOrWhiteSpace(folderName) && !folderName.Equals("未分类", StringComparison.OrdinalIgnoreCase))
		{
			if (_pinnedFolders.Contains(folderName))
			{
				_pinnedFolders.Remove(folderName);
			}
			else
			{
				_pinnedFolders.Add(folderName);
			}
			SavePinnedFolders();
			DisplayCurrent();
		}
	}

	private void OnNewGroupClick(object sender, RoutedEventArgs e)
	{
		FolderInputWindow folderInputWindow = new FolderInputWindow();
		folderInputWindow.Owner = (Window)(object)this;
		if (folderInputWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(folderInputWindow.FolderName))
		{
			return;
		}
		string text = folderInputWindow.FolderName.Trim();
		if (!text.Equals("未分类", StringComparison.OrdinalIgnoreCase))
		{
			if (!_folders.ContainsKey(text))
			{
				_folders[text] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				SaveFolders();
			}
			if (!_pinnedFolders.Contains(text))
			{
				_pinnedFolders.Add(text);
				SavePinnedFolders();
			}
			_currentSort = SortMode.Group;
			SortGroup.IsChecked = true;
			DisplayCurrent();
		}
	}

	private static string GetPinnedFoldersPath()
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return Path.Combine(folderPath, "BetterDesktop", "grabber-pinned-folders.txt");
	}

	private void LoadPinnedFolders()
	{
		try
		{
			string pinnedFoldersPath = GetPinnedFoldersPath();
			if (!File.Exists(pinnedFoldersPath))
			{
				return;
			}
			_pinnedFolders.Clear();
			string[] array = File.ReadAllLines(pinnedFoldersPath);
			foreach (string text in array)
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					_pinnedFolders.Add(text.Trim());
				}
			}
		}
		catch
		{
		}
	}

	private void SavePinnedFolders()
	{
		try
		{
			string pinnedFoldersPath = GetPinnedFoldersPath();
			string directoryName = Path.GetDirectoryName(pinnedFoldersPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllLines(pinnedFoldersPath, _pinnedFolders);
		}
		catch
		{
		}
	}

	private void OnSortAlphaClick(object sender, RoutedEventArgs e)
	{
		if (!_batchOrdering)
		{
			_currentSort = SortMode.Alpha;
			DisplayCurrent();
		}
	}

	private void OnSortGroupClick(object sender, RoutedEventArgs e)
	{
		if (!_batchOrdering)
		{
			_currentSort = SortMode.Group;
			DisplayCurrent();
		}
	}

	private void OnShowFavoritesClick(object sender, RoutedEventArgs e)
	{
		if (!_batchOrdering)
		{
			_currentSort = SortMode.Favorites;
			DisplayCurrent();
		}
	}

	private void OnKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		if ((int)e.Key == 13)
		{
			((Window)this).Close();
		}
	}

	private static string GetFirstLetter(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "#";
		}
		char c = name[0];
		if (char.IsLetter(c))
		{
			return char.ToUpper(c).ToString();
		}
		if (char.IsDigit(c))
		{
			return "0-9";
		}
		return "#";
	}

	private string? GetFolderFor(string appId)
	{
		foreach (KeyValuePair<string, HashSet<string>> folder in _folders)
		{
			if (folder.Value.Contains(appId))
			{
				return folder.Key;
			}
		}
		return null;
	}

	private void AssignFolder(DockItemData app, string folderName)
	{
		string item = app.Id.ToString();
		foreach (HashSet<string> value2 in _folders.Values)
		{
			value2.Remove(item);
		}
		List<string> list = (from kvp in _folders
			where kvp.Value.Count == 0
			select kvp.Key).ToList();
		foreach (string item2 in list)
		{
			_folders.Remove(item2);
		}
		if (!string.IsNullOrWhiteSpace(folderName))
		{
			if (!_folders.TryGetValue(folderName, out HashSet<string> value))
			{
				value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				_folders[folderName] = value;
			}
			value.Add(item);
		}
		SaveFolders();
		DisplayCurrent();
	}

	private void PromptNewFolder(DockItemData app)
	{
		FolderInputWindow folderInputWindow = new FolderInputWindow();
		folderInputWindow.Owner = (Window)(object)this;
		if (folderInputWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(folderInputWindow.FolderName))
		{
			string text = folderInputWindow.FolderName.Trim();
			if (text.Equals("未分类", StringComparison.OrdinalIgnoreCase))
			{
				AssignFolder(app, string.Empty);
			}
			else
			{
				AssignFolder(app, text);
			}
		}
	}

	private static string GetFavoritesPath()
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return Path.Combine(folderPath, "BetterDesktop", "grabber-favorites.txt");
	}

	private static string GetFoldersPath()
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return Path.Combine(folderPath, "BetterDesktop", "grabber-folders.json");
	}

	private void LoadFavorites()
	{
		try
		{
			string favoritesPath = GetFavoritesPath();
			if (!File.Exists(favoritesPath))
			{
				return;
			}
			_favoriteApps.Clear();
			string[] array = File.ReadAllLines(favoritesPath);
			foreach (string text in array)
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					_favoriteApps.Add(text.Trim());
				}
			}
		}
		catch
		{
		}
	}

	private void SaveFavorites()
	{
		try
		{
			string favoritesPath = GetFavoritesPath();
			string directoryName = Path.GetDirectoryName(favoritesPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllLines(favoritesPath, _favoriteApps);
		}
		catch
		{
		}
	}

	private void LoadFolders()
	{
		try
		{
			_folders.Clear();
			string foldersPath = GetFoldersPath();
			if (!File.Exists(foldersPath))
			{
				return;
			}
			string json = File.ReadAllText(foldersPath);
			Dictionary<string, List<string>> dictionary = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
			if (dictionary == null)
			{
				return;
			}
			foreach (KeyValuePair<string, List<string>> item in dictionary)
			{
				if (!string.IsNullOrWhiteSpace(item.Key))
				{
					_folders[item.Key] = new HashSet<string>(item.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
				}
			}
		}
		catch
		{
		}
	}

	private void SaveFolders()
	{
		try
		{
			string foldersPath = GetFoldersPath();
			string directoryName = Path.GetDirectoryName(foldersPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			Dictionary<string, List<string>> value = _folders.ToDictionary<KeyValuePair<string, HashSet<string>>, string, List<string>>((KeyValuePair<string, HashSet<string>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<string>> kvp) => kvp.Value.ToList());
			File.WriteAllText(foldersPath, JsonSerializer.Serialize(value, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}

	private static bool IsShortcut(DockItemData app)
	{
		switch (Path.GetExtension(app.ShortcutPath))
		{
		case ".lnk":
		case ".url":
		case ".appref-ms":
			return true;
		default:
			return false;
		}
	}

	private static TextBlock MakeGroupHeader(string title)
	{
		TextBlock textBlock = new TextBlock
		{
			Text = title,
			FontSize = 15.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 12.0, 0.0, 8.0)
		};
		BindTheme(textBlock, TextBlock.ForegroundProperty, "ThemeForeground");
		return textBlock;
	}

	private FrameworkElement MakeAppGrid(IReadOnlyList<DockItemData> apps)
	{
		WrapPanel wrapPanel = new WrapPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		};
		foreach (DockItemData app in apps)
		{
			wrapPanel.Children.Add(CreateItem(app));
		}
		return wrapPanel;
	}

	private FrameworkElement CreateItem(DockItemData app, bool removable = false)
	{
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_06fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0703: Unknown result type (might be due to invalid IL or missing references)
		//IL_071d: Expected O, but got Unknown
		//IL_0772: Unknown result type (might be due to invalid IL or missing references)
		bool flag = _pinnedIds.Contains(app.Id);
		bool flag2 = _favoriteApps.Contains(app.Id.ToString());
		Grid container = new Grid
		{
			Width = 96.0,
			Height = 96.0,
			Cursor = Cursors.Hand,
			Background = Brushes.Transparent,
			Margin = new Thickness(5.0),
			AllowDrop = true,
			Tag = app
		};
		container.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		container.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		Image iconImage = new Image
		{
			Width = 44.0,
			Height = 44.0,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			UseLayoutRounding = true,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Source = CreatePlaceholderIcon(),
			RenderTransformOrigin = new Point(0.5, 0.5),
			RenderTransform = new ScaleTransform(1.0, 1.0)
		};
		RenderOptions.SetBitmapScalingMode((DependencyObject)(object)iconImage, BitmapScalingMode.HighQuality);
		TextBlock textBlock = new TextBlock
		{
			Text = app.Name,
			FontSize = 11.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			HorizontalAlignment = HorizontalAlignment.Center,
			TextAlignment = TextAlignment.Center,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
			MaxWidth = 88.0
		};
		BindTheme(textBlock, TextBlock.ForegroundProperty, "ThemeForeground");
		Border border = new Border();
		border.Width = 16.0;
		border.Height = 16.0;
		border.CornerRadius = new CornerRadius(8.0);
		border.Background = (flag ? ((Application.Current?.TryFindResource("AccentBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(43, 138, 62))) : Brushes.Transparent);
		border.HorizontalAlignment = HorizontalAlignment.Right;
		border.VerticalAlignment = VerticalAlignment.Top;
		border.Margin = new Thickness(0.0, 2.0, 6.0, 0.0);
		border.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		Border border2 = border;
		border2.Child = new TextBlock
		{
			Text = "?",
			Foreground = Brushes.White,
			FontSize = 9.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		TextBlock favMark = new TextBlock
		{
			Text = (flag2 ? "★" : ""),
			Foreground = Brushes.Gold,
			FontSize = 12.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(6.0, 2.0, 0.0, 0.0)
		};
		Border border3 = new Border
		{
			Width = 22.0,
			Height = 22.0,
			CornerRadius = new CornerRadius(11.0),
			Background = new SolidColorBrush(Color.FromArgb(204, 26, 26, 30)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(230, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.2),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		border3.Child = new TextBlock
		{
			Text = "",
			Foreground = Brushes.White,
			FontSize = 12.0,
			FontWeight = FontWeights.Bold,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetRow(iconImage, 0);
		Grid.SetRow(textBlock, 1);
		container.Children.Add(iconImage);
		container.Children.Add(textBlock);
		container.Children.Add(border2);
		container.Children.Add(favMark);
		container.Children.Add(border3);
		RegisterBatchControl(app.Id, border3);
		if (removable)
		{
			Button button = new Button
			{
				Content = "?",
				Width = 18.0,
				Height = 18.0,
				FontSize = 10.0,
				Padding = new Thickness(0.0),
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 2.0, 2.0, 0.0),
				Cursor = Cursors.Hand,
				Background = new SolidColorBrush(Color.FromArgb(204, 51, 51, 51)),
				Foreground = Brushes.White,
				BorderThickness = new Thickness(0.0),
				ToolTip = "从收藏中移除"
			};
			button.Click += delegate
			{
				_favoriteApps.Remove(app.Id.ToString());
				SaveFavorites();
				if (_currentSort == SortMode.Favorites)
				{
					DisplayCurrent();
				}
			};
			container.Children.Add(button);
		}
		Border card = new Border
		{
			CornerRadius = new CornerRadius(10.0),
			BorderThickness = new Thickness(0.0),
			Child = container
		};
		DispatcherTimer clickTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(320.0)
		};
		bool pendingClick = false;
		container.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			if (_batchOrdering)
			{
				e.Handled = true;
				OnBatchItemClicked(app.Id);
			}
			else if (pendingClick)
			{
				pendingClick = false;
				clickTimer.Stop();
				e.Handled = true;
				Launch(app);
			}
			else
			{
				pendingClick = true;
				clickTimer.Stop();
				clickTimer.Start();
			}
		};
		clickTimer.Tick += delegate
		{
			clickTimer.Stop();
			if (pendingClick)
			{
				pendingClick = false;
				if (_batchOrdering)
				{
					OnBatchItemClicked(app.Id);
				}
				else if (FilterPinned.IsChecked != true)
				{
					TogglePin(app);
				}
			}
		};
		container.MouseRightButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			ShowContextMenu(app, favMark, e);
		};
		Point dragStartPoint = default(Point);
		bool dragInProgress = false;
		Brush accentBrush = (Application.Current?.TryFindResource("AccentBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(43, 138, 62));
		container.PreviewMouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
			if (_dragEnabled)
			{
				dragStartPoint = e.GetPosition(null);
				dragInProgress = false;
			}
		};
		container.MouseMove += delegate(object _, MouseEventArgs e)
		{
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0043: Unknown result type (might be due to invalid IL or missing references)
			if (!(!_dragEnabled | dragInProgress) && e.LeftButton == MouseButtonState.Pressed)
			{
				Vector val = e.GetPosition(null) - dragStartPoint;
				if (!(Math.Abs(((Vector)(ref val)).X) < 6.0) || !(Math.Abs(((Vector)(ref val)).Y) < 6.0))
				{
					dragInProgress = true;
					try
					{
						card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.45, TimeSpan.FromMilliseconds(130.0))
						{
							EasingFunction = new CubicEase
							{
								EasingMode = EasingMode.EaseOut
							}
						});
						DragDrop.DoDragDrop((DependencyObject)(object)container, app.Id, DragDropEffects.Move);
					}
					catch
					{
					}
					finally
					{
						card.BeginAnimation(UIElement.OpacityProperty, null);
						card.Opacity = 1.0;
						card.BorderThickness = new Thickness(0.0);
						dragInProgress = false;
					}
				}
			}
		};
		container.DragOver += delegate(object _, DragEventArgs e)
		{
			if (_dragEnabled && e.Data.GetDataPresent(typeof(DockItemId)))
			{
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
				if (card.BorderThickness.Bottom != 2.0)
				{
					card.BorderThickness = new Thickness(2.0);
					card.BorderBrush = accentBrush;
				}
			}
		};
		container.DragLeave += delegate
		{
			if (_dragEnabled)
			{
				card.BorderThickness = new Thickness(0.0);
			}
		};
		container.Drop += delegate(object _, DragEventArgs e)
		{
			card.BorderThickness = new Thickness(0.0);
			if (_dragEnabled && e.Data.GetDataPresent(typeof(DockItemId)))
			{
				e.Handled = true;
				DockItemId dockItemId = (DockItemId)e.Data.GetData(typeof(DockItemId));
				if (container.Tag is DockItemData dockItemData && !(dockItemId == dockItemData.Id))
				{
					ReorderPinned(dockItemId, dockItemData.Id);
				}
			}
		};
		container.MouseEnter += delegate
		{
			ScaleTransform scaleTransform = (ScaleTransform)iconImage.RenderTransform;
			scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.15, TimeSpan.FromMilliseconds(150.0)));
			scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.15, TimeSpan.FromMilliseconds(150.0)));
		};
		container.MouseLeave += delegate
		{
			ScaleTransform scaleTransform = (ScaleTransform)iconImage.RenderTransform;
			scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.15, 1.0, TimeSpan.FromMilliseconds(150.0)));
			scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.15, 1.0, TimeSpan.FromMilliseconds(150.0)));
		};
		LoadIconAsync(app, iconImage);
		return card;
	}

	private void TogglePin(DockItemData app)
	{
		try
		{
			_suppressRefresh = true;
			if (_pinnedIds.Contains(app.Id))
			{
				_dockAppsService.RemoveById(app.Id);
			}
			else
			{
				string text = ((!string.IsNullOrWhiteSpace(app.ShortcutPath) && ShellLinkResolver.IsSupportedFile(app.ShortcutPath)) ? app.ShortcutPath : app.TargetPath);
				if (!string.IsNullOrWhiteSpace(text) && ShellLinkResolver.IsSupportedFile(text))
				{
					_dockAppsService.AddByPath(text);
				}
			}
			_dockAppsService.Save();
		}
		finally
		{
			_suppressRefresh = false;
		}
		RefreshPinnedState();
	}

	private void ReorderPinned(DockItemId sourceId, DockItemId targetId)
	{
		if (sourceId == targetId)
		{
			return;
		}
		List<DockItemId> list = _dockAppsService.Pinned.Select((DockItemData x) => x.Id).ToList();
		if (list.Contains(sourceId) && list.Contains(targetId))
		{
			list.Remove(sourceId);
			int num = list.IndexOf(targetId);
			if (num < 0)
			{
				list.Add(sourceId);
			}
			else
			{
				list.Insert(num, sourceId);
			}
			_dockAppsService.Reorder(list);
			_dockAppsService.Save();
			RefreshPinnedState();
			DisplayCurrent();
		}
	}

	private void ShowContextMenu(DockItemData app, TextBlock favMark, MouseButtonEventArgs e)
	{
		bool flag = _pinnedIds.Contains(app.Id);
		bool flag2 = _favoriteApps.Contains(app.Id.ToString());
		ContextMenu contextMenu = new ContextMenu();
		MenuItem menuItem = new MenuItem
		{
			Header = (flag ? "取消固定" : "固定到 Dock")
		};
		menuItem.Click += delegate
		{
			TogglePin(app);
		};
		contextMenu.Items.Add(menuItem);
		MenuItem menuItem2 = new MenuItem
		{
			Header = (flag2 ? "取消收藏" : "收藏")
		};
		menuItem2.Click += delegate
		{
			ToggleFavorite(app, favMark);
		};
		contextMenu.Items.Add(menuItem2);
		if (_currentMode == AppGrabberMode.Clean)
		{
			MenuItem menuItem3 = new MenuItem
			{
				Header = "排除（不再显示）"
			};
			menuItem3.Click += delegate
			{
				_dockAppsService.ExcludeApp(app);
				_allCandidates.RemoveAll((DockItemData a) => a.Id == app.Id);
				DisplayCurrent();
			};
			contextMenu.Items.Add(menuItem3);
		}
		MenuItem menuItem4 = new MenuItem
		{
			Header = "移动到分类"
		};
		foreach (string folderName in _folders.Keys.OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase))
		{
			MenuItem menuItem5 = new MenuItem
			{
				Header = folderName
			};
			menuItem5.Click += delegate
			{
				AssignFolder(app, folderName);
			};
			menuItem4.Items.Add(menuItem5);
		}
		MenuItem menuItem6 = new MenuItem
		{
			Header = "新建分类…"
		};
		menuItem6.Click += delegate
		{
			PromptNewFolder(app);
		};
		menuItem4.Items.Add(menuItem6);
		if (GetFolderFor(app.Id.ToString()) != null)
		{
			menuItem4.Items.Add(new Separator());
			MenuItem menuItem7 = new MenuItem
			{
				Header = "移出分类（未分类）"
			};
			menuItem7.Click += delegate
			{
				AssignFolder(app, string.Empty);
			};
			menuItem4.Items.Add(menuItem7);
		}
		contextMenu.Items.Add(menuItem4);
		contextMenu.Items.Add(new Separator());
		MenuItem menuItem8 = new MenuItem
		{
			Header = "启动"
		};
		menuItem8.Click += delegate
		{
			Launch(app);
		};
		contextMenu.Items.Add(menuItem8);
		MenuItem menuItem9 = new MenuItem
		{
			Header = "打开所在目录"
		};
		menuItem9.Click += delegate
		{
			OpenContainingDirectory(app);
		};
		contextMenu.Items.Add(menuItem9);
		if (_currentMode == AppGrabberMode.Clean && !string.IsNullOrWhiteSpace(app.UninstallCommand))
		{
			contextMenu.Items.Add(new Separator());
			MenuItem menuItem10 = new MenuItem
			{
				Header = "卸载"
			};
			menuItem10.Click += delegate
			{
				Uninstall(app);
			};
			contextMenu.Items.Add(menuItem10);
		}
		contextMenu.PlacementTarget = (UIElement)(((object)(e.OriginalSource as UIElement)) ?? ((object)this));
		contextMenu.IsOpen = true;
	}

	private void Uninstall(DockItemData app)
	{
		try
		{
			string text = app.UninstallCommand.Trim();
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				FileName = "cmd.exe",
				Arguments = "/c " + text
			};
			Process proc = Process.Start(startInfo);
			if (proc == null)
			{
				return;
			}
			Dispatcher uiDispatcher = ((DispatcherObject)Application.Current).Dispatcher;
			Task.Run(delegate
			{
				try
				{
					proc.WaitForExit();
				}
				catch
				{
				}
			}).ContinueWith((Task _) => uiDispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				try
				{
					_dockAppsService.InvalidateScanCache();
					LoadCandidates();
				}
				catch
				{
				}
			}, Array.Empty<object>()));
		}
		catch
		{
		}
	}

	private void ToggleFavorite(DockItemData app, TextBlock favMark)
	{
		string item = app.Id.ToString();
		if (_favoriteApps.Contains(item))
		{
			_favoriteApps.Remove(item);
			favMark.Text = "";
		}
		else
		{
			_favoriteApps.Add(item);
			favMark.Text = "★";
		}
		SaveFavorites();
		if (_currentSort == SortMode.Favorites)
		{
			DisplayCurrent();
		}
	}

	private void Launch(DockItemData app)
	{
		try
		{
			string text = ((!string.IsNullOrWhiteSpace(app.TargetPath)) ? app.TargetPath : app.ShortcutPath);
			if (!string.IsNullOrWhiteSpace(text))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = text,
					UseShellExecute = true
				});
			}
		}
		catch
		{
		}
	}

	private void OpenContainingDirectory(DockItemData app)
	{
		try
		{
			string text = ((!string.IsNullOrWhiteSpace(app.TargetPath)) ? app.TargetPath : app.ShortcutPath);
			if (!string.IsNullOrWhiteSpace(text) && Directory.Exists(Path.GetDirectoryName(text)))
			{
				Process.Start("explorer.exe", Path.GetDirectoryName(text));
			}
		}
		catch
		{
		}
	}

	private void OnAddFileClick(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = "选择一个或多个应用（exe/快捷方式/网址）",
			Multiselect = true,
			Filter = "应用与快捷方式|*.exe;*.bat;*.cmd;*.com;*.msc;*.lnk;*.url;*.appref-ms|所有文件|*.*"
		};
		if (openFileDialog.ShowDialog((Window)(object)this) != true)
		{
			return;
		}
		string[] fileNames = openFileDialog.FileNames;
		foreach (string text in fileNames)
		{
			if (ShellLinkResolver.IsSupportedFile(text))
			{
				_dockAppsService.AddByPath(text);
			}
		}
		_dockAppsService.Save();
		LoadCandidates();
	}

	private void OnCloseClick(object sender, RoutedEventArgs e)
	{
		((Window)this).Close();
	}

	private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			((Window)this).DragMove();
		}
	}

	private void OnMinimizeClick(object sender, RoutedEventArgs e)
	{
		((Window)this).WindowState = WindowState.Minimized;
	}

	private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
	{
		DisplayCurrent();
	}

	private void OnFilterChanged(object sender, RoutedEventArgs e)
	{
		if (!_batchOrdering)
		{
			_currentSort = SortMode.Alpha;
			SortAlpha.IsChecked = true;
			DisplayCurrent();
		}
	}

	private async Task LoadIconAsync(DockItemData item, Image target)
	{
		try
		{
			ImageSource icon = await _dockIconService.GetIconAsync(item);
			if (icon != null)
			{
				target.Source = icon;
			}
		}
		catch
		{
		}
	}

	private static ImageSource CreatePlaceholderIcon()
	{
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		DrawingVisual drawingVisual = new DrawingVisual();
		using DrawingContext drawingContext = drawingVisual.RenderOpen();
		drawingContext.DrawRectangle(Brushes.DimGray, null, new Rect(0.0, 0.0, 44.0, 44.0));
		drawingContext.DrawText(new FormattedText("?", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20.0, Brushes.White, 1.0), new Point(15.0, 10.0));
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(44, 44, 96.0, 96.0, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(drawingVisual);
		((Freezable)renderTargetBitmap).Freeze();
		return renderTargetBitmap;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.10.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/BetterDesktop.Shell.Dock;component/appgrabberwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.10.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			RootBorder = (Border)target;
			break;
		case 2:
			((StackPanel)target).MouseLeftButtonDown += OnTitleBarMouseDown;
			break;
		case 3:
			SearchBox = (TextBox)target;
			SearchBox.TextChanged += OnSearchTextChanged;
			break;
		case 4:
			MinButton = (Button)target;
			MinButton.Click += OnMinimizeClick;
			break;
		case 5:
			CloseButton = (Button)target;
			CloseButton.Click += OnCloseClick;
			break;
		case 6:
			ModeClean = (RadioButton)target;
			ModeClean.Checked += OnModeCleanClick;
			break;
		case 7:
			ModeAll = (RadioButton)target;
			ModeAll.Checked += OnModeAllClick;
			break;
		case 8:
			FilterSortPanel = (StackPanel)target;
			break;
		case 9:
			FilterAll = (RadioButton)target;
			FilterAll.Checked += OnFilterChanged;
			break;
		case 10:
			FilterPinned = (RadioButton)target;
			FilterPinned.Checked += OnFilterChanged;
			break;
		case 11:
			FilterStartMenu = (RadioButton)target;
			FilterStartMenu.Checked += OnFilterChanged;
			break;
		case 12:
			FilterInstalled = (RadioButton)target;
			FilterInstalled.Checked += OnFilterChanged;
			break;
		case 13:
			SortAlpha = (RadioButton)target;
			SortAlpha.Checked += OnSortAlphaClick;
			break;
		case 14:
			SortGroup = (RadioButton)target;
			SortGroup.Checked += OnSortGroupClick;
			break;
		case 15:
			SortFavorites = (RadioButton)target;
			SortFavorites.Checked += OnShowFavoritesClick;
			break;
		case 16:
			NewGroupButton = (Button)target;
			NewGroupButton.Click += OnNewGroupClick;
			break;
		case 17:
			BatchOrderButton = (Button)target;
			BatchOrderButton.Click += OnBatchOrderClick;
			break;
		case 18:
			ListScroller = (ScrollViewer)target;
			break;
		case 19:
			ContentPanel = (StackPanel)target;
			break;
		case 20:
			FavoritesScroller = (ScrollViewer)target;
			break;
		case 21:
			FavoritesPanel = (StackPanel)target;
			break;
		case 22:
			BusyOverlay = (Grid)target;
			break;
		case 23:
			BusySpinner = (TextBlock)target;
			break;
		case 24:
			BusyText = (TextBlock)target;
			break;
		case 25:
			((Button)target).Click += OnAddFileClick;
			break;
		case 26:
			((Button)target).Click += OnCloseClick;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
