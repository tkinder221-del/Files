// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.AppLifecycle;
using System.Runtime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using WinRT;

namespace Files.App
{
	/// <summary>
	/// Represents the entry point of UI for Files app.
	/// </summary>
	public partial class App : Application
	{
		public static SystemTrayIcon? SystemTrayIcon { get; private set; }

		public static TaskCompletionSource? SplashScreenLoadingTCS { get; private set; }
		public static string? OutputPath { get; set; }

		private static FlyoutBase? _LastOpenedFlyout;
		public static FlyoutBase? LastOpenedFlyout
		{
			set
			{
				_LastOpenedFlyout = value;

				if (_LastOpenedFlyout is not null)
					_LastOpenedFlyout.Closed += LastOpenedFlyout_Closed;
			}
		}

		// TODO: Replace with DI
		public static QuickAccessManager QuickAccessManager { get; private set; } = null!;
		public static StorageHistoryWrapper HistoryWrapper { get; private set; } = null!;
		public static FileTagsManager FileTagsManager { get; private set; } = null!;
		public static LibraryManager LibraryManager { get; private set; } = null!;
		public static AppModel AppModel { get; private set; } = null!;
		public static ILogger Logger { get; private set; } = NullLogger.Instance;

		public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; private set; }

		/// <summary>
		/// Initializes an instance of <see cref="App"/>.
		/// </summary>
		public App()
		{
			InitializeComponent();

			// Configure exception handlers
			AppLifecycleHelper.RecordFirstChanceExceptions();
			UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception, true, "Application.UnhandledException", e.Message);
			AppDomain.CurrentDomain.UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.ExceptionObject as Exception, false, "AppDomain.UnhandledException");
			TaskScheduler.UnobservedTaskException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception, false, "TaskScheduler.UnobservedTaskException");
			AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
				SafetyExtensions.IgnoreExceptions(() => Ioc.Default.GetService<FileLoggerProvider>()?.TryCompleteAndFlush(TimeSpan.FromSeconds(2)));
		}

		/// <summary>
		/// Gets invoked when the application is launched normally by the end user.
		/// </summary>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			UiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

			// Constructed on the UI thread: the ctor subscribes the UI-thread-only Clipboard.ContentChanged
			AppModel = new AppModel();

			_ = ActivateAsync();

			async Task ActivateAsync()
			{
				// Build the DI container off-thread while the window initializes
				var appModel = AppModel;
				var servicesTask = Task.Run(() =>
				{
					try
					{
						var provider = AppLifecycleHelper.ConfigureHost(appModel);

						// Configure Ioc here so Ioc.Default-dependent constructions warm off-thread too
						Ioc.Default.ConfigureServices(provider);

						// Warm the settings file reads off the UI thread
						_ = provider.GetRequiredService<IGeneralSettingsService>().LeaveAppRunning;
						_ = provider.GetRequiredService<IAppearanceSettingsService>().AppThemeBackdropMaterial;
						InitializeCompatibilityServices(provider);
						WarmCommandManagerInBackground(provider);

						return provider;
					}
					catch (Exception)
					{
						// A UI-thread-only service ctor failed off-thread; rebuilt on the UI thread below
						return null;
					}
				});

				// Get AppActivationArguments
				var appActivationArguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
				var isStartupTask = appActivationArguments.Data is Windows.ApplicationModel.Activation.IStartupTaskActivatedEventArgs;

				// IsDynamicCodeSupported is false on Native AOT, where startup is fast enough to skip the splash screen
				var showSplashScreen = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

				if (!isStartupTask)
				{
					// Initialize and activate MainWindow
					MainWindow.Instance.Activate();

					if (showSplashScreen)
					{
						// Wait for the Window to initialize
						await Task.Delay(10);

						SplashScreenLoadingTCS = new TaskCompletionSource();
						MainWindow.Instance.ShowSplashScreen();
					}
				}

				// Configure the DI (dependency injection) container
				var serviceProvider = await servicesTask;
				if (serviceProvider is null)
				{
					serviceProvider = AppLifecycleHelper.ConfigureHost(appModel);
					Ioc.Default.ConfigureServices(serviceProvider);
					InitializeCompatibilityServices(serviceProvider);
				}

				// Configure Sentry after the service barrier so its transport and sanitization
				// setup cannot compete with the first page on the UI thread.
				if (AppLifecycleHelper.AppEnvironment is not AppEnvironment.Dev)
				{
					_ = Task.Run(() =>
					{
						try
						{
							AppLifecycleHelper.ConfigureSentry();
							ActiveSessionTracker.ReportPersistedTime();
						}
						catch (Exception ex)
						{
							System.Diagnostics.Debug.WriteLine($"Sentry initialization failed: {ex}");
						}
					});
				}

				var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
				var isLeaveAppRunning = userSettingsService.GeneralSettingsService.LeaveAppRunning;

				if (isStartupTask && !isLeaveAppRunning)
				{
					// Initialize and activate MainWindow
					MainWindow.Instance.Activate();

					if (showSplashScreen)
					{
						// Wait for the Window to initialize
						await Task.Delay(10);

						SplashScreenLoadingTCS = new TaskCompletionSource();
						MainWindow.Instance.ShowSplashScreen();
					}
				}

				// TODO: Replace with DI
				Logger = Ioc.Default.GetRequiredService<ILogger<App>>();
				AppModel = Ioc.Default.GetRequiredService<AppModel>();

			// Hook events for the window
			MainWindow.Instance.Closed += Window_Closed;
			MainWindow.Instance.AppWindow.Closing += AppWindow_Closing;
			MainWindow.Instance.Activated += Window_Activated;

				Logger.LogInformation($"App launched. Launch args type: {appActivationArguments.Data.GetType().Name}");

				if (!(isStartupTask && isLeaveAppRunning))
				{
					if (SplashScreenLoadingTCS is not null)
					{
						// Wait for the UI to update
						await SplashScreenLoadingTCS.Task.WithTimeoutAsync(TimeSpan.FromMilliseconds(500));
						SplashScreenLoadingTCS = null;
					}

					// Deferred so the first frame renders first
					MainWindow.Instance.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
					{
						SystemTrayIcon = new SystemTrayIcon();
						if (userSettingsService.GeneralSettingsService.ShowSystemTrayIcon)
							SystemTrayIcon.Show();
					});

					_ = MainWindow.Instance.InitializeApplicationAsync(appActivationArguments.Data);
				}
					else
					{
						// Create a system tray icon
						SystemTrayIcon = new SystemTrayIcon();
						if (userSettingsService.GeneralSettingsService.ShowSystemTrayIcon)
							SystemTrayIcon.Show();

						// Sleep current instance
						Program.Pool = new(0, 1, $"Files-{AppLifecycleHelper.AppEnvironment}-Instance");

						Thread.Yield();

						var cts = new CancellationTokenSource();
						TryEmptyWorkingSetWhenIdle(cts.Token);

						try
						{
							// Wait off-thread so the UI dispatcher stays responsive
							await Task.Run(() => Program.Pool?.WaitOne());
						}
						finally
						{
							cts.Cancel();
						}

						// Resume the instance
						Program.Pool?.Dispose();
						Program.Pool = null;
					}

				await AppLifecycleHelper.InitializeAppComponentsAsync();
			}
		}

		private static void InitializeCompatibilityServices(IServiceProvider serviceProvider)
		{
			QuickAccessManager = serviceProvider.GetRequiredService<QuickAccessManager>();
			HistoryWrapper = serviceProvider.GetRequiredService<StorageHistoryWrapper>();
			FileTagsManager = serviceProvider.GetRequiredService<FileTagsManager>();
			LibraryManager = serviceProvider.GetRequiredService<LibraryManager>();
		}

		private static void WarmCommandManagerInBackground(IServiceProvider serviceProvider)
		{
			_ = Task.Run(() =>
			{
				var previousPriority = Thread.CurrentThread.Priority;
				Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
				try
				{
					_ = serviceProvider.GetRequiredService<ICommandManager>();
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"Command manager warm-up failed: {ex}");
				}
				finally
				{
					Thread.CurrentThread.Priority = previousPriority;
				}
			});
		}

		/// <summary>
		/// Gets invoked when the application is activated.
		/// </summary>
		public async Task OnActivatedAsync(AppActivationArguments activatedEventArgs)
		{
			var activatedEventArgsData = activatedEventArgs.Data;

			Logger.LogInformation($"The app is being activated. Activation type: {activatedEventArgsData?.GetType().Name ?? "Unknown"}");

			// InitializeApplication accesses UI, needs to be called on UI thread
			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(()
				=> MainWindow.Instance.InitializeApplicationAsync(activatedEventArgsData));
		}

		/// <summary>
		/// Gets invoked when the main window is activated.
		/// </summary>
		private void Window_Activated(object sender, WindowActivatedEventArgs args)
		{
			Logger.LogInformation($"Window_Activated: State={args.WindowActivationState}");

			ActiveSessionTracker.OnActivationChanged(args.WindowActivationState != WindowActivationState.Deactivated);

			if (args.WindowActivationState != WindowActivationState.Deactivated)
				AppModel.IsMainWindowClosed = false;

			// TODO(s): Is this code still needed?
			if (args.WindowActivationState != WindowActivationState.CodeActivated ||
				args.WindowActivationState != WindowActivationState.PointerActivated)
				return;

			ApplicationData.Current.LocalSettings.Values["INSTANCE_ACTIVE"] = -Environment.ProcessId;
		}

		/// <summary>
		/// Gets invoked when the main window is being closed. Cancelable - setting
		/// <see cref="AppWindowClosingEventArgs.Cancel"/> keeps the window alive in
		/// the background instead of tearing the process down.
		/// </summary>
		private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
		{
			var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();

			bool stayInBackground = userSettingsService.GeneralSettingsService.LeaveAppRunning
				&& !AppModel.ForceProcessTermination
				&& !Process.GetProcessesByName("Files").Any(x => x.Id != Environment.ProcessId);

			if (!stayInBackground)
				return;

			// Cancel the close so the window stays alive while cached in the background
			args.Cancel = true;

			// Hide and cache the window on the UI thread, then wait asynchronously for a resume signal
			_ = MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(CacheWindowAndWaitForResumeAsync);
		}

		/// <summary>
		/// Caches the window to the background and sleeps the process until a resume
		/// signal is received (tray click, single-instance redirect, or Quit).
		/// </summary>
		private async Task CacheWindowAndWaitForResumeAsync()
		{
			var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			var statusCenterViewModel = Ioc.Default.GetRequiredService<StatusCenterViewModel>();

			// Close open content dialogs
			UIHelpers.CloseAllDialogs();

			// Close all notification banners except in progress
			statusCenterViewModel.RemoveAllCompletedItems();

			// Cache the window instead of closing it
			MainWindow.Instance.AppWindow.Hide();
			AppModel.IsMainWindowClosed = true;

			// Close all tabs
			MainPageViewModel.AppInstances.ForEach(tabItem => tabItem.Unload());
			MainPageViewModel.AppInstances.Clear();

			// Wait for all properties windows to close
			await FilePropertiesHelpers.WaitClosingAll();

			// Sleep current instance
			Program.Pool = new(0, 1, $"Files-{AppLifecycleHelper.AppEnvironment}-Instance");

			// Displays a notification the first time the app goes to the background
			if (userSettingsService.AppSettingsService.ShowBackgroundRunningNotification)
			{
				SafetyExtensions.IgnoreExceptions(() =>
				{
					AppToastNotificationHelper.ShowBackgroundRunningToast();

					userSettingsService.AppSettingsService.ShowBackgroundRunningNotification = false;
				});
			}

			var cts = new CancellationTokenSource();
			TryEmptyWorkingSetWhenIdle(cts.Token);

			try
			{
				// Wait off-thread so the UI dispatcher stays responsive
				await Task.Run(() => Program.Pool?.WaitOne());
			}
			finally
			{
				cts.Cancel();
			}

			// Resume the instance
			Program.Pool?.Dispose();
			Program.Pool = null;

			if (AppModel.ForceProcessTermination)
				return;

			_ = AppLifecycleHelper.CheckAppUpdate();

			MainWindow.Instance.AppWindow.Show();
			MainWindow.Instance.Activate();
		}

		/// <summary>
		/// Gets invoked when the application execution is closed.
		/// </summary>
		/// <remarks>
		/// Performs final teardown when the window is actually closing (tray Quit,
		/// update service termination, or background-running disabled). The
		/// background-running path is handled by <see cref="AppWindow_Closing"/>.
		/// </remarks>
		private async void Window_Closed(object sender, WindowEventArgs args)
		{
			// Save application state and stop any background activity
			IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			ICommandManager commandManager = Ioc.Default.GetRequiredService<ICommandManager>();

			// A Workaround for the crash (#10110)
			if (_LastOpenedFlyout?.IsOpen ?? false)
			{
				args.Handled = true;
				_LastOpenedFlyout.Closed += (sender, e) => App.Current.Exit();
				_LastOpenedFlyout.Hide();
				return;
			}

			// Persist the final active stretch; it is reported on the next launch
			ActiveSessionTracker.OnActivationChanged(false);

			// Save the current tab list in case it was overwriten by another instance
			if (userSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp || userSettingsService.AppSettingsService.RestoreTabsOnStartup)
				AppLifecycleHelper.SaveSessionTabs();
			else
				await commandManager.CloseAllTabs.ExecuteAsync();

			if (OutputPath is not null)
			{
				var instance = MainPageViewModel.AppInstances.FirstOrDefault(x =>
					(x.TabItemContent ?? throw new InvalidOperationException("A tab does not have content.")).IsCurrentInstance);
				if (instance is null)
					return;

				var items = (instance.TabItemContent as ShellPanesPage)?.ActivePane?.SlimContentPage?.SelectedItems;
				if (items is null)
					return;

				var results = items.Select(x => x.ItemPath!).ToList();
				System.IO.File.WriteAllLines(OutputPath, results);

				using var eventHandle = PInvoke.CreateEvent(null, false, false, "FILEDIALOG");
				PInvoke.SetEvent(eventHandle);
			}

			// Stop the tray icon's hidden window before continuing teardown so a late "Quit"
			// click can't dispatch into OnQuitClicked once Application.Current is null.
			SystemTrayIcon?.Dispose();
			SystemTrayIcon = null;

			// Method can take a long time, make sure the window is hidden
			await Task.Yield();

			// Try to maintain clipboard data after app close
			SafetyExtensions.IgnoreExceptions(() =>
			{
				var dataPackage = Clipboard.GetContent();
				if (dataPackage.Properties.PackageFamilyName == Package.Current.Id.FamilyName)
				{
					if (dataPackage.Contains(StandardDataFormats.StorageItems))
						Clipboard.Flush();
				}
			},
			Logger);

			// Destroy cached properties windows
			FilePropertiesHelpers.DestroyCachedWindows();
			AppModel.IsMainWindowClosed = true;

			// Wait for ongoing file operations
			FileOperationsHelpers.WaitForCompletion();
		}

		private static void TryEmptyWorkingSetWhenIdle(CancellationToken cancellationToken)
		{
			static void AggressiveGC(Windows.Win32.Foundation.HANDLE processHandle, CancellationToken cancellationToken)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
				GC.WaitForPendingFinalizers();
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
				Thread.Sleep(1000);

				if (cancellationToken.IsCancellationRequested)
					return;

				PInvoke.K32EmptyWorkingSet(processHandle);
			}

			new Thread(() =>
			{
				using var process = Process.GetCurrentProcess();
				var processHandle = new Windows.Win32.Foundation.HANDLE(process.Handle);

				// Try to empty the working set
				AggressiveGC(processHandle, cancellationToken);

				if (cancellationToken.IsCancellationRequested)
					return;

				FileOperationsHelpers.WaitForCompletion();
				if (cancellationToken.IsCancellationRequested)
					return;

				// After all pending file operations are completed, try to empty the working set again
				AggressiveGC(processHandle, cancellationToken);
			})
			{ IsBackground = true }.Start();
		}

		/// <summary>
		/// Gets invoked when the last opened flyout is closed.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(FlyoutBase))]
		private static void LastOpenedFlyout_Closed(object? sender, object e)
		{
			if (sender is not FlyoutBase flyoutBase)
				return;

			flyoutBase.Closed -= LastOpenedFlyout_Closed;
			if (_LastOpenedFlyout == flyoutBase)
				_LastOpenedFlyout = null;
		}
	}
}
