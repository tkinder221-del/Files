
namespace Files.App.Services
{
	internal sealed partial class DummyUpdateService : ObservableObject, IUpdateService
	{
		public bool IsUpdateAvailable => false;

		public bool IsUpdating => false;

		public int UpdateProgress => 0;

		public bool IsAppUpdated => AppLifecycleHelper.IsAppUpdated;

		private bool _areReleaseNotesAvailable = false;
		public bool AreReleaseNotesAvailable
		{
			get => _areReleaseNotesAvailable;
			private set => SetProperty(ref _areReleaseNotesAvailable, value);
		}

		public new event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

		public Task CheckAndUpdateFilesLauncherAsync()
		{
			return Task.CompletedTask;
		}

		public Task CheckForUpdatesAsync()
		{
			return Task.CompletedTask;
		}

		public Task CheckForReleaseNotesAsync()
		{
			// The development implementation has no update channel. Avoid a network request
			// during startup when release notes cannot be opened by this service.
			AreReleaseNotesAvailable = false;
			return Task.CompletedTask;
		}

		public Task DownloadMandatoryUpdatesAsync()
		{
			return Task.CompletedTask;
		}

		public Task DownloadUpdatesAsync()
		{
			return Task.CompletedTask;
		}
	}
}
