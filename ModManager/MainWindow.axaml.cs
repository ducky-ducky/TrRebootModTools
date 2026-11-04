using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MsBox.Avalonia.Enums;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TrRebootTools.ModManager.Mod;
using TrRebootTools.Shared;
using TrRebootTools.Shared.Cdc;
using TrRebootTools.Shared.Controls;
using TrRebootTools.Shared.Forms;
using TrRebootTools.Shared.Util;

namespace TrRebootTools.ModManager
{
    internal partial class MainWindow : WindowWithProgress
    {
        private readonly ArchiveSet _archiveSet;
        private readonly ResourceUsageCache _gameResourceUsages;
        private readonly ObservableCollection<InstalledMod> _installedMods = [];

        private enum ModSortColumn
        {
            Name,
            Priority
        }

        private ModSortColumn _sortColumn = ModSortColumn.Priority;
        private bool _sortAscending = true;

        private const string ConfirmToolTip = "Apply changes: set the priority numbers and enable or disable the mods as ticked";
        private bool _closeConfirmed;

        public MainWindow()
        {
            InitializeComponent();
        }

        public MainWindow(string gameFolderPath, CdcGame game)
            : this()
        {
            _archiveSet = ArchiveSet.Open(gameFolderPath, true, true, game);
            _gameResourceUsages = new ResourceUsageCache();

            CdcGameInfo gameInfo = CdcGameInfo.Get(game);
            Title = $"{gameInfo.ShortName} Mod Manager {TrRebootTools.ModManager.Util.AppVersion.Text}";
            LogFiles.StartSession(gameInfo.ShortName);
            _imgSelectGame.Source = gameInfo.Icon;
        }

        public bool GameSelectionRequested
        {
            get;
            private set;
        }

        public bool GameFolderChangeRequested
        {
            get;
            private set;
        }

        protected override async void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _lbMods.ItemsSource = _installedMods;
            if (Design.IsDesignMode)
            {
                _installedMods.Add(new(1, "Enabled mod", true));
                _installedMods.Add(new(2, "Disabled mod", false));
                return;
            }

            RefreshModList();

            string listFilePath = CdcHash.GetListFilePath(_archiveSet.Game);
            if (!File.Exists(listFilePath))
            {
                await MessageBox.ShowAsync(
                    "Missing file",
                    $"The name list for {CdcGameInfo.Get(_archiveSet.Game).ShortName} wasn't found:\n{listFilePath}\n\n" +
                    "Without it the manager can't recognise the game's files and installing mods will fail. " +
                    "Copy the .list files that come with the tools into the same folder as the program.",
                    icon: MsBox.Avalonia.Enums.Icon.Error
                );
            }

            await Task.Delay(1);

            if (!_gameResourceUsages.Load(_archiveSet.FolderPath))
            {
                // Without the name list the cache would be built with every file unrecognised and then saved, which would keep
                // the manager from working even after the list is put back. So nothing is built or saved until the list is there.
                if (!File.Exists(listFilePath))
                    return;

                using ArchiveSet gameArchiveSet = ArchiveSet.Open(_archiveSet.FolderPath, true, false, _archiveSet.Game);
                await Task.Run(() => _gameResourceUsages.AddArchiveSet(gameArchiveSet, this, CancellationToken));
                _gameResourceUsages.Save(_archiveSet.FolderPath);
            }

            if (_archiveSet.DuplicateArchives.Any(a => a.ModName != null))
                await ReinstallModsAsync(false);
        }

        private async void OnAddFromZipClicked(object? sender, RoutedEventArgs e)
        {
            string? filePath = await App.OpenFilePickerAsync(
                "Select mod archive to install",
                new()
                {
                    { "Archive", ["*.7z", "*.zip", "*.rar"] }
                }
            );
            if (filePath == null)
                return;

            await InstallModFromZipAsync(filePath);
        }

        private async Task InstallModFromZipAsync(string filePath)
        {
            if (!await ConfirmDiscardPendingChangesAsync("Installing a mod"))
                return;

            try
            {
                ModInstaller installer = new(_archiveSet, _gameResourceUsages, this);
                InstalledMod? mod = await Task.Run(
                    () => installer.InstallFromZipAsync(filePath, this, CancellationToken)
                );
                if (mod == null)
                    return;

                await UpdateFlatModArchiveAsync();
                LogFiles.LogEvent($"Finished installing {mod.Name}");
                RefreshModList();
            }
            catch (Exception ex)
            {
                await MessageBox.ShowErrorAsync(ex);
                RefreshModList();
            }
        }

        private async void OnAddFromFolderClicked(object? sender, RoutedEventArgs e)
        {
            string? folderPath = await App.OpenFolderPickerAsync("Select mod folder to install");
            if (folderPath == null)
                return;

            await InstallModFromFolderAsync(folderPath);
        }

        private async Task InstallModFromFolderAsync(string folderPath)
        {
            if (!await ConfirmDiscardPendingChangesAsync("Installing a mod"))
                return;

            try
            {
                ModInstaller installer = new(_archiveSet, _gameResourceUsages, this);
                InstalledMod? mod = await Task.Run(
                    () => installer.InstallFromFolderAsync(folderPath, this, CancellationToken)
                );
                if (mod == null)
                    return;

                await UpdateFlatModArchiveAsync();
                LogFiles.LogEvent($"Finished installing {mod.Name}");
            }
            catch (Exception ex)
            {
                await MessageBox.ShowErrorAsync(ex);
            }
            finally
            {
                RefreshModList();
            }
        }

        private async void OnRemoveClicked(object? sender, RoutedEventArgs e)
        {
            if (_lbMods.Selection.Count == 0)
            {
                await MessageBox.ShowAsync("", "No mods selected to remove.", icon: MsBox.Avalonia.Enums.Icon.Info);
                return;
            }

            if (await MessageBox.ShowAsync(
                    "Confirm",
                    "Are you sure you want to remove the selected mod(s)?",
                    ButtonEnum.YesNo,
                    MsBox.Avalonia.Enums.Icon.Question
                ) == ButtonResult.No)
            {
                return;
            }

            string removeDescription = "removing " + string.Join(", ", _lbMods.Selection.SelectedItems.Cast<InstalledMod>().Select(m => m.Name));
            try
            {
                LogFiles.LogEvent("Started " + removeDescription);
                List<int> archiveIds = _lbMods.Selection.SelectedItems.Cast<InstalledMod>().Select(m => m.ArchiveId).ToList();
                await Task.Run(() => _archiveSet.Delete(archiveIds, _gameResourceUsages, this, CancellationToken));
                foreach (int index in _lbMods.Selection.SelectedIndexes.OrderByDescending(i => i).ToList())
                {
                    _installedMods.RemoveAt(index);
                }
                await UpdateFlatModArchiveAsync();
                LogFiles.LogEvent("Finished " + removeDescription);
            }
            catch (Exception ex)
            {
                ModInstaller.LogFailure(removeDescription, ex);
                await MessageBox.ShowErrorAsync(ex);
                RefreshModList();
            }
            finally
            {
                _archiveSet.CloseStreams();
            }
        }

        private async void OnReinstallClicked(object? sender, RoutedEventArgs e)
        {
            if (!await ConfirmDiscardPendingChangesAsync("Reinstalling all mods"))
                return;

            await ReinstallModsAsync(true);
        }

        private async Task ReinstallModsAsync(bool showCompletionMessage)
        {
            ModInstaller installer = new ModInstaller(_archiveSet, _gameResourceUsages, this);
            try
            {
                await Task.Run(() => installer.ReinstallAll(this, CancellationToken));
                await UpdateFlatModArchiveAsync();
                LogFiles.LogEvent("Finished reinstalling all mods");
                if (showCompletionMessage)
                    await MessageBox.ShowAsync("", "Mod reinstallation complete.", icon: MsBox.Avalonia.Enums.Icon.Info);
            }
            catch (Exception ex)
            {
                await MessageBox.ShowErrorAsync(ex);
            }
            RefreshModList();
        }

        private async void OnSelectGameClicked(object? sender, RoutedEventArgs e)
        {
            if (!await ConfirmDiscardPendingChangesAsync("Switching to a different game"))
                return;

            _closeConfirmed = true;
            GameSelectionRequested = true;
            Close();
        }

        private async Task UpdateFlatModArchiveAsync()
        {
            ModInstaller installer = new(_archiveSet, _gameResourceUsages, this);
            await Task.Run(() => installer.UpdateFlatModArchive(this, CancellationToken));
        }

        private void OnDragModOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = GetDroppedPathsIfAllowed(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private async void OnDropMod(object? sender, DragEventArgs e)
        {
            List<string>? paths = GetDroppedPathsIfAllowed(e);
            if (paths == null)
                return;

            foreach (string path in paths)
            {
                if (File.Exists(path))
                    await InstallModFromZipAsync(path);
                else
                    await InstallModFromFolderAsync(path);
            }
        }

        private List<string>? GetDroppedPathsIfAllowed(DragEventArgs e)
        {
            if (!_lbMods.IsEnabled)
                return null;

            List<string> paths = [];
            foreach (string path in e.DataTransfer.GetFileSystemPaths())
            {
                if (File.Exists(path))
                {
                    string extension = Path.GetExtension(path);
                    if (extension is ".7z" or ".zip" or ".rar")
                    {
                        paths.Add(path);
                        continue;
                    }
                }

                if (Directory.Exists(path))
                {
                    paths.Add(path);
                    continue;
                }

                return null;
            }
            return paths.Count > 0 ? paths : null;
        }

        private void OnModsListKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Delete:
                    OnRemoveClicked(sender, new());
                    break;
            }
        }

        // Ticking, unticking and typing priorities only mark changes. They are applied together here with a single rebuild.
        private async void OnConfirmClicked(object? sender, RoutedEventArgs e)
        {
            try
            {
                List<InstalledMod> mods = _installedMods.ToList();
                List<int> archiveIdsToEnable = mods.Where(m => m.Enabled && !m.SavedEnabled).Select(m => m.ArchiveId).ToList();
                List<int> archiveIdsToDisable = mods.Where(m => !m.Enabled && m.SavedEnabled).Select(m => m.ArchiveId).ToList();
                bool prioritiesChanged = mods.Any(m => m.Priority != m.SavedPriority);
                if (archiveIdsToEnable.Count == 0 && archiveIdsToDisable.Count == 0 && !prioritiesChanged)
                {
                    await MessageBox.ShowAsync("", "There are no changes to apply.", icon: MsBox.Avalonia.Enums.Icon.Info);
                    return;
                }

                Dictionary<int, int>? priorities = prioritiesChanged ? mods.ToDictionary(m => m.ArchiveId, m => m.Priority) : null;
                LogFiles.LogEvent($"Started applying changes: enabling {archiveIdsToEnable.Count}, disabling {archiveIdsToDisable.Count} mod(s), priorities {(prioritiesChanged ? "changed" : "unchanged")}");
                try
                {
                    await Task.Run(() => _archiveSet.ApplyModChanges(archiveIdsToEnable, archiveIdsToDisable, priorities, _gameResourceUsages, this, CancellationToken));
                    await UpdateFlatModArchiveAsync();
                }
                catch (Exception logEx)
                {
                    ModInstaller.LogFailure("applying changes", logEx);
                    throw;
                }
                LogFiles.LogEvent("Finished applying changes");
                RefreshModList();
                await MessageBox.ShowAsync("", "Changes applied.", icon: MsBox.Avalonia.Enums.Icon.Info);
            }
            catch (Exception ex)
            {
                await MessageBox.ShowErrorAsync(ex);
                RefreshModList();
            }
        }

        private bool HasPendingChanges => _installedMods.Any(m => m.IsChanged);

        private void OnModStateChanged(object? sender, EventArgs e)
        {
            UpdatePendingState();
        }

        // Outlines the Confirm button while ticks or priorities differ from what is applied
        private void UpdatePendingState()
        {
            if (HasPendingChanges)
            {
                _btnConfirm.BorderBrush = Avalonia.Media.Brushes.LimeGreen;
                _btnConfirm.BorderThickness = new Avalonia.Thickness(3);
                ToolTip.SetTip(_btnConfirm, ConfirmToolTip + " (there are changes that haven't been applied)");
            }
            else
            {
                _btnConfirm.ClearValue(Button.BorderBrushProperty);
                _btnConfirm.ClearValue(Button.BorderThicknessProperty);
                ToolTip.SetTip(_btnConfirm, ConfirmToolTip);
            }
        }

        private async Task<bool> ConfirmDiscardPendingChangesAsync(string action)
        {
            if (!HasPendingChanges)
                return true;

            return await MessageBox.ShowAsync(
                       "Unapplied changes",
                       $"Some ticks or priority numbers haven't been applied yet. {action} will discard them.\n\nContinue?",
                       ButtonEnum.YesNo,
                       MsBox.Avalonia.Enums.Icon.Question
                   ) == ButtonResult.Yes;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || _closeConfirmed || !HasPendingChanges)
                return;

            e.Cancel = true;
            _ = ConfirmCloseAsync();
        }

        private async Task ConfirmCloseAsync()
        {
            if (await ConfirmDiscardPendingChangesAsync("Closing the manager"))
            {
                _closeConfirmed = true;
                Close();
            }
        }

        private async void OnChangeGameFolderClicked(object? sender, RoutedEventArgs e)
        {
            try
            {
                CdcGameInfo gameInfo = CdcGameInfo.Get(_archiveSet.Game);
                if (!await ConfirmDiscardPendingChangesAsync("Changing the game folder"))
                    return;

                string? newFolderPath = await GameFolderFinder.BrowseAsync(_archiveSet.Game);
                if (newFolderPath == null)
                    return;

                string currentFolderPath = _archiveSet.FolderPath;
                if (string.Equals(NormalizeFolderPath(newFolderPath), NormalizeFolderPath(currentFolderPath), StringComparison.OrdinalIgnoreCase))
                {
                    await MessageBox.ShowAsync("", "That is the folder the manager is already using.", icon: MsBox.Avalonia.Enums.Icon.Info);
                    return;
                }

                // Mods, the original-archive backups and the usage cache all live inside the game folder, so nothing is moved:
                // the manager simply shows whatever is installed in the folder it points at.
                int currentModCount = CountInstalledMods(currentFolderPath);
                int newModCount = CountInstalledMods(newFolderPath);
                if (await MessageBox.ShowAsync(
                        "Change game folder",
                        $"Switch the {gameInfo.ShortName} folder to:\n{newFolderPath}\n\n" +
                        "Mods are stored inside the game folder, so the mods installed in the current folder " +
                        $"({currentModCount}) stay there and are not moved. The new folder has {newModCount} mod(s) installed.\n\nContinue?",
                        ButtonEnum.YesNo,
                        MsBox.Avalonia.Enums.Icon.Question
                    ) == ButtonResult.No)
                {
                    return;
                }

                Configuration config = Configuration.Load();
                config.GameFolderPaths[_archiveSet.Game] = newFolderPath;
                config.Save();

                _closeConfirmed = true;
                GameFolderChangeRequested = true;
                Close();
            }
            catch (Exception ex)
            {
                await MessageBox.ShowErrorAsync(ex);
            }
        }

        private static string NormalizeFolderPath(string folderPath)
        {
            return Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // Counts the mods (enabled or disabled) installed in a game folder
        private static int CountInstalledMods(string folderPath)
        {
            try
            {
                return Directory.EnumerateFiles(folderPath, ArchiveSet.ModArchivePrefix + "*.000.000.nfo*").Count();
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private void OnSortByNameClicked(object? sender, RoutedEventArgs e)
        {
            SetSortColumn(ModSortColumn.Name);
        }

        private void OnSortByPriorityClicked(object? sender, RoutedEventArgs e)
        {
            SetSortColumn(ModSortColumn.Priority);
        }

        private void SetSortColumn(ModSortColumn column)
        {
            if (_sortColumn == column)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }
            ApplySort();
        }

        private void ApplySort()
        {
            IEnumerable<InstalledMod> sorted;
            if (_sortColumn == ModSortColumn.Name)
            {
                sorted = _sortAscending ? _installedMods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                                        : _installedMods.OrderByDescending(m => m.Name, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                sorted = _sortAscending ? _installedMods.OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                                        : _installedMods.OrderByDescending(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
            }

            List<InstalledMod> sortedMods = sorted.ToList();
            _installedMods.Clear();
            _installedMods.AddRange(sortedMods);

            string arrow = _sortAscending ? " \u25B2" : " \u25BC";
            _btnSortName.Content = "Mod Name" + (_sortColumn == ModSortColumn.Name ? arrow : "");
            _btnSortPriority.Content = "Priority" + (_sortColumn == ModSortColumn.Priority ? arrow : "");
        }

        private void RefreshModList()
        {
            Dictionary<int, int> priorities = _archiveSet.GetEffectiveModPriorities();

            List<InstalledMod> mods = _archiveSet.Archives
                                                 .Where(a => a.ModName != null && a.SubId == 0)
                                                 .Select(a => new InstalledMod(a.Id, a.ModName!, a.MetaData!.Enabled, priorities.GetValueOrDefault(a.Id, 1)))
                                                 .ToList();
            foreach (InstalledMod mod in mods)
            {
                mod.StateChanged += OnModStateChanged;
            }

            _installedMods.Clear();
            _installedMods.AddRange(mods);
            ApplySort();
            UpdatePendingState();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _archiveSet?.Dispose();
        }
    }
}