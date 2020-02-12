//
//  MainWindow.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Diagnostics;
using System.Timers;
using Gdk;
using GLib;
using Gtk;

using Launchpad.Common;
using Launchpad.Launcher.Configuration;
using Launchpad.Launcher.Handlers;
using Launchpad.Launcher.Handlers.Protocols;
using Launchpad.Launcher.Services;
using Launchpad.Launcher.Utility;
using Launchpad.Launcher.Utility.Enums;

using NGettext;
using NLog;
using SixLabors.ImageSharp;
using Application = Gtk.Application;
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;

namespace Launchpad.Launcher.Interface
{
	/// <summary>
	/// The main UI class for Launchpad. This class acts as a manager for all threaded
	/// actions, such as installing, updating or repairing the game.
	/// </summary>
	public sealed partial class MainWindow : Gtk.Window
	{
		/// <summary>
		/// Logger instance for this class.
		/// </summary>
		private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

		private readonly LocalVersionService LocalVersionService = new LocalVersionService();

		/// <summary>
		/// The configuration instance reference.
		/// </summary>
		private readonly ILaunchpadConfiguration Configuration = ConfigHandler.Instance.Configuration;

		/// <summary>
		/// The checks handler reference.
		/// </summary>
		private readonly ChecksHandler Checks = new ChecksHandler();

		/// <summary>
		/// The launcher handler. Allows updating the launcher and loading the changelog.
		/// </summary>
		private readonly LauncherHandler Launcher = new LauncherHandler();

		/// <summary>
		/// The game handler. Allows updating, installing and repairing the game.
		/// </summary>
		private readonly GameHandler Game = new GameHandler();

		private readonly TagfileService TagfileService = new TagfileService();

		/// <summary>
		/// The current mode that the launcher is in. Determines what the primary button does when pressed.
		/// </summary>
		private ELauncherMode Mode = ELauncherMode.Inactive;

		/// <summary>
		/// The localization catalog.
		/// </summary>
		private static readonly ICatalog LocalizationCatalog = new Catalog("Launchpad", "./Content/locale");

		/// <summary>
		/// Whether or not the launcher UI has been initialized.
		/// </summary>
		private bool IsInitialized;

		/// <summary>
		/// Whether or not we should launch the game after downloading.
		/// </summary>
		private bool ShouldLaunchGame;

		/// <summary>
		/// Initializes a new instance of the <see cref="MainWindow"/> class.
		/// </summary>
		/// <param name="builder">The UI builder.</param>
		/// <param name="handle">The native handle of the window.</param>
		private MainWindow(Builder builder, IntPtr handle)
			: base(handle)
		{
			builder.Autoconnect(this);

			BindUIEvents();

			// Bind the handler events
			this.Game.ProgressChanged += OnModuleInstallationProgressChanged;
			this.Game.DownloadFinished += OnGameDownloadFinished;
			this.Game.DownloadFailed += OnGameDownloadFailed;
			this.Game.LaunchFailed += OnGameLaunchFailed;
			this.Game.GameExited += OnGameExited;

			this.Launcher.LauncherDownloadProgressChanged += OnModuleInstallationProgressChanged;
			this.Launcher.LauncherDownloadFinished += OnLauncherDownloadFinished;

			// Set the initial launcher mode
			SetLauncherMode(ELauncherMode.Inactive, false);

			// Set the window title
			this.Title = LocalizationCatalog.GetString("Launchpad");
		}

		/// <summary>
		/// Initializes the UI of the launcher, performing varying checks against the patching server.
		/// </summary>
		/// <returns>A task that must be awaited.</returns>
		public Task InitializeAsync()
		{
			if (this.IsInitialized)
			{
				return Task.CompletedTask;
			}

			this.StatusLabel.Text = LocalizationCatalog.GetString("Checking for updates...");

			// First of all, check if we can connect to the patching service.
			if (!this.Checks.CanPatch() || !this.Checks.IsPlatformAvailable(this.Configuration.SystemTarget))
			{
				using (var dialog = new MessageDialog
				(
					this,
					DialogFlags.Modal,
					MessageType.Warning,
					ButtonsType.Ok,
					LocalizationCatalog.GetString("Failed to connect to the update server.")
				))
				{
					dialog.Run();
				}

				this.StatusLabel.Text = LocalizationCatalog.GetString("Could not connect to server.");
			}
			else
			{
				LoadBanner();
				this.IsInitialized = true;

				// If we can connect, proceed with the rest of our checks.
				if (ChecksHandler.IsInitialStartup() && !this.Checks.IsGameInstalled())
				{
					Log.Info("This instance is the first start of the application in this folder.");
					this.TagfileService.CreateLauncherTagfile();
					this.ShouldLaunchGame = true;
					SetLauncherMode(ELauncherMode.Install, false);
					ExecuteMainAction();

					return Task.CompletedTask;
				}

				if (this.Checks.IsLauncherOutdated())
				{
					// The launcher was outdated.
					Log.Info($"The launcher is outdated. \n\tLocal version: {this.LocalVersionService.GetLocalLauncherVersion()}");
					SetLauncherMode(ELauncherMode.Update, false);
					ExecuteMainAction();

					return Task.CompletedTask;
				}

				if (this.Checks.IsGameOutdated())
				{
					// If it does, offer to update it
					Log.Info("The game is outdated.");
					this.ShouldLaunchGame = true;
					SetLauncherMode(ELauncherMode.Update, false);
					ExecuteMainAction();

					return Task.CompletedTask;
				}

				// All checks passed, so we can simply launch the game.
				Log.Info("All checks passed. Game can be launched.");
				SetLauncherMode(ELauncherMode.Launch, false);
				ExecuteMainAction();
			}

			this.IsInitialized = true;
			return Task.CompletedTask;
		}

		private void LoadBanner()
		{
			var patchHandler = PatchProtocolProvider.GetHandler();

			// Load the game banner (if there is one)
			if (!patchHandler.CanProvideBanner())
			{
				return;
			}

			Task.Factory.StartNew
			(
				() =>
				{
					// Fetch the banner from the server
					var bannerImage = patchHandler.GetBanner();

					bannerImage.Mutate(i => i.Resize(this.BannerImage.AllocatedWidth, 0));

					// Load the image into a pixel buffer
					return new Pixbuf
					(
						Bytes.NewStatic(bannerImage.SavePixelData()),
						Colorspace.Rgb,
						true,
						8,
						bannerImage.Width,
						bannerImage.Height,
						4 * bannerImage.Width
					);
				}
			)
			.ContinueWith
			(
				async bannerTask => this.BannerImage.Pixbuf = await bannerTask
			);
		}

		/// <summary>
		/// Sets the launcher mode and updates UI elements to match.
		/// </summary>
		/// <param name="newMode">The new mode.</param>
		/// <param name="isInProgress">If set to <c>true</c>, the selected mode is in progress.</param>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="ELauncherMode"/> passed to the function is not a valid value.
		/// </exception>
		private void SetLauncherMode(ELauncherMode newMode, bool isInProgress)
		{
			// Set the global launcher mode
			this.Mode = newMode;

			switch (newMode)
			{
				case ELauncherMode.Install:
				{
					this.StatusLabel.Text = isInProgress ? LocalizationCatalog.GetString("Installing...") : string.Empty;
					break;
				}
				case ELauncherMode.Update:
				{
					this.StatusLabel.Text = isInProgress ? LocalizationCatalog.GetString("Updating...") : string.Empty;
					break;
				}
				case ELauncherMode.Repair:
				{
					this.StatusLabel.Text = isInProgress ? LocalizationCatalog.GetString("Repairing...") : string.Empty;
					break;
				}
				case ELauncherMode.Launch:
				{
					this.StatusLabel.Text = isInProgress ? LocalizationCatalog.GetString("Launching...") : string.Empty;
					break;
				}
				default:
				{
					this.StatusLabel.Text = string.Empty;
					break;
				}
			}
		}

		/// <summary>
		/// Executes the main action depending on state.
		/// </summary>
		private void ExecuteMainAction()
		{
			// Drop out if the current platform isn't available on the server
			if (!this.Checks.IsPlatformAvailable(this.Configuration.SystemTarget))
			{
				Log.Info
				(
					$"The server does not provide files for platform \"{PlatformHelpers.GetCurrentPlatform()}\". " +
					"A .provides file must be present in the platforms' root directory."
				);

				SetLauncherMode(ELauncherMode.Inactive, false);

				return;
			}

			// else, run the relevant function
			switch (this.Mode)
			{
				case ELauncherMode.Repair:
				{
					// Repair the game asynchronously
					SetLauncherMode(ELauncherMode.Repair, true);
					this.Game.VerifyGame();

					break;
				}
				case ELauncherMode.Install:
				{
					// Install the game asynchronously
					SetLauncherMode(ELauncherMode.Install, true);
					this.Game.InstallGame();

					break;
				}
				case ELauncherMode.Update:
				{
					if (this.Checks.IsLauncherOutdated())
					{
						// Update the launcher asynchronously
						SetLauncherMode(ELauncherMode.Update, true);
						this.Launcher.UpdateLauncher();
					}
					else
					{
						// Update the game asynchronously
						SetLauncherMode(ELauncherMode.Update, true);
						this.Game.UpdateGame();
					}

					break;
				}
				case ELauncherMode.Launch:
				{
					SetLauncherMode(ELauncherMode.Launch, true);

					Timer launchDelay = new Timer(1000);
					launchDelay.Elapsed += LaunchEvent;
					launchDelay.Start();
					break;
				}
				default:
				{
					Log.Warn("Trying to execute an action in an invalid active mode.");
					break;
				}
			}
		}

		/// <summary>
		/// Launches the game.
		/// </summary>
		private void LaunchEvent(object sender, ElapsedEventArgs e)
		{
			this.Game.LaunchGame();
			Application.Quit();
		}

		/// <summary>
		/// Starts the launcher update process when its files have finished downloading.
		/// </summary>
		private static void OnLauncherDownloadFinished(object sender, EventArgs e)
		{
			Application.Invoke((o, args) =>
			{
				ProcessStartInfo script = LauncherHandler.CreateUpdateScript();
				Process.Start(script);

				Application.Quit();
			});
		}

		/// <summary>
		/// Warns the user when the game fails to launch, and offers to attempt a repair.
		/// </summary>
		/// <param name="sender">The sender.</param>
		/// <param name="e">Empty event args.</param>
		private void OnGameLaunchFailed(object sender, EventArgs e)
		{
			Application.Invoke((o, args) =>
			{
				this.StatusLabel.Text = LocalizationCatalog.GetString("The application failed to launch. Trying to repair...");

				SetLauncherMode(ELauncherMode.Repair, false);
				ExecuteMainAction();
			});
		}

		/// <summary>
		/// Provides alternatives when the game fails to download, either through an update or through an installation.
		/// </summary>
		/// <param name="sender">The sender.</param>
		/// <param name="e">Contains the type of failure that occurred.</param>
		private void OnGameDownloadFailed(object sender, EventArgs e)
		{
			Application.Invoke((o, args) =>
			{
				switch (this.Mode)
				{
					case ELauncherMode.Install:
					case ELauncherMode.Update:
					case ELauncherMode.Repair:
					{
						// Set the mode to the same as it was, but no longer in progress.
						// The modes which fall to this case are all capable of repairing an incomplete or
						// broken install on their own.
						SetLauncherMode(this.Mode, false);
						break;
					}
					default:
					{
						// Other cases (such as Launch) will go to the default mode of Repair.
						SetLauncherMode(ELauncherMode.Repair, false);
						break;
					}
				}
			});
		}

		/// <summary>
		/// Updates the progress bar and progress label during installations, repairs and updates.
		/// </summary>
		/// <param name="sender">The sender.</param>
		/// <param name="e">Contains the progress values and current filename.</param>
		private void OnModuleInstallationProgressChanged(object sender, ModuleProgressChangedArgs e)
		{
			Application.Invoke((o, args) =>
			{
				this.MainProgressBar.Fraction = e.ProgressFraction;
			});
		}

		/// <summary>
		/// Allows the user to launch or repair the game once installation finishes.
		/// </summary>
		/// <param name="sender">The sender.</param>
		/// <param name="e">Contains the result of the download.</param>
		private void OnGameDownloadFinished(object sender, EventArgs e)
		{
			Application.Invoke((o, args) =>
			{
				switch (this.Mode)
				{
					case ELauncherMode.Install:
					{
						this.StatusLabel.Text = LocalizationCatalog.GetString("Installation finished");
						break;
					}
					case ELauncherMode.Update:
					{
						this.StatusLabel.Text = LocalizationCatalog.GetString("Update finished");
						break;
					}
					case ELauncherMode.Repair:
					{
						this.StatusLabel.Text = LocalizationCatalog.GetString("Repair finished");
						break;
					}
					default:
					{
						this.StatusLabel.Text = string.Empty;
						break;
					}
				}

				if (this.ShouldLaunchGame == true)
				{
					SetLauncherMode(ELauncherMode.Launch, false);
					ExecuteMainAction();
				}
			});
		}

		/// <summary>
		/// Handles offering of repairing the game to the user should the game exit
		/// with a bad exit code.
		/// </summary>
		private void OnGameExited(object sender, int exitCode)
		{
			Application.Invoke((o, args) =>
			{
				if (exitCode != 0)
				{
					using (var crashDialog = new MessageDialog
					(
						this,
						DialogFlags.Modal,
						MessageType.Question,
						ButtonsType.YesNo,
						LocalizationCatalog.GetString
						(
							"Whoops! The game appears to have crashed.\n" +
							"Would you like the launcher to verify the installation?"
						)
					))
					{
						if (crashDialog.Run() == (int)ResponseType.Yes)
						{
							SetLauncherMode(ELauncherMode.Repair, false);
						}
						else
						{
							SetLauncherMode(ELauncherMode.Launch, false);
						}
					}
				}
				else
				{
					SetLauncherMode(ELauncherMode.Launch, false);
				}
			});
		}

		/// <summary>
		/// Handles starting of a reinstallation procedure as requested by the user.
		/// </summary>
		private void OnReinstallGameActionActivated(object sender, EventArgs e)
		{
			using (var reinstallConfirmDialog = new MessageDialog
			(
				this,
				DialogFlags.Modal,
				MessageType.Question,
				ButtonsType.YesNo,
				LocalizationCatalog.GetString
				(
					"Reinstalling the game will delete all local files and download the entire game again.\n" +
					"Are you sure you want to reinstall the game?"
				)
			))
			{
				if (reinstallConfirmDialog.Run() == (int)ResponseType.Yes)
				{
					SetLauncherMode(ELauncherMode.Install, true);
					this.Game.ReinstallGame();
				}
			}
		}
	}
}
