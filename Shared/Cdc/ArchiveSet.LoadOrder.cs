using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TrRebootTools.Shared.Serialization;
using TrRebootTools.Shared.Util;

namespace TrRebootTools.Shared.Cdc
{
    public enum LoadOrderStatus
    {
        NotUsed,
        NotNeeded,
        UpToDate,
        OutOfDate
    }

    public record LoadOrderInfo(LoadOrderStatus Status, int NumFiles, DateTime? BuildTime, string? LastError);

    public record ModDependency(string ModName, string ProviderModName, int NumFiles, int NumFilesNotInGame);

    public abstract partial class ArchiveSet
    {
        public const string LoadOrderArchivePrefix = ModArchivePrefix + "_load_order.";

        private const string LoadOrderModLabel = "Load order (Mod Manager 1.13.0) - remove this if using any version before 1.13.0";
        private const string LoadOrderMarker = "generated:load_order";
        private const string LoadOrderFingerprintPrefix = "built-from:";

        private const int LoadOrderFormatVersion = 1;

        private readonly HashSet<int> _loadOrderArchiveIds = [];
        private ArchiveMetaData? _loadOrderMetaData;
        private int _loadOrderFileCount;
        private int? _lastLoadOrderArchiveId;
        private int _loadOrderSuspendCount;
        private string? _loadOrderError;
        private readonly Dictionary<(Archive, int, uint), List<ResourceKey>> _resourceReferences = new();

        private readonly record struct CollectionRow(ResourceCollection Collection, int Index)
        {
            public ResourceDescriptor Resource => Collection.Resources[Index];
        }

        private readonly record struct ModCollection(ArchiveFileDescriptor File, ResourceCollection Collection, bool IsUnplacedList);

        protected virtual bool UsesLoadOrderArchive => false;

        private int LoadOrderVersion => _loadOrderMetaData?.Version ?? 0;

        private static bool IsLoadOrderFile(string filePath)
        {
            return Path.GetFileName(filePath).StartsWith(LoadOrderArchivePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadLoadOrderMetaData(string nfoFilePath)
        {
            try
            {
                _loadOrderMetaData = ArchiveMetaData.Load(nfoFilePath);
            }
            catch (Exception)
            {
                _loadOrderMetaData = null;
            }
        }

        private void NoteLoadOrderArchive(int id, int numFiles)
        {
            _loadOrderArchiveIds.Add(id);
            _loadOrderFileCount += numFiles;
        }

        public IDisposable SuspendLoadOrderUpdates()
        {
            lock (_lock)
            {
                _loadOrderSuspendCount++;
            }
            return new LoadOrderSuspension(this);
        }

        private sealed class LoadOrderSuspension : IDisposable
        {
            private ArchiveSet? _archiveSet;

            public LoadOrderSuspension(ArchiveSet archiveSet)
            {
                _archiveSet = archiveSet;
            }

            public void Dispose()
            {
                ArchiveSet? archiveSet = Interlocked.Exchange(ref _archiveSet, null);
                if (archiveSet == null)
                    return;

                lock (archiveSet._lock)
                {
                    archiveSet._loadOrderSuspendCount--;
                }
            }
        }

        public void UpdateLoadOrder(ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            if (!UsesLoadOrderArchive)
                return;

            lock (_lock)
            {
                try
                {
                    RebuildLoadOrder(gameResourceUsageCache, progress, cancellationToken);
                }
                finally
                {
                    CloseStreams();
                    progress?.End();
                }
            }
        }

        public void DeleteLoadOrder()
        {
            if (!UsesLoadOrderArchive)
                return;

            lock (_lock)
            {
                DeleteLoadOrderFiles();
            }
        }

        public LoadOrderInfo GetLoadOrderInfo()
        {
            lock (_lock)
            {
                if (!UsesLoadOrderArchive)
                    return new LoadOrderInfo(LoadOrderStatus.NotUsed, 0, null, null);

                List<Archive> sortedArchives = GetSortedArchives();
                ArchiveMetaData? metaData = _loadOrderMetaData;
                bool exists = metaData != null || _loadOrderArchiveIds.Count > 0;
                if (!sortedArchives.Any(a => a.ModName != null))
                    return new LoadOrderInfo(exists ? LoadOrderStatus.OutOfDate : LoadOrderStatus.NotNeeded, 0, null, null);

                if (metaData == null || !metaData.Enabled || !_loadOrderArchiveIds.Contains(metaData.DlcIndex))
                    return new LoadOrderInfo(LoadOrderStatus.OutOfDate, 0, null, _loadOrderError);

                string? builtFrom = metaData.CustomEntries
                                            .FirstOrDefault(c => c.StartsWith(LoadOrderFingerprintPrefix))
                                            ?.Substring(LoadOrderFingerprintPrefix.Length);
                int highestVersion = _archives.Values.Concat(_duplicateArchives).Max(a => a.MetaData?.Version ?? 0);
                bool upToDate = builtFrom == ComputeLoadOrderFingerprint(sortedArchives) && metaData.Version > highestVersion;

                DateTime? buildTime = File.Exists(metaData.FilePath) ? File.GetLastWriteTime(metaData.FilePath) : null;
                return new LoadOrderInfo(upToDate ? LoadOrderStatus.UpToDate : LoadOrderStatus.OutOfDate, _loadOrderFileCount, buildTime, _loadOrderError);
            }
        }

        private void RebuildLoadOrder(ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            bool existed = _loadOrderMetaData != null || _loadOrderArchiveIds.Count > 0;
            DeleteLoadOrderFiles();
            _loadOrderError = null;

            List<Archive> sortedArchives = GetSortedArchives();
            if (!sortedArchives.Any(a => a.ModName != null))
            {
                if (existed)
                    LogFiles.LogEvent("Load order removed, as no mods are enabled");

                return;
            }

            progress?.Begin("Updating the load order...");
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<string> writtenFilePaths = [];
            try
            {
                (int numFiles, int numFilledIn, int numReferenced) = BuildLoadOrder(sortedArchives, gameResourceUsageCache, writtenFilePaths, progress, cancellationToken);
                int numMods = sortedArchives.Where(a => a.ModName != null).Select(a => a.Id).Distinct().Count();
                LogFiles.LogEvent(
                    $"Load order rebuilt: {numFiles} merged game file(s) for {numMods} enabled mod(s), archive number {_lastLoadOrderArchiveId}. " +
                    $"Entries filled in that the winning mod's copy didn't have: {numFilledIn}. " +
                    $"Entries added because resources in use refer to them: {numReferenced} ({LogFiles.FormatDuration(stopwatch.Elapsed)})"
                );
            }
            catch (Exception ex)
            {
                foreach (string filePath in writtenFilePaths)
                {
                    TryDeleteFile(filePath);
                }
                _loadOrderArchiveIds.Clear();
                _loadOrderMetaData = null;
                _loadOrderFileCount = 0;

                if (ex is OperationCanceledException)
                    throw;

                _loadOrderError = ex.Message;
                throw new InvalidOperationException(
                    $"The load order (the merged copies of the game files that several mods change) could not be built: {ex.Message}\n\n" +
                    "The manager tries again at the next change or when it starts.",
                    ex
                );
            }
        }

        private (int NumFiles, int NumFilledIn, int NumReferenced) BuildLoadOrder(
            List<Archive> sortedArchives,
            ResourceUsageCache gameResourceUsageCache,
            List<string> writtenFilePaths,
            ITaskProgress? progress,
            CancellationToken cancellationToken)
        {
            Dictionary<ArchiveFileKey, ArchiveFileDescriptor> gameFiles = new();
            foreach (Archive archive in sortedArchives.Where(a => a.ModName == null))
            {
                foreach (ArchiveFileDescriptor file in archive.Files)
                {
                    gameFiles[file] = file;
                }
            }

            Dictionary<ResourceKey, ResourceDescriptor> providers = new();
            Dictionary<ArchiveFileKey, List<(Archive Archive, ArchiveFileDescriptor File)>> modCopies = new();
            foreach (Archive archive in sortedArchives.Where(a => a.ModName != null))
            {
                foreach (ModCollection modCollection in EnumerateModCollections(archive, cancellationToken))
                {
                    foreach (ResourceDescriptor resource in modCollection.Collection.Resources)
                    {
                        if (resource.ArchiveId == archive.Id && resource.Enabled)
                            providers[resource] = resource;
                    }

                    ArchiveFileKey fileKey = modCollection.File;
                    if (!modCollection.IsUnplacedList && gameFiles.ContainsKey(fileKey))
                        modCopies.GetOrAdd(fileKey, () => []).Add((archive, modCollection.File));
                }
            }

            List<ArchiveFileKey> fileKeys = modCopies.Keys.OrderBy(k => k).ToList();

            int id = FindFreeLoadOrderArchiveId();
            ArchiveMetaData metaData = CreateLoadOrderMetaData(id, sortedArchives);
            Dictionary<string, int> maxFilesBySuffix = new() { { "", 0 } };
            foreach (ArchiveFileKey fileKey in fileKeys)
            {
                string suffix = MakeLocaleSuffix(fileKey.Locale);
                maxFilesBySuffix[suffix] = maxFilesBySuffix.GetValueOrDefault(suffix) + 1;
            }

            Dictionary<string, (Archive Archive, string FileName)> archivesBySuffix = new();
            SpecMasksToc toc = new();
            CollectionMerger merger = new(this, gameResourceUsageCache, gameFiles, providers);
            int numFiles = 0;
            try
            {
                int subId = 0;
                foreach (string suffix in maxFilesBySuffix.Keys.OrderBy(s => s.Length > 0).ThenBy(s => s, StringComparer.Ordinal).ToList())
                {
                    string archiveFileName = $"{LoadOrderArchivePrefix}000{suffix}.000.tiger";
                    string archiveFilePath = Path.Combine(FolderPath, archiveFileName);
                    writtenFilePaths.Add(archiveFilePath);
                    archivesBySuffix[suffix] = (Archive.Create(archiveFilePath, id, subId, metaData, maxFilesBySuffix[suffix], Game), archiveFileName);
                    subId++;
                }
                toc.Entries.TryAdd(0xFFFFFFFFFFFFFFFF, archivesBySuffix[""].FileName);

                for (int i = 0; i < fileKeys.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report((float)i / fileKeys.Count);

                    ResourceCollection? merged = merger.Merge(gameFiles[fileKeys[i]], modCopies[fileKeys[i]]);
                    if (merged == null)
                        continue;

                    (Archive archive, string archiveFileName) = archivesBySuffix[MakeLocaleSuffix(merged.Locale)];
                    using MemoryStream stream = new();
                    merged.Write(stream);
                    stream.TryGetBuffer(out ArraySegment<byte> data);
                    archive.AddFile(merged.NameHash, merged.Locale, data);
                    toc.Entries.TryAdd(merged.Locale, archiveFileName);
                    numFiles++;
                }
            }
            finally
            {
                foreach ((Archive Archive, string FileName) entry in archivesBySuffix.Values)
                {
                    for (int part = 1; part < entry.Archive.NumParts; part++)
                    {
                        writtenFilePaths.Add(entry.Archive.GetPartFilePath(part));
                    }
                    entry.Archive.Dispose();
                }
            }

            if (toc.Entries.Count > 1 && RequiresSpecMaskFiles)
            {
                string specMasksFilePath = GetLoadOrderSpecMasksFilePath();
                writtenFilePaths.Add(specMasksFilePath);
                toc.Write(specMasksFilePath);
            }

            writtenFilePaths.Add(metaData.FilePath);
            metaData.Save();

            _loadOrderArchiveIds.Add(id);
            _lastLoadOrderArchiveId = id;
            _loadOrderMetaData = metaData;
            _loadOrderFileCount = numFiles;
            return (numFiles, merger.NumFilledInEntries, merger.NumReferencedEntries);
        }

        private ArchiveMetaData CreateLoadOrderMetaData(int id, List<Archive> sortedArchives)
        {
            int version = _archives.Values.Concat(_duplicateArchives).Max(a => a.MetaData?.Version ?? 0) + 1;
            int gameId = _archives.Values.Select(a => a.MetaData?.GameId ?? 0).FirstOrDefault(i => i > 0);
            string? steamId = _archives.Values
                                       .SelectMany(a => (IEnumerable<string>?)a.MetaData?.CustomEntries ?? [])
                                       .FirstOrDefault(c => c.StartsWith("steamID:"));

            string nfoFilePath = Path.Combine(FolderPath, LoadOrderArchivePrefix + "000.000.nfo");
            ArchiveMetaData metaData = ArchiveMetaData.Create(nfoFilePath, gameId, version, id, id, false);
            if (steamId != null)
                metaData.CustomEntries.Add(steamId);

            metaData.CustomEntries.Add("mod:" + LoadOrderModLabel);
            metaData.CustomEntries.Add(LoadOrderMarker);
            metaData.CustomEntries.Add(LoadOrderFingerprintPrefix + ComputeLoadOrderFingerprint(sortedArchives));
            return metaData;
        }

        private void DeleteLoadOrderFiles()
        {
            List<string> filePaths = Directory.EnumerateFiles(FolderPath, LoadOrderArchivePrefix + "*")
                                              .OrderBy(p => p.Contains(".nfo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                              .ToList();
            filePaths.Add(GetLoadOrderSpecMasksFilePath());
            foreach (string filePath in filePaths)
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }

            if (_loadOrderArchiveIds.Count > 0)
                _lastLoadOrderArchiveId = _loadOrderArchiveIds.Min();

            _loadOrderArchiveIds.Clear();
            _loadOrderMetaData = null;
            _loadOrderFileCount = 0;
        }

        private string GetLoadOrderSpecMasksFilePath()
        {
            return SpecMasksToc.GetFilePathForArchive(Path.Combine(FolderPath, LoadOrderArchivePrefix + "000.000.tiger"));
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private int FindFreeLoadOrderArchiveId()
        {
            HashSet<int> usedIds = GetUsedArchiveIds();
            int? id = FindFreeArchiveId(usedIds);
            if (id == null)
                throw new InvalidOperationException("There is no free archive number left for the load order. Removing a mod you no longer use frees one up.");

            int highestGameId = GetHighestGameArchiveId();
            if (_lastLoadOrderArchiveId is int lastId &&
                IsFreeArchiveId(lastId, usedIds) &&
                (lastId > highestGameId) == (id.Value > highestGameId))
            {
                return lastId;
            }

            return id.Value;
        }

        private string ComputeLoadOrderFingerprint(List<Archive> sortedArchives)
        {
            StringBuilder text = new();
            text.Append($"format {LoadOrderFormatVersion}\n");

            foreach (Archive archive in sortedArchives.Where(a => a.ModName == null)
                                                      .OrderBy(a => Path.GetFileName(a.BaseFilePath), StringComparer.OrdinalIgnoreCase)
                                                      .ThenBy(a => a.Id)
                                                      .ThenBy(a => a.SubId))
            {
                AppendArchiveFingerprint(text, "game", archive);
            }
            foreach (Archive archive in sortedArchives.Where(a => a.ModName != null))
            {
                AppendArchiveFingerprint(text, "mod " + archive.ModName, archive);
            }

            ulong hash = 0xCBF29CE484222325;
            foreach (char c in text.ToString())
            {
                hash = (hash ^ c) * 0x100000001B3;
            }
            return hash.ToString("x16");
        }

        private static void AppendArchiveFingerprint(StringBuilder text, string kind, Archive archive)
        {
            text.Append($"{kind}|{Path.GetFileName(archive.BaseFilePath)}|{archive.Id}|{archive.SubId}|{archive.MetaData?.Version}|{archive.NumParts}");
            for (int part = 0; part < archive.NumParts; part++)
            {
                text.Append('|').Append(GetFileLength(archive.GetPartFilePath(part)));
            }
            text.Append('\n');
        }

        private static long GetFileLength(string filePath)
        {
            try
            {
                return new FileInfo(filePath).Length;
            }
            catch (IOException)
            {
                return -1;
            }
            catch (UnauthorizedAccessException)
            {
                return -1;
            }
        }

        private IEnumerable<ModCollection> EnumerateModCollections(Archive archive, CancellationToken cancellationToken)
        {
            foreach (ArchiveFileDescriptor file in archive.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (UnplacedResourceList.IsListFile(file, Game))
                {
                    ResourceCollection? list = UnplacedResourceList.Open(archive, file, Game);
                    if (list != null)
                        yield return new ModCollection(file, list, true);

                    continue;
                }

                ResourceCollection? collection = archive.GetResourceCollection(file);
                if (collection != null)
                    yield return new ModCollection(file, collection, false);
            }
        }

        private ResourceCollection? TryGetResourceCollection(ArchiveFileDescriptor file)
        {
            Archive? archive = _archives.GetValueOrDefault((file.ArchiveId, file.ArchiveSubId));
            if (archive == null)
                return null;

            try
            {
                return archive.GetResourceCollection(file);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool MightReferToOtherResources(ResourceType type)
        {
            return type != ResourceType.Texture && type != ResourceType.ShaderLib && type != ResourceType.SoundBank;
        }

        private List<ResourceKey> GetResourceReferences(ResourceDescriptor resource)
        {
            if (!MightReferToOtherResources(resource.Type) || resource.RefDefinitionsSize == 0)
                return [];

            Archive? archive = _archives.GetValueOrDefault((resource.ArchiveId, resource.ArchiveSubId));
            if (archive == null)
                return [];

            (Archive, int, uint) location = (archive, resource.ArchivePart, resource.Offset);
            if (_resourceReferences.TryGetValue(location, out List<ResourceKey>? references))
                return references;

            try
            {
                using Stream stream = archive.OpenResource(resource);
                using MemoryStream memStream = new();
                stream.CopyTo(memStream);
                memStream.Position = 0;
                references = ResourceReader.Create(resource, memStream, Game)
                                           .ExternalRefs
                                           .Where(r => r.ExternalResource != null)
                                           .Select(r => r.ExternalResource!.Value)
                                           .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                references = [];
            }

            _resourceReferences[location] = references;
            return references;
        }

        private sealed class CollectionMerger
        {
            private const int MaxCachedGameCollections = 2000;

            private readonly ArchiveSet _archiveSet;
            private readonly ResourceUsageCache _gameResourceUsageCache;
            private readonly Dictionary<ArchiveFileKey, ArchiveFileDescriptor> _gameFiles;
            private readonly Dictionary<ResourceKey, ResourceDescriptor> _providers;
            private readonly Dictionary<(ResourceType, int), List<ResourceDescriptor>> _providersByTypeAndId;
            private readonly HashSet<int> _gameArchiveIds;
            private readonly HashSet<int> _modArchiveIds;
            private readonly Dictionary<ArchiveFileKey, ResourceCollection?> _gameCollections = new();
            private readonly Dictionary<ResourceKey, CollectionRow?> _gameVersions = new();

            public CollectionMerger(
                ArchiveSet archiveSet,
                ResourceUsageCache gameResourceUsageCache,
                Dictionary<ArchiveFileKey, ArchiveFileDescriptor> gameFiles,
                Dictionary<ResourceKey, ResourceDescriptor> providers)
            {
                _archiveSet = archiveSet;
                _gameResourceUsageCache = gameResourceUsageCache;
                _gameFiles = gameFiles;
                _providers = providers;
                _providersByTypeAndId = providers.Values
                                                 .GroupBy(r => (r.Type, r.Id))
                                                 .ToDictionary(g => g.Key, g => g.ToList());
                _gameArchiveIds = archiveSet._archives.Values
                                                      .Where(a => a.ModName == null && (a.MetaData?.Enabled ?? false))
                                                      .Select(a => a.Id)
                                                      .ToHashSet();
                _modArchiveIds = archiveSet._archives.Values.Where(a => a.ModName != null).Select(a => a.Id).ToHashSet();
            }

            public int NumFilledInEntries
            {
                get;
                private set;
            }

            public int NumReferencedEntries
            {
                get;
                private set;
            }

            public ResourceCollection? Merge(ArchiveFileDescriptor gameFile, List<(Archive Archive, ArchiveFileDescriptor File)> modCopyFiles)
            {
                List<ResourceCollection> modCopies = [];
                for (int i = modCopyFiles.Count - 1; i >= 0; i--)
                {
                    ResourceCollection? copy = modCopyFiles[i].Archive.GetResourceCollection(modCopyFiles[i].File);
                    if (copy != null)
                        modCopies.Add(copy);
                }

                ResourceCollection? merged = modCopies.Count > 0 ? _archiveSet.TryGetResourceCollection(gameFile) : null;
                if (merged == null)
                    return null;

                int numGameEntries = merged.Resources.Count;
                for (int i = 0; i < numGameEntries; i++)
                {
                    if (_providers.TryGetValue(merged.Resources[i], out ResourceDescriptor? provider))
                        merged.Set(i, provider);
                }

                List<ResourceDescriptor> entries = merged.Resources.ToList();
                HashSet<ResourceKey> presentKeys = entries.Select(r => (ResourceKey)r).ToHashSet();
                HashSet<(ResourceType, int)> presentTypesAndIds = entries.Select(r => (r.Type, r.Id)).ToHashSet();

                HashSet<ResourceKey> winningCopyKeys = modCopies[0].Resources.Select(r => (ResourceKey)r).ToHashSet();
                foreach (ResourceCollection copy in modCopies)
                {
                    IReadOnlyList<ResourceDescriptor> copyResources = copy.Resources;
                    for (int i = 0; i < copyResources.Count; i++)
                    {
                        ResourceDescriptor resource = copyResources[i];
                        if (presentKeys.Contains(resource))
                            continue;

                        if (_providers.TryGetValue(resource, out ResourceDescriptor? provider))
                        {
                            Append(merged, new CollectionRow(copy, i), provider, entries, presentKeys, presentTypesAndIds);
                        }
                        else if (GetGameVersion(resource) is CollectionRow gameRow)
                        {
                            Append(merged, gameRow, gameRow.Resource, entries, presentKeys, presentTypesAndIds);
                        }
                        else if (resource.Enabled && _gameArchiveIds.Contains(resource.ArchiveId))
                        {
                            Append(merged, new CollectionRow(copy, i), resource, entries, presentKeys, presentTypesAndIds);
                        }
                        else
                        {
                            continue;
                        }

                        if (!winningCopyKeys.Contains(resource))
                            NumFilledInEntries++;
                    }
                }

                Queue<int> toCheck = new();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (i >= numGameEntries || _modArchiveIds.Contains(entries[i].ArchiveId))
                        toCheck.Enqueue(i);
                }

                HashSet<(ResourceType, int)> checkedReferences = new();
                while (toCheck.Count > 0)
                {
                    ResourceDescriptor resource = entries[toCheck.Dequeue()];
                    foreach (ResourceKey reference in _archiveSet.GetResourceReferences(resource))
                    {
                        if (resource.SubType == ResourceSubType.Model && reference.Type == ResourceType.Model)
                            continue;

                        (ResourceType, int) typeAndId = (reference.Type, reference.Id);
                        if (presentTypesAndIds.Contains(typeAndId) || !checkedReferences.Add(typeAndId))
                            continue;

                        foreach ((CollectionRow? row, ResourceDescriptor version) in GetSourcesForReference(reference))
                        {
                            if (presentKeys.Contains(version))
                                continue;

                            toCheck.Enqueue(Append(merged, row, version, entries, presentKeys, presentTypesAndIds));
                            NumReferencedEntries++;
                        }
                    }
                }

                return merged;
            }

            private static int Append(
                ResourceCollection merged,
                CollectionRow? row,
                ResourceDescriptor version,
                List<ResourceDescriptor> entries,
                HashSet<ResourceKey> presentKeys,
                HashSet<(ResourceType, int)> presentTypesAndIds)
            {
                if (row != null)
                {
                    int index = merged.Add(row.Value.Collection, row.Value.Index);
                    merged.Set(index, version);
                }
                else
                {
                    merged.Add(version);
                }

                entries.Add(version);
                presentKeys.Add(version);
                presentTypesAndIds.Add((version.Type, version.Id));
                return entries.Count - 1;
            }

            private List<(CollectionRow? Row, ResourceDescriptor Version)> GetSourcesForReference(ResourceKey reference)
            {
                List<(CollectionRow? Row, ResourceDescriptor Version)> sources = [];
                HashSet<ulong> locales = new();
                if (_providersByTypeAndId.TryGetValue((reference.Type, reference.Id), out List<ResourceDescriptor>? modVersions))
                {
                    foreach (ResourceDescriptor modVersion in modVersions)
                    {
                        if (locales.Add(modVersion.Locale))
                            sources.Add((GetGameVersion(modVersion), modVersion));
                    }
                }

                IReadOnlyCollection<ulong> gameLocales = _gameResourceUsageCache.GetResourceLocales(reference.Type, reference.Id) ?? [0xFFFFFFFFFFFFFFFF];
                foreach (ulong locale in gameLocales)
                {
                    if (locales.Contains(locale))
                        continue;

                    CollectionRow? gameRow = GetGameVersion(new ResourceKey(reference.Type, 0, reference.Id, locale));
                    if (gameRow != null)
                    {
                        locales.Add(locale);
                        sources.Add((gameRow, gameRow.Value.Resource));
                    }
                }

                if (reference.Locale != 0xFFFFFFFFFFFFFFFF)
                    sources.RemoveAll(s => s.Version.Locale != reference.Locale);

                return sources;
            }

            private CollectionRow? GetGameVersion(ResourceKey key)
            {
                if (_gameVersions.TryGetValue(key, out CollectionRow? cached))
                    return cached;

                CollectionRow? found = null;
                foreach (ResourceCollectionItemReference usage in _gameResourceUsageCache.GetResourceUsages(_archiveSet, key))
                {
                    if (!_gameFiles.TryGetValue(usage.CollectionReference, out ArchiveFileDescriptor? gameFile))
                        continue;

                    ResourceCollection? collection = GetGameCollection(gameFile);
                    if (collection == null || usage.ResourceIndex >= collection.Resources.Count)
                        continue;

                    ResourceDescriptor resource = collection.Resources[usage.ResourceIndex];
                    if (resource.Enabled && key.Equals((ResourceKey)resource))
                    {
                        found = new CollectionRow(collection, usage.ResourceIndex);
                        break;
                    }
                }

                _gameVersions[key] = found;
                return found;
            }

            private ResourceCollection? GetGameCollection(ArchiveFileDescriptor gameFile)
            {
                ArchiveFileKey fileKey = gameFile;
                if (_gameCollections.TryGetValue(fileKey, out ResourceCollection? collection))
                    return collection;

                if (_gameCollections.Count >= MaxCachedGameCollections)
                {
                    _gameCollections.Clear();
                    _gameVersions.Clear();
                }

                collection = _archiveSet.TryGetResourceCollection(gameFile);
                _gameCollections[fileKey] = collection;
                return collection;
            }
        }

        public List<ModDependency> FindModDependencies(
            ICollection<int> archiveIdsTurningOff,
            ICollection<int> archiveIdsTurningOn,
            ResourceUsageCache gameResourceUsageCache,
            ITaskProgress? progress,
            CancellationToken cancellationToken)
        {
            if (Game is not (CdcGame.Tr2013 or CdcGame.Rise or CdcGame.Shadow))
                return [];

            lock (_lock)
            {
                List<IGrouping<int, Archive>> mods = _archives.Values.Where(a => a.ModName != null && a.MetaData != null).GroupBy(a => a.Id).ToList();
                HashSet<int> turningOff = mods.Where(m => archiveIdsTurningOff.Contains(m.Key) && m.First().MetaData!.Enabled)
                                              .Select(m => m.Key)
                                              .ToHashSet();
                List<IGrouping<int, Archive>> staying = mods.Where(m => !turningOff.Contains(m.Key) && (m.First().MetaData!.Enabled || archiveIdsTurningOn.Contains(m.Key)))
                                                            .ToList();
                if (turningOff.Count == 0 || staying.Count == 0)
                    return [];

                try
                {
                    progress?.Begin("Checking whether other mods use files from these mods...");

                    Dictionary<int, List<ResourceDescriptor>> modResources = new();
                    foreach (IGrouping<int, Archive> mod in mods.Where(m => turningOff.Contains(m.Key)).Concat(staying))
                    {
                        Dictionary<ResourceKey, ResourceDescriptor> resources = new();
                        foreach (Archive archive in mod)
                        {
                            foreach (ModCollection modCollection in EnumerateModCollections(archive, cancellationToken))
                            {
                                foreach (ResourceDescriptor resource in modCollection.Collection.Resources)
                                {
                                    if (resource.ArchiveId == archive.Id && resource.Enabled)
                                        resources.TryAdd(resource, resource);
                                }
                            }
                        }
                        modResources[mod.Key] = resources.Values.ToList();
                    }

                    HashSet<(ResourceType, int)> providedByStaying = staying.SelectMany(m => modResources[m.Key]).Select(r => (r.Type, r.Id)).ToHashSet();
                    Dictionary<(ResourceType, int), int> providedByTurningOff = new();
                    foreach (int modId in turningOff)
                    {
                        foreach (ResourceDescriptor resource in modResources[modId])
                        {
                            providedByTurningOff.TryAdd((resource.Type, resource.Id), modId);
                        }
                    }

                    Dictionary<(int Mod, int Provider), HashSet<(ResourceType, int)>> usedFiles = new();
                    int numResources = staying.Sum(m => modResources[m.Key].Count);
                    int resourceIdx = 0;
                    foreach (IGrouping<int, Archive> mod in staying)
                    {
                        foreach (ResourceDescriptor resource in modResources[mod.Key])
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            resourceIdx++;
                            progress?.Report((float)resourceIdx / numResources);

                            foreach (ResourceKey reference in GetResourceReferences(resource))
                            {
                                if (resource.SubType == ResourceSubType.Model && reference.Type == ResourceType.Model)
                                    continue;

                                (ResourceType, int) typeAndId = (reference.Type, reference.Id);
                                if (providedByStaying.Contains(typeAndId) || !providedByTurningOff.TryGetValue(typeAndId, out int providerId))
                                    continue;

                                usedFiles.GetOrAdd((mod.Key, providerId), () => []).Add(typeAndId);
                            }
                        }
                    }

                    Dictionary<int, string> modNames = mods.ToDictionary(m => m.Key, m => m.First().ModName!);
                    List<ModDependency> dependencies = [];
                    foreach (KeyValuePair<(int Mod, int Provider), HashSet<(ResourceType, int)>> used in usedFiles)
                    {
                        int numFiles = 0;
                        int numNotInGame = 0;
                        foreach ((ResourceType type, int id) in used.Value)
                        {
                            bool inGame = gameResourceUsageCache.GetResourceUsages(this, type, id).Any();
                            if (inGame && type == ResourceType.Texture)
                                continue;

                            numFiles++;
                            if (!inGame)
                                numNotInGame++;
                        }

                        if (numFiles > 0)
                            dependencies.Add(new ModDependency(modNames[used.Key.Mod], modNames[used.Key.Provider], numFiles, numNotInGame));
                    }

                    return dependencies.OrderBy(d => d.ModName, StringComparer.OrdinalIgnoreCase)
                                       .ThenBy(d => d.ProviderModName, StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                }
                finally
                {
                    CloseStreams();
                    progress?.End();
                }
            }
        }
    }
}
