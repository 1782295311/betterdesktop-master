using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Core.Animation;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Dock.Contracts;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Dock;

public sealed class DockPlugin : IPlugin
{
	private Window? _dockWindow;

	private Window? _grabberWindow;

	private NewAppsNotificationWindow? _newAppsNotification;

	private DispatcherTimer? _newAppsTimer;

	private IAppSourceService _appSourceService = null;

	private IAppIconService _appIconService = null;

	private IDockAppsService _dockAppsService = null;

	private IDockIconService _dockIconService = null;

	private IDockPinnedService _dockPinnedService = null;

	private IVibrancyService? _vibrancy;

	private IAppearanceService? _appearance;

	public string Name => "shell.dock";

	public IReadOnlyList<Type> Inject => new Type[3]
	{
		typeof(IVibrancyService),
		typeof(IAppSourceService),
		typeof(IAppIconService)
	};

	public DockPlugin()
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string text = Path.Combine(folderPath, "BetterDesktop", "dock-pinned.json");
	}

	public Task LoadAsync(IContext context, CancellationToken cancellationToken = default(CancellationToken))
	{
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0132: Expected O, but got Unknown
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Expected O, but got Unknown
		ISettingsService val = context.Get<ISettingsService>();
		if (val != null && !val.Get<bool>("components.dock", true))
		{
			return Task.CompletedTask;
		}
		try
		{
			_vibrancy = context.Get<IVibrancyService>();
			_appSourceService = context.Get<IAppSourceService>();
			_appIconService = context.Get<IAppIconService>();
			_appearance = context.Get<IAppearanceService>();
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			string storagePath = Path.Combine(folderPath, "BetterDesktop", "dock-pinned.json");
			_dockAppsService = new DockAppsService(_appSourceService, storagePath);
			DockVisualSettings dockVisualSettings = new DockVisualSettings(val);
			_dockIconService = new DockIconService(_appIconService, dockVisualSettings);
			_dockPinnedService = new DockPinnedService(_appSourceService, storagePath);
			context.Provide<IDockAppsService>(_dockAppsService);
			context.Provide<IDockIconService>(_dockIconService);
			context.Provide<IDockPinnedService>(_dockPinnedService);
			context.Provide<DockVisualSettings>(dockVisualSettings);
			_dockWindow = (Window?)(object)new DockWindow(_vibrancy, (IAnimationService)new AnimationService(), new DockService(), _dockAppsService, _dockIconService, new DockLayoutService(4.0, dockVisualSettings.BottomMargin, val), this, _appIconService, val, _appearance, dockVisualSettings);
			_dockWindow.Show();
			ShowAppGrabber();
			Task.Run(delegate
			{
				CheckNewApps();
			});
			_newAppsTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(60.0)
			};
			_newAppsTimer.Tick += delegate
			{
				CheckNewApps();
			};
			_newAppsTimer.Start();
			return Task.CompletedTask;
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException("DockPlugin.LoadAsync 失败: " + ex.Message, ex);
		}
	}

	public void ShowAppGrabber()
	{
		if (_grabberWindow != null)
		{
			_grabberWindow.Activate();
			return;
		}
		_grabberWindow = (Window?)(object)new AppGrabberWindow(_vibrancy, _dockAppsService, _dockIconService, _appearance, NotifyNewApps);
		_grabberWindow.Closed += delegate
		{
			_grabberWindow = null;
		};
		_grabberWindow.Show();
	}

	private void CheckNewApps()
	{
		try
		{
			IReadOnlyList<DockItemData> newlyInstalledApps = _dockAppsService.GetNewlyInstalledApps();
			if (newlyInstalledApps.Count != 0)
			{
				NotifyNewApps(newlyInstalledApps);
			}
		}
		catch
		{
		}
	}

	public void NotifyNewApps(IReadOnlyList<DockItemData> fresh)
	{
		if (fresh == null || fresh.Count == 0 || _newAppsNotification != null)
		{
			return;
		}
		try
		{
			_newAppsNotification = new NewAppsNotificationWindow(_vibrancy, _dockAppsService, _dockIconService, fresh, _appearance);
			((Window)(object)_newAppsNotification).Closed += delegate
			{
				_newAppsNotification = null;
			};
			((Window)(object)_newAppsNotification).Show();
		}
		catch
		{
		}
	}

	public Task UnloadAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		DispatcherTimer? newAppsTimer = _newAppsTimer;
		if (newAppsTimer != null)
		{
			newAppsTimer.Stop();
		}
		_newAppsTimer = null;
		((Window)(object)_newAppsNotification)?.Close();
		_newAppsNotification = null;
		_grabberWindow?.Close();
		_grabberWindow = null;
		_dockWindow?.Close();
		_dockWindow = null;
		return Task.CompletedTask;
	}
}
