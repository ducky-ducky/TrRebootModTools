using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrRebootTools.ModManager.Util;
using TrRebootTools.Shared;
using TrRebootTools.Shared.Cdc;
using TrRebootTools.Shared.Serialization;
using TrRebootTools.Shared.Util;

namespace TrRebootTools.ModManager.Mod
{
    public class ModInstaller
    {
        private record struct ResourceCollectionItem(ResourceCollection Collection, int ResourceIndex);

        private readonly ArchiveSet _archiveSet;
        private readonly ResourceUsageCache _gameResourceUsageCache;

        public ModInstaller(ArchiveSet archiveSet, ResourceUsageCache gameResourceUsageCache, Window? mainWindow = null)
        {
            _archiveSet = archiveSet;
            _gameResourceUsageCache = gameResourceUsageCache;
        }

        public async Task<InstalledMod?> InstallFromZipAsync(string filePath, ITaskProgress progress, CancellationToken cancellationToken)
        {
            string modName = Regex.Replace(Path.GetFileNameWithoutExtension(filePath), @"(-\d+)+$", "");
            if (await IsModAlreadyInstalledAsync(modName))
                return null;

            LogFiles.LogEvent($"Started installing {modName} (from {filePath})");
            try
            {
                using ZipTempExtractor extractor = new(filePath);
                extractor.Extract(progress, cancellationToken);

                using ModPackage modPackage = new FolderModPackage(modName, extractor.FolderPath, false, _archiveSet, _gameResourceUsageCache);
                return await InstallAsync(modPackage, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                LogFailure($"installing {modName}", ex);
                throw;
            }
        }

        // Records a failure in the report (the full details go to the error log when the error is shown)
        internal static void LogFailure(string what, Exception ex)
        {
            if (ex is OperationCanceledException)
                LogFiles.LogEvent($"Cancelled while {what}");
            else
                LogFiles.LogEvent($"FAILED {what}: {ex.Message}");
        }

        public async Task<InstalledMod?> InstallFromFolderAsync(string folderPath, ITaskProgress progress, CancellationToken cancellationToken)
        {
            string modName = Path.GetFileName(folderPath);
            return await InstallFromFolderAsync(modName, folderPath, progress, cancellationToken);
        }

        public async Task<InstalledMod?> InstallFromFolderAsync(string modName, string folderPath, ITaskProgress progress, CancellationToken cancellationToken)
        {
            Archive? existingArchive = _archiveSet.Archives.FirstOrDefault(a => a.ModName != null && Regex.Replace(a.ModName, @" \(.+\)$", "") == modName);
            if (existingArchive != null)
                _archiveSet.Delete(existingArchive.Id, _gameResourceUsageCache, progress, cancellationToken);

            LogFiles.LogEvent($"Started installing {modName} (from {folderPath})");
            try
            {
                using ModPackage modPackage = new FolderModPackage(modName, folderPath, true, _archiveSet, _gameResourceUsageCache);
                return await InstallAsync(modPackage, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                LogFailure($"installing {modName}", ex);
                throw;
            }
        }

        public void ReinstallAll(ITaskProgress progress, CancellationToken cancellationToken)
        {
            s_isReinstalling = true;
            LogFiles.LogEvent("Started reinstalling all mods");
            try
            {
                ReinstallAllInternal(progress, cancellationToken);
            }
            catch (Exception ex)
            {
                LogFailure("reinstalling all mods", ex);
                throw;
            }
            finally
            {
                s_isReinstalling = false;
            }
        }

        private void ReinstallAllInternal(ITaskProgress progress, CancellationToken cancellationToken)
        {
            List<OriginalModInfo> originalMods = [];
            foreach (IGrouping<int, Archive> archivesOfId in _archiveSet.Archives
                                                                        .Concat(_archiveSet.DuplicateArchives)
                                                                        .Where(a => a.ModName != null)
                                                                        .GroupBy(a => a.Id)
                                                                        .OrderByDescending(g => g.First().MetaData!.Version))
            {
                _archiveSet.CloseStreams();

                ArchiveMetaData metaData = archivesOfId.First().MetaData!;
                OriginalModInfo originalModInfo = new(
                    ModName: archivesOfId.First().ModName!,
                    Enabled: metaData.Enabled,
                    NfoFilePath: metaData.FilePath + ".orig",
                    ArchiveFilePaths: [],
                    Priority: metaData.Priority,
                    PartFilePaths: []
                );

                _archiveSet.Disable(archivesOfId.Key, _gameResourceUsageCache, progress, cancellationToken);

                File.Delete(originalModInfo.NfoFilePath);
                File.Move(metaData.FilePath, originalModInfo.NfoFilePath);

                foreach (Archive archive in archivesOfId)
                {
                    string archiveFilePath = archive.BaseFilePath;
                    string origFilePath = archiveFilePath + ".orig";
                    File.Delete(origFilePath);
                    File.Move(archiveFilePath, origFilePath);
                    originalModInfo.ArchiveFilePaths.Add(origFilePath);

                    // The extra parts of a large archive (.001.tiger and so on) move aside too. The archive is found again through
                    // the name of its first part, so each part gets ".orig" added to its name. Delete() below only removes
                    // the parts under their normal names, so moving them first keeps them from being deleted.
                    for (int part = 1; part < archive.NumParts; part++)
                    {
                        string partFilePath = archive.GetPartFilePath(part);
                        string origPartFilePath = partFilePath + ".orig";
                        File.Delete(origPartFilePath);
                        if (File.Exists(partFilePath))
                            File.Move(partFilePath, origPartFilePath);
                        originalModInfo.PartFilePaths.Add(origPartFilePath);
                    }
                }

                _archiveSet.Delete(archivesOfId.Key, _gameResourceUsageCache, progress, cancellationToken);
                originalMods.Insert(0, originalModInfo);
            }

            Dictionary<int, int> reinstalledPriorities = new();
            foreach (OriginalModInfo originalMod in originalMods)
            {
                using (ModPackage modPackage = new TigerModPackage(originalMod.NfoFilePath, originalMod.ArchiveFilePaths, _archiveSet.Game))
                {
                    LogFiles.LogEvent($"Reinstalling {originalMod.ModName}");
                    InstalledMod installedMod = Install(modPackage, null, false, progress, cancellationToken);
                    if (installedMod == null)
                        continue;

                    LogFiles.LogEvent($"Reinstalled {originalMod.ModName}");

                    if (originalMod.Priority != null)
                        reinstalledPriorities[installedMod.ArchiveId] = originalMod.Priority.Value;

                    if (!originalMod.Enabled)
                        _archiveSet.Disable([installedMod.ArchiveId], _gameResourceUsageCache, progress, cancellationToken);
                }

                File.Delete(originalMod.NfoFilePath);
                foreach (string archiveFilePath in originalMod.ArchiveFilePaths.Concat(originalMod.PartFilePaths))
                {
                    File.Delete(archiveFilePath);
                }
            }

            // Reinstalling gives every mod a fresh priority by install order, so put the priorities the user chose back
            if (reinstalledPriorities.Count > 0)
                _archiveSet.SetModPriorities(reinstalledPriorities, _gameResourceUsageCache, progress, cancellationToken);
        }

        public void UpdateFlatModArchive(ITaskProgress progress, CancellationToken cancellationToken)
        {
            ArchiveIdentity? flattenedArchiveIdent = _archiveSet.GetActiveFlattenedModArchiveIdentity();
            if (flattenedArchiveIdent == null)
                return;

            List<TigerModPackage> modPackages = [];

            Archive? origGameArchive = _archiveSet.GetArchive(flattenedArchiveIdent.Id, 0);
            if (origGameArchive == null)
                throw new Exception($"Archive with ID {flattenedArchiveIdent.Id} not found");

            modPackages.Add(new TigerModPackage([origGameArchive], _archiveSet.Game));

            foreach (IGrouping<int, Archive> archivesOfId in _archiveSet.Archives
                                                                        .Where(a => a.ModName != null && a.MetaData!.Enabled)
                                                                        .GroupBy(a => a.Id)
                                                                        .OrderByDescending(g => ArchiveSet.GetModPriority(g.First()))
                                                                        .ThenBy(g => g.First().MetaData!.Version))
            {
                modPackages.Add(new TigerModPackage(archivesOfId, _archiveSet.Game));
            }

            using var modPackage = new MultiTigerModPackage(flattenedArchiveIdent.FileName.Replace(".000.tiger", ""), modPackages);
            LogFiles.LogEvent($"Started rebuilding {flattenedArchiveIdent.FileName} from the original and {modPackages.Count - 1} enabled mod(s)");
            try
            {
                Install(modPackage, null, true, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                LogFailure($"rebuilding {flattenedArchiveIdent.FileName}", ex);
                throw;
            }
            LogFiles.LogEvent($"Finished rebuilding {flattenedArchiveIdent.FileName}");
        }

        private record OriginalModInfo(
            string ModName,
            bool Enabled,
            string NfoFilePath,
            List<string> ArchiveFilePaths,
            int? Priority,
            List<string> PartFilePaths
        );

        private async Task<bool> IsModAlreadyInstalledAsync(string modName)
        {
            if (_archiveSet.Archives.Any(a => a.ModName == modName))
            {
                await MessageBox.ShowAsync("", $"The mod {modName} is already installed.", icon: MsBox.Avalonia.Enums.Icon.Info);
                return true;
            }
            return false;
        }

        private async Task<InstalledMod?> InstallAsync(ModPackage modPackage, ITaskProgress progress, CancellationToken cancellationToken)
        {
            ModVariation? modVariation = null;
            if (modPackage.Variations != null && modPackage.Variations.Count > 0)
            {
                modVariation = await Dispatcher.UIThread.InvokeAsync(() => VariationSelectionWindow.ShowDialogAsync(modPackage));
                if (modVariation == null)
                    return null;
            }
            return Install(modPackage, modVariation, false, progress, cancellationToken);
        }

        private InstalledMod Install(ModPackage modPackage, ModVariation? modVariation, bool flattened, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            ArchiveSet? archiveSet = null;
            Dictionary<ulong, Archive>? archives = null;
            string modName = modPackage.Name;
            if (modVariation != null)
                modName += $" ({modVariation.Name})";

            try
            {
                progress?.Begin($"Installing mod {modName}...");

                _archiveSet.CloseStreams();
                ResourceUsageCache resourceUsageCache = !flattened ? GetFullResourceUsageCache() : _gameResourceUsageCache;
                archiveSet = !flattened ? _archiveSet : ArchiveSet.Open(_archiveSet.FolderPath, true, false, _archiveSet.Game);

                ResourceKeyLookup modResourceKeys = new(modPackage.Resources);
                if (modVariation != null)
                    AddModVariationResources(modResourceKeys, modVariation.Resources);

                Dictionary<ResourceKey, List<ResourceCollectionItemReference>> modResourceUsages =
                    modResourceKeys.ToDictionary(
                        r => r,
                        r => (r.Locale == 0xFFFFFFFFFFFFFFFF ? resourceUsageCache.GetResourceUsages(archiveSet, r.Type, r.Id)
                                                             : resourceUsageCache.GetResourceUsages(archiveSet, r)
                             ).ToList()
                );
                List<ResourceKey> unusedResourceKeys = modResourceUsages.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList();
                modResourceUsages.RemoveAll(p => p.Value.Count == 0);

                List<ArchiveFileDescriptor> collectionReferences = modResourceUsages.Values
                                                                                    .SelectMany(c => c)
                                                                                    .Select(c => c.CollectionReference)
                                                                                    .Distinct()
                                                                                    .ToList();
                Dictionary<ArchiveFileKey, ResourceCollection> modResourceCollections = new();
                List<string> collectionFailures = [];
                foreach (ArchiveFileDescriptor collectionReference in collectionReferences)
                {
                    ResourceCollection? collection = archiveSet.GetResourceCollection(collectionReference);
                    if (collection == null)
                    {
                        collectionFailures.Add(archiveSet.DescribeResourceCollectionFailure(collectionReference));
                        continue;
                    }
                    modResourceCollections[(ArchiveFileKey)collectionReference] = collection;
                }
                if (collectionFailures.Count > 0)
                    throw new InvalidOperationException($"{collectionFailures.Count} of {collectionReferences.Count} resource collections (.drm files) needed by this mod could not be opened. First failure: {collectionFailures[0]}");

                // The combined archive replaces the original game archive, so everything the game archive contained has to end up in it.
                // That includes resources (such as the variants for other graphics modes) that mods can't target and that the
                // usage cache doesn't know about, so the collections that reference them are loaded here as well.
                Archive? originalArchive = null;
                if (flattened)
                {
                    ArchiveIdentity? flattenedIdentity = archiveSet.GetActiveFlattenedModArchiveIdentity();
                    originalArchive = flattenedIdentity != null ? archiveSet.GetArchive(flattenedIdentity.Id, 0) : null;
                    if (originalArchive != null)
                        AddCollectionsWithUnwrittenOriginalResources(archiveSet, originalArchive, modResourceKeys, modResourceCollections);
                }

                Dictionary<ResourceCollection, int> baselineDuplicateRows = modResourceCollections.Values.ToDictionary(c => c, CountDuplicateRows);

                Dictionary<ResourceKey, List<ResourceCollectionItem>> modResourceCollectionItems =
                    modResourceUsages.ToDictionary(
                        p => p.Key,
                        p => p.Value
                              .Select(r => new ResourceCollectionItem(modResourceCollections[r.CollectionReference], r.ResourceIndex))
                              .ToList()
                    );

                Dictionary<ArchiveFileKey, HashSet<ResourceKey>> resourcesToAdd = GetResourcesToAdd(modPackage, modVariation, modResourceKeys, resourceUsageCache);
                AddResourcesToCollections(modResourceCollections, modResourceCollectionItems, resourcesToAdd, resourceUsageCache);

                IEnumerable<ArchiveFileKey> fileKeys = modResourceCollections.Keys.Concat(modPackage.Files).Concat(modVariation?.Files ?? []);
                Dictionary<ulong, int> fileCountsByLocale = fileKeys.GroupBy(f => f.Locale).ToDictionary(g => g.Key, g => g.Count());
                fileCountsByLocale.TryAdd(0xFFFFFFFFFFFFFFFF, 0);

                archives = !flattened ? archiveSet.CreateModArchives(modName, fileCountsByLocale)
                                      : new() { { 0xFFFFFFFFFFFFFFFF, archiveSet.CreateFlattenedModArchive(fileCountsByLocale.Values.Sum()) } };

                InstallReport report = new();
                uint nextDecompressionOffset = AddResourcesToArchive(archives[0xFFFFFFFFFFFFFFFF], modPackage, modVariation, modResourceCollectionItems, report, progress, cancellationToken);

                (int Resources, int Rows) carriedOver = (0, 0);
                if (originalArchive != null)
                    carriedOver = CarryOverOriginalResources(archives[0xFFFFFFFFFFFFFFFF], originalArchive, modResourceCollections.Values, report, nextDecompressionOffset, cancellationToken);

                AddFilesToArchives(archives, modPackage, modVariation, modResourceCollections.Values);

                IntegrityResult integrity = CheckIntegrity(archives[0xFFFFFFFFFFFFFFFF], modResourceCollections.Values, baselineDuplicateRows, archiveSet.Game);
                WriteInstallReport(modName, flattened, modPackage, modResourceKeys, unusedResourceKeys, modResourceCollectionItems, report, archives[0xFFFFFFFFFFFFFFFF], resourceUsageCache, archiveSet, carriedOver, integrity);

                foreach (Archive archive in archives.Values.Distinct())
                {
                    archive.CloseStreams();
                }

                // The archive is complete, so the .nfo is written now. It is what makes the mod count as installed.
                foreach (ArchiveMetaData metaData in archives.Values.Select(a => a.MetaData).OfType<ArchiveMetaData>().Distinct())
                {
                    metaData.Save();
                }

                if (!flattened)
                {
                    foreach (Archive archive in archives.Values.Distinct())
                    {
                        archiveSet.Add(archive, _gameResourceUsageCache, progress, cancellationToken);
                    }
                }
                if (!flattened)
                    s_newModName = s_isReinstalling ? null : modName;

                return new InstalledMod(archives.Values.First().Id, modName, true, archives.Values.First().MetaData?.Priority ?? 1);
            }
            catch
            {
                if (archives != null)
                {
                    foreach (Archive archive in archives.Values.Distinct())
                    {
                        archive.CloseStreams();
                        if (archive.MetaData != null)
                            File.Delete(archive.MetaData.FilePath);

                        File.Delete(archive.BaseFilePath);
                        archive.DeleteExtraPartFiles();

                        if (flattened)
                            RestoreOriginalFlatArchive(archive);
                    }
                }
                throw;
            }
            finally
            {
                archiveSet?.CloseStreams();
                progress?.End();
            }
        }

        // A failed rebuild leaves the combined archive (patch2.000.tiger) incomplete or missing, which would stop the game from starting.
        // The untouched copy of the game's own archive (bigfile._orig.patch2.000.tiger) is put back in its place, so the game runs
        // without mods until the next rebuild succeeds. The copy itself is left alone.
        private static void RestoreOriginalFlatArchive(Archive failedArchive)
        {
            string archiveFilePath = failedArchive.BaseFilePath;
            string backupFilePath = Path.Combine(
                Path.GetDirectoryName(archiveFilePath)!,
                ArchiveSet.OriginalGameArchivePrefix + Path.GetFileName(archiveFilePath)
            );
            string archiveFileName = Path.GetFileName(archiveFilePath);
            try
            {
                if (!File.Exists(backupFilePath))
                {
                    LogFiles.LogEvent($"Could not restore {archiveFileName}: the backup {Path.GetFileName(backupFilePath)} was not found");
                    return;
                }

                File.Copy(backupFilePath, archiveFilePath, true);
                LogFiles.LogEvent($"Restored the original {archiveFileName} from {Path.GetFileName(backupFilePath)}. The game runs without mods until the next successful rebuild.");
            }
            catch (Exception restoreEx)
            {
                LogFiles.LogEvent($"Could not restore {archiveFileName} from {Path.GetFileName(backupFilePath)}: {restoreEx.Message}");
            }
        }

        private ResourceUsageCache GetFullResourceUsageCache()
        {
            ResourceUsageCache fullResourceUsageCache = new ResourceUsageCache(_gameResourceUsageCache);
            using (ArchiveSet modArchiveSet = ArchiveSet.Open(_archiveSet.FolderPath, false, true, _archiveSet.Game))
            {
                fullResourceUsageCache.AddArchiveSet(modArchiveSet, null, CancellationToken.None);
            }
            return fullResourceUsageCache;
        }

        private void AddModVariationResources(ResourceKeyLookup modResourceKeys, IEnumerable<ResourceKey> variationResourceKeys)
        {
            foreach (ResourceKey resourceKey in variationResourceKeys)
            {
                if (modResourceKeys.Contains(resourceKey))
                {
                    string extension = ResourceNaming.GetExtension(resourceKey.Type, resourceKey.SubType, _archiveSet.Game);
                    throw new Exception($"The resource {resourceKey.Id}{extension} exists in both the base mod and the selected variation.");
                }
                modResourceKeys.Add(resourceKey);
            }
        }

        private Dictionary<ArchiveFileKey, HashSet<ResourceKey>> GetResourcesToAdd(
            ModPackage modPackage,
            ModVariation? modVariation,
            ResourceKeyLookup modResourceKeys,
            ResourceUsageCache fullResourceUsageCache)
        {
            Dictionary<ArchiveFileKey, HashSet<ResourceKey>> resourcesToAdd = new();
            foreach (ResourceKey modResourceKey in modResourceKeys)
            {
                HashSet<ResourceKey> externalResourceKeys = new();
                CollectExternalResourceKeys(modPackage, modVariation, modResourceKeys, modResourceKey, externalResourceKeys, fullResourceUsageCache);
                if (externalResourceKeys.Count == 0)
                    continue;

                foreach (ResourceCollectionItemReference modResourceUsage in fullResourceUsageCache.GetResourceUsages(_archiveSet, modResourceKey))
                {
                    resourcesToAdd.GetOrAdd(modResourceUsage.CollectionReference, () => []).UnionWith(externalResourceKeys);
                }
            }
            return resourcesToAdd;
        }

        private void CollectExternalResourceKeys(
            ModPackage modPackage,
            ModVariation? modVariation,
            ResourceKeyLookup modResourceKeys,
            ResourceKey resourceKey,
            HashSet<ResourceKey> allExternalResourceKeys,
            ResourceUsageCache fullResourceUsageCache)
        {
            if (!MightHaveRefDefinitions(resourceKey))
                return;

            ResourceDescriptor? resource = fullResourceUsageCache.GetResourceDescriptor(_archiveSet, resourceKey);
            if (resource != null && resource.RefDefinitionsSize == 0)
                return;

            List<ResourceKey> externalResourceKeys;
            Stream? resourceStream = null;
            try
            {
                if (modResourceKeys.Contains(resourceKey))
                    resourceStream = modVariation?.OpenResource(resourceKey) ?? modPackage.OpenResource(resourceKey)!;
                else if (resource != null)
                    resourceStream = _archiveSet.OpenResource(resource);
                else
                    return;

                if (!resourceStream.CanSeek)
                {
                    MemoryStream memResourceStream = new MemoryStream();
                    resourceStream.CopyTo(memResourceStream);
                    resourceStream.Close();
                    memResourceStream.Position = 0;
                    resourceStream = memResourceStream;
                }

                externalResourceKeys = ResourceReader.Create(resource, resourceStream, _archiveSet.Game)
                                                     .ExternalRefs
                                                     .SelectMany(r => GetLocaleSpecificResourceKeys(r.ExternalResource!.Value, modResourceKeys, fullResourceUsageCache))
                                                     .Select(r => GetResourceKeyWithAddedSubType(r, modResourceKeys))
                                                     .ToList();
            }
            finally
            {
                resourceStream?.Close();
            }

            foreach (ResourceKey externalResourceKey in externalResourceKeys)
            {
                if (resourceKey.SubType == ResourceSubType.Model && externalResourceKey.Type == ResourceType.Model)
                    continue;

                if (allExternalResourceKeys.Add(externalResourceKey))
                    CollectExternalResourceKeys(modPackage, modVariation, modResourceKeys, externalResourceKey, allExternalResourceKeys, fullResourceUsageCache);
            }
        }

        private static IEnumerable<ResourceKey> GetLocaleSpecificResourceKeys(ResourceKey resourceKey, ResourceKeyLookup modResourceKeys, ResourceUsageCache resourceUsageCache)
        {
            if (resourceKey.Locale != 0xFFFFFFFFFFFFFFFF)
            {
                yield return resourceKey;
                yield break;
            }

            IReadOnlyCollection<ulong> cacheLocales = resourceUsageCache.GetResourceLocales(resourceKey.Type, resourceKey.Id) ?? [];
            IReadOnlyCollection<ulong> modLocales = modResourceKeys.GetLocales(resourceKey.Type, resourceKey.Id) ?? [];
            if (cacheLocales.Count == 0 && modLocales.Count == 0)
            {
                yield return resourceKey;
                yield break;
            }

            foreach (ulong locale in cacheLocales.Union(modLocales))
            {
                yield return new ResourceKey(resourceKey.Type, resourceKey.SubType, resourceKey.Id, locale);
            }
        }

        private static ResourceKey GetResourceKeyWithAddedSubType(ResourceKey resourceKey, ResourceKeyLookup modResourceKeys)
        {
            switch (resourceKey.Type)
            {
                case ResourceType.Texture:
                    return new ResourceKey(ResourceType.Texture, ResourceSubType.Texture, resourceKey.Id, resourceKey.Locale);

                case ResourceType.Model:
                {
                    ResourceSubType? subType = modResourceKeys.GetSubType(resourceKey.Type, resourceKey.Id);
                    if (subType != null)
                        return new ResourceKey(resourceKey.Type, subType.Value, resourceKey.Id, resourceKey.Locale);

                    break;
                }
            }
            return resourceKey;
        }

        private void AddResourcesToCollections(
            Dictionary<ArchiveFileKey, ResourceCollection> modResourceCollections,
            Dictionary<ResourceKey, List<ResourceCollectionItem>> modResourceCollectionItems,
            Dictionary<ArchiveFileKey, HashSet<ResourceKey>> resourcesToAdd,
            ResourceUsageCache resourceUsageCache)
        {
            foreach ((ArchiveFileKey collectionKey, ICollection<ResourceKey> resourcesForCollection) in resourcesToAdd)
            {
                ResourceCollection? modCollection = modResourceCollections.GetValueOrDefault(collectionKey);
                if (modCollection == null)
                    continue;

                foreach (ResourceKey resourceKey in resourcesForCollection)
                {
                    if (modCollection.Contains(resourceKey))
                        continue;

                    int modCollectionResourceIdx = -1;
                    ResourceCollectionItemReference? existingUsage = resourceUsageCache.GetResourceUsages(_archiveSet, resourceKey).FirstOrDefault();
                    if (existingUsage != null)
                    {
                        ResourceCollection? sourceCollection = _archiveSet.GetResourceCollection(existingUsage.CollectionReference);
                        if (sourceCollection != null)
                            modCollectionResourceIdx = modCollection.Add(sourceCollection, existingUsage.ResourceIndex);
                    }
                    modResourceCollectionItems.GetOrAdd(resourceKey, () => []).Add(new ResourceCollectionItem(modCollection, modCollectionResourceIdx));
                }
            }
        }

        // The mod installed just before the combined archive is rebuilt, if the rebuild is the result of an install. Reported with the rebuild.
        private static string? s_newModName;
        private static bool s_isReinstalling;

        private record IntegrityResult(int RowsChecked, int BadRows, int NewDuplicateRows, List<string> Examples);

        // Rows that are exact copies of another row in the same collection (same resource, same location)
        private static int CountDuplicateRows(ResourceCollection collection)
        {
            HashSet<(ResourceKey, int, int, int, uint, uint)> seen = new();
            int duplicates = 0;
            foreach (ResourceDescriptor resource in collection.Resources)
            {
                if (!seen.Add((resource, resource.ArchiveId, resource.ArchiveSubId, resource.ArchivePart, resource.Offset, resource.Length)))
                    duplicates++;
            }
            return duplicates;
        }

        private static IntegrityResult CheckIntegrity(Archive archive, IEnumerable<ResourceCollection> collections, Dictionary<ResourceCollection, int> baselineDuplicateRows, CdcGame game)
        {
            int rowsChecked = 0;
            int badRows = 0;
            int newDuplicateRows = 0;
            List<string> examples = [];

            foreach (ResourceCollection collection in collections)
            {
                int duplicatesNow = CountDuplicateRows(collection);
                int baseline = baselineDuplicateRows.GetValueOrDefault(collection, 0);
                if (duplicatesNow > baseline)
                {
                    newDuplicateRows += duplicatesNow - baseline;
                    if (examples.Count < 10)
                        examples.Add($"{duplicatesNow - baseline} duplicate row(s) in collection 0x{collection.NameHash:X}");
                }

                if (game != CdcGame.Tr2013)
                    continue;

                foreach (ResourceDescriptor resource in collection.Resources)
                {
                    if (resource.ArchiveId != archive.Id || resource.ArchiveSubId != archive.SubId)
                        continue;

                    rowsChecked++;
                    if (!archive.HasResourceHeaderAt(resource.ArchivePart, resource.Offset))
                    {
                        badRows++;
                        if (examples.Count < 10)
                            examples.Add($"{(ResourceKey)resource} in collection 0x{collection.NameHash:X} points to part {resource.ArchivePart}, offset 0x{resource.Offset:X}, where there is no resource");
                    }
                }
            }

            return new IntegrityResult(rowsChecked, badRows, newDuplicateRows, examples);
        }

        // Loads the collections of the original game archive that contain rows we may have to carry over but that aren't already loaded
        private static void AddCollectionsWithUnwrittenOriginalResources(ArchiveSet archiveSet, Archive originalArchive, ResourceKeyLookup modResourceKeys, Dictionary<ArchiveFileKey, ResourceCollection> modResourceCollections)
        {
            foreach (ArchiveFileDescriptor file in originalArchive.Files)
            {
                ArchiveFileKey fileKey = (ArchiveFileKey)file;
                if (modResourceCollections.ContainsKey(fileKey))
                    continue;

                ResourceCollection? collection = archiveSet.GetResourceCollection(file);
                if (collection == null)
                    continue;

                bool hasUnwrittenResources = collection.Resources.Any(r => r.ArchiveId == originalArchive.Id &&
                                                                          r.ArchiveSubId == originalArchive.SubId &&
                                                                          !modResourceKeys.Contains(r));
                if (hasUnwrittenResources)
                    modResourceCollections[fileKey] = collection;
            }
        }

        // The combined archive replaces the original game archive. Any collection row that still points into the original
        // archive (because no mod or original-package resource was written for it) would point at nothing in the new archive,
        // so copy those resources across and repoint the rows.
        private static (int Resources, int Rows) CarryOverOriginalResources(
            Archive archive,
            Archive originalArchive,
            IEnumerable<ResourceCollection> collections,
            InstallReport report,
            uint decompressionOffset,
            CancellationToken cancellationToken)
        {
            HashSet<ResourceKey> writtenKeys = report.Written.Select(w => w.Key).ToHashSet();
            Dictionary<(int Part, uint Offset), (ArchiveBlobDescriptor Blob, uint DecompressionOffset)> carried = new();
            int carriedRows = 0;

            foreach (ResourceCollection collection in collections)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<(int Index, ResourceDescriptor Resource)> pending = [];
                IReadOnlyList<ResourceDescriptor> resources = collection.Resources;
                for (int i = 0; i < resources.Count; i++)
                {
                    ResourceDescriptor resource = resources[i];
                    if (resource.ArchiveId == originalArchive.Id && resource.ArchiveSubId == originalArchive.SubId && !writtenKeys.Contains(resource))
                        pending.Add((i, resource));
                }

                foreach ((int index, ResourceDescriptor resource) in pending)
                {
                    (int, uint) source = (resource.ArchivePart, resource.Offset);
                    if (!carried.TryGetValue(source, out var carriedResource))
                    {
                        using Stream stream = originalArchive.OpenResource(resource);
                        ArchiveBlobDescriptor blob = archive.AddResource(stream);
                        carriedResource = (blob, decompressionOffset);
                        carried[source] = carriedResource;

                        decompressionOffset += (uint)stream.Length;
                        decompressionOffset = (decompressionOffset + 0xF) & ~0xFu;
                    }

                    ResourceDescriptor movedResource =
                        new ResourceDescriptor(
                            resource.Type,
                            resource.SubType,
                            resource.Id,
                            resource.Locale,
                            carriedResource.Blob.ArchiveId,
                            carriedResource.Blob.ArchiveSubId,
                            carriedResource.Blob.ArchivePart,
                            carriedResource.Blob.Offset,
                            carriedResource.Blob.Length,
                            carriedResource.DecompressionOffset,
                            resource.RefDefinitionsSize,
                            resource.BodySize
                        );
                    collection.Set(index, movedResource);
                    carriedRows++;
                }
            }

            return (carried.Count, carriedRows);
        }

        private sealed class InstallReport
        {
            public readonly List<(ResourceKey Key, int Part, long Offset, long Length)> Written = new();
            public readonly List<ResourceKey> NoData = new();
        }

        // The mods that were already installed before this one, with whether they are ticked and their priority
        private static void AppendInstalledMods(StringBuilder sb, ArchiveSet archiveSet, string newModName)
        {
            Dictionary<int, int> priorities = archiveSet.GetEffectiveModPriorities();
            List<Archive> installedMods = archiveSet.Archives
                                                    .Where(a => a.ModName != null && a.SubId == 0 && a.MetaData != null && a.ModName != newModName)
                                                    .OrderBy(a => priorities.GetValueOrDefault(a.Id, int.MaxValue))
                                                    .ToList();

            sb.AppendLine($"Mods already installed: {installedMods.Count}");
            foreach (Archive mod in installedMods)
            {
                sb.AppendLine($"  {mod.ModName}\t{(mod.MetaData!.Enabled ? "enabled" : "disabled")}\tpriority {priorities.GetValueOrDefault(mod.Id)}");
            }
            sb.AppendLine();
        }

        private static void WriteInstallReport(
            string modName,
            bool flattened,
            ModPackage modPackage,
            ResourceKeyLookup modResourceKeys,
            List<ResourceKey> unusedResourceKeys,
            Dictionary<ResourceKey, List<ResourceCollectionItem>> modResourceCollectionItems,
            InstallReport report,
            Archive archive,
            ResourceUsageCache resourceUsageCache,
            ArchiveSet archiveSet,
            (int Resources, int Rows) carriedOver,
            IntegrityResult integrity)
        {
            try
            {
                List<ResourceKey> missingData = report.NoData.Where(k => modResourceKeys.Contains(k)).ToList();
                int numReferencedGameResources = modResourceCollectionItems.Keys.Count(k => !modResourceKeys.Contains(k));

                MultiTigerModPackage? multiPackage = modPackage as MultiTigerModPackage;
                string? newModName = null;
                if (flattened)
                {
                    newModName = s_newModName;
                    s_newModName = null;
                }

                StringBuilder sb = new();
                sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {CdcGameInfo.Get(archiveSet.Game).ShortName} | {modName} | {(flattened ? "combined patch archive (the one the game loads)" : "separate mod archive")} ===");
                if (flattened)
                {
                    sb.AppendLine(newModName != null
                                      ? $"Rebuilt after installing \"{newModName}\""
                                      : "Rebuilt because mods were enabled, disabled, reordered, removed or reinstalled");
                    sb.AppendLine($"Total resources in the archive (original game and enabled mods, shared ones counted once): {modResourceKeys.Count}");
                    if (multiPackage != null && newModName != null)
                    {
                        (int totalResources, int fromNewMod, int onlyFromNewMod) = multiPackage.CountResourcesOf(newModName);
                        sb.AppendLine($"  Already in the archive before this install: {totalResources - onlyFromNewMod}");
                        sb.AppendLine($"  Added by this install: {fromNewMod} ({onlyFromNewMod} new to the archive, {fromNewMod - onlyFromNewMod} also provided by the game or other mods)");
                    }
                }
                else
                {
                    sb.AppendLine($"Resources in this mod: {modResourceKeys.Count}");
                }
                sb.AppendLine($"Installed (written to the archive): {report.Written.Count}");
                sb.AppendLine($"Skipped, not used by any game resource collection: {unusedResourceKeys.Count}");
                sb.AppendLine($"Skipped, no data could be read from the mod: {missingData.Count}");
                sb.AppendLine($"Existing game resources pulled into collections because mod resources reference them: {numReferencedGameResources}");
                sb.AppendLine($"Archive parts: {archive.NumParts}");
                if (flattened)
                    sb.AppendLine($"Original game resources carried over unchanged (not touched by any mod): {carriedOver.Resources} resources, referenced by {carriedOver.Rows} collection rows");
                if (archiveSet.Game == CdcGame.Tr2013)
                    sb.AppendLine($"Integrity check: {integrity.RowsChecked} collection rows point into this archive, {integrity.BadRows} of them do not start with a valid resource header, {integrity.NewDuplicateRows} duplicate rows were introduced");
                else
                    sb.AppendLine($"Integrity check: the resource header check is only done for TR2013 and was not run for this game; {integrity.NewDuplicateRows} duplicate rows were introduced");
                if (integrity.BadRows > 0 || integrity.NewDuplicateRows > 0)
                {
                    sb.AppendLine("WARNING: the archive may not work in the game. Examples:");
                    foreach (string example in integrity.Examples)
                        sb.AppendLine($"  {example}");
                }
                sb.AppendLine();

                if (!flattened)
                    AppendInstalledMods(sb, archiveSet, modName);

                if (multiPackage != null)
                {
                    sb.AppendLine("Sources in the order they are applied (lowest priority first, so the last one wins on shared resources), with how many resources each provides and how many of them end up used:");
                    foreach ((string packageName, int provided, int used) in multiPackage.GetResourceCountsPerSource())
                        sb.AppendLine($"  {packageName}\tprovides {provided}\tused {used}");
                    sb.AppendLine();

                    List<(ResourceKey Key, IReadOnlyList<string> Providers)> conflicts = multiPackage.GetConflictingResources().OrderBy(c => c.Key.Type).ThenBy(c => c.Key.Id).ToList();
                    sb.AppendLine($"Resources provided by more than one source: {conflicts.Count}");
                    foreach (IGrouping<string, (ResourceKey Key, IReadOnlyList<string> Providers)> winnerGroup in conflicts.GroupBy(c => c.Providers[c.Providers.Count - 1]).OrderBy(g => g.Key))
                        sb.AppendLine($"  {winnerGroup.Key} won {winnerGroup.Count()}");
                    foreach ((ResourceKey key, IReadOnlyList<string> providers) in conflicts)
                        sb.AppendLine($"  {key}\twinner: {providers[providers.Count - 1]}\toverridden: {string.Join(", ", providers.Take(providers.Count - 1))}");
                    sb.AppendLine();

                    List<string> applyOrder = multiPackage.PackageNames.ToList();
                    Func<ResourceKey, string> sourceOf = k => multiPackage.GetWinnerName(k) ?? "referenced game resource (not from a mod)";

                    sb.AppendLine("Where each source's resources were written (only the resources that won):");
                    foreach (IGrouping<string, (ResourceKey Key, int Part, long Offset, long Length)> sourceGroup in report.Written.GroupBy(w => sourceOf(w.Key)).OrderBy(g => { int orderIdx = applyOrder.IndexOf(g.Key); return orderIdx < 0 ? int.MaxValue : orderIdx; }))
                    {
                        string perPart = string.Join(", ", sourceGroup.GroupBy(w => w.Part).OrderBy(g => g.Key).Select(g => $"part {g.Key}: {g.Count()} ({g.Sum(w => w.Length) / 1048576.0:F1} MB)"));
                        sb.AppendLine($"  {sourceGroup.Key}: {sourceGroup.Count()} resources, {sourceGroup.Sum(w => w.Length) / 1048576.0:F1} MB; {perPart}");
                    }
                    sb.AppendLine();

                    sb.AppendLine("Contents of each archive part:");
                    foreach (IGrouping<int, (ResourceKey Key, int Part, long Offset, long Length)> contentPart in report.Written.GroupBy(w => w.Part).OrderBy(g => g.Key))
                    {
                        string perSource = string.Join(", ", contentPart.GroupBy(w => sourceOf(w.Key)).Select(g => $"{g.Key}: {g.Count()} ({g.Sum(w => w.Length) / 1048576.0:F1} MB)"));
                        sb.AppendLine($"  part {contentPart.Key}: {perSource}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("Installed resources per archive part:");
                foreach (IGrouping<int, (ResourceKey Key, int Part, long Offset, long Length)> partGroup in report.Written.GroupBy(x => x.Part).OrderBy(x => x.Key))
                    sb.AppendLine($"  part {partGroup.Key}: {partGroup.Count()} resources, {partGroup.Sum(x => x.Length) / 1048576.0:F1} MB");
                sb.AppendLine();

                if (unusedResourceKeys.Count > 0)
                {
                    sb.AppendLine("Skipped (not used by any game resource collection); cache details for the first 40:");
                    int unusedIdx = 0;
                    foreach (ResourceKey key in unusedResourceKeys)
                    {
                        if (unusedIdx++ < 40)
                            sb.AppendLine($"  {key} -> {resourceUsageCache.DescribeResourceUsages(archiveSet, key)}");
                        else
                            sb.AppendLine($"  {key}");
                    }
                    sb.AppendLine();
                }

                if (missingData.Count > 0)
                {
                    sb.AppendLine("Skipped (no data could be read from the mod):");
                    foreach (ResourceKey key in missingData)
                        sb.AppendLine($"  {key}");
                    sb.AppendLine();
                }

                sb.AppendLine(multiPackage != null ? "Installed resources (resource, part, offset, length in bytes, source):" : "Installed resources (resource, part, offset, length in bytes):");
                foreach ((ResourceKey key, int part, long offset, long length) in report.Written)
                {
                    string source = multiPackage != null ? "\t" + (multiPackage.GetWinnerName(key) ?? "referenced game resource") : "";
                    sb.AppendLine($"  {key}\tpart {part}\t0x{offset:X}\t{length}{source}");
                }
                sb.AppendLine();

                LogFiles.AppendReport(sb.ToString());
            }
            catch
            {
                // The report is informational only and must never fail an install
            }
        }

        private uint AddResourcesToArchive(
            Archive archive,
            ModPackage modPackage,
            ModVariation? modVariation,
            Dictionary<ResourceKey, List<ResourceCollectionItem>> modResourceCollectionItems,
            InstallReport report,
            ITaskProgress? progress,
            CancellationToken cancellationToken)
        {
            int resourceIdx = 0;
            uint decompressionOffset = 0;
            foreach ((ResourceKey modResourceKey, List<ResourceCollectionItem> collectionItems) in modResourceCollectionItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                resourceIdx++;
                progress?.Report((float)resourceIdx / modResourceCollectionItems.Count);

                Stream? modResourceStream = modVariation?.OpenResource(modResourceKey) ?? modPackage.OpenResource(modResourceKey);
                if (modResourceStream == null)
                {
                    report.NoData.Add(modResourceKey);
                    continue;
                }

                try
                {
                    foreach (ResourceCollectionItem item in collectionItems)
                    {
                        if (item.Collection == null)
                            throw new InvalidOperationException("The resource collection (.drm file) this resource belongs to could not be opened. It may be missing from the game's .list file or failed to parse.");
                    }

                    uint? refDefinitionsSize = null;
                    if (MightHaveRefDefinitions(modResourceKey))
                    {
                        ResourceDescriptor? existingResource = collectionItems[0].ResourceIndex >= 0 ? collectionItems[0].Collection.Resources[collectionItems[0].ResourceIndex] : null;
                        if (existingResource == null || existingResource.RefDefinitionsSize > 0)
                            refDefinitionsSize = (uint)GetResourceRefDefinitionsSize(modPackage, modVariation, modResourceKey);
                    }

                    ArchiveBlobDescriptor newResourceBlob = archive.AddResource(modResourceStream);
                    report.Written.Add((modResourceKey, (int)newResourceBlob.ArchivePart, (long)newResourceBlob.Offset, (long)newResourceBlob.Length));
                    ResourceDescriptor newResourceDesc =
                        new ResourceDescriptor(
                            modResourceKey.Type,
                            modResourceKey.SubType,
                            modResourceKey.Id,
                            modResourceKey.Locale,
                            newResourceBlob.ArchiveId,
                            newResourceBlob.ArchiveSubId,
                            newResourceBlob.ArchivePart,
                            newResourceBlob.Offset,
                            newResourceBlob.Length,
                            decompressionOffset,
                            refDefinitionsSize,
                            (uint)modResourceStream.Length - (refDefinitionsSize ?? 0)
                        );
                    foreach (ResourceCollectionItem collectionItem in collectionItems)
                    {
                        if (collectionItem.ResourceIndex < 0)
                            collectionItem.Collection.Add(newResourceDesc);
                        else
                            collectionItem.Collection.Set(collectionItem.ResourceIndex, newResourceDesc);
                    }

                    decompressionOffset += (uint)modResourceStream.Length;
                    decompressionOffset = (decompressionOffset + 0xF) & ~0xFu;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed while adding resource {modResourceKey} (#{resourceIdx} of {modResourceCollectionItems.Count}); archive currently has {archive.NumParts} part(s)", ex);
                }
                finally
                {
                    modResourceStream.Close();
                }
            }

            return decompressionOffset;
        }
        
        private int GetResourceRefDefinitionsSize(ModPackage modPackage, ModVariation? modVariation, ResourceKey resourceKey)
        {
            using Stream stream = modVariation?.OpenResource(resourceKey) ?? modPackage.OpenResource(resourceKey)!;
            ResourceReader reader = ResourceReader.Create(stream, _archiveSet.Game);
            return reader.RefDefinitionsSize;
        }

        private static void AddFilesToArchives(Dictionary<ulong, Archive> archives, ModPackage modPackage, ModVariation? modVariation, IEnumerable<ResourceCollection> resourceCollections)
        {
            SortedDictionary<ArchiveFileKey, object> files = new();

            foreach (ArchiveFileKey fileKey in modPackage.Files)
            {
                files.Add(fileKey, modPackage);
            }

            if (modVariation != null)
            {
                foreach (ArchiveFileKey fileKey in modVariation.Files)
                {
                    files.Add(fileKey, modVariation);
                }
            }

            foreach (ResourceCollection resourceCollection in resourceCollections)
            {
                files.Add(new ArchiveFileKey(resourceCollection.NameHash, resourceCollection.Locale), resourceCollection);
            }

            foreach ((ArchiveFileKey fileKey, object value) in files)
            {
                switch (value)
                {
                    case ModPackage _:
                        AddFileToArchive(archives, fileKey, modPackage.OpenFile(fileKey));
                        break;

                    case ModVariation _:
                        AddFileToArchive(archives, fileKey, modVariation!.OpenFile(fileKey));
                        break;

                    case ResourceCollection resourceCollection:
                        MemoryStream collectionStream = new();
                        resourceCollection.Write(collectionStream);
                        collectionStream.TryGetBuffer(out ArraySegment<byte> collectionData);
                        archives[resourceCollection.Locale].AddFile(resourceCollection.NameHash, resourceCollection.Locale, collectionData);
                        break;
                }
            }
        }

        private static void AddFileToArchive(Dictionary<ulong, Archive> archives, ArchiveFileKey fileKey, Stream? stream)
        {
            if (stream == null)
                return;

            byte[] data = new byte[stream.Length];
            stream.Read(data, 0, data.Length);
            stream.Close();

            (archives.GetValueOrDefault(fileKey.Locale) ?? archives[0xFFFFFFFFFFFFFFFF]).AddFile(fileKey, data);
        }

        private static bool MightHaveRefDefinitions(ResourceKey resourceKey)
        {
            if (resourceKey.Type == ResourceType.Texture ||
                resourceKey.Type == ResourceType.ShaderLib ||
                resourceKey.Type == ResourceType.SoundBank)
                return false;

            return true;
        }
    }
}
