using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using TrRebootTools.Shared.Cdc.Avengers;
using TrRebootTools.Shared.Cdc.Rise;
using TrRebootTools.Shared.Cdc.Shadow;
using TrRebootTools.Shared.Cdc.Tr2013;
using TrRebootTools.Shared.Util;

namespace TrRebootTools.Shared.Cdc
{
    public abstract class ArchiveSet : IDisposable
    {
        public const string ModArchivePrefix = "bigfile._mod.";
        public const string OriginalGameArchivePrefix = "bigfile._orig.";

        private readonly object _lock = new();
        private readonly Dictionary<(int Id, int SubId), Archive> _archives = new();
        private readonly List<Archive> _duplicateArchives = [];
        private readonly Dictionary<ArchiveFileKey, ArchiveFileDescriptor> _files = new();

        public static ArchiveSet Open(string folderPath, bool includeGame, bool includeMods, CdcGame game)
        {
            return game switch
            {
                CdcGame.Tr2013   => new Tr2013ArchiveSet(folderPath, includeGame, includeMods),
                CdcGame.Rise     => new RiseArchiveSet(folderPath, includeGame, includeMods),
                CdcGame.Shadow   => new ShadowArchiveSet(folderPath, includeGame, includeMods),
                CdcGame.Avengers => new AvengersArchiveSet(folderPath, includeGame, includeMods),
                _ => throw new NotSupportedException()
            };
        }

        protected ArchiveSet(string folderPath, bool includeGame, bool includeMods)
        {
            FolderPath = folderPath;

            EnsureFlattenedModArchiveBackup();

            Dictionary<int, ArchiveMetaData> metaDatas = new();
            foreach (string nfoFilePath in Directory.EnumerateFiles(folderPath))
            {
                if (!nfoFilePath.EndsWith(".nfo") && !nfoFilePath.EndsWith(".nfo.disabled"))
                    continue;

                ArchiveMetaData metaData = ArchiveMetaData.Load(nfoFilePath);
                metaDatas[metaData.DlcIndex] = metaData;
            }

            foreach (string archiveFilePath in Directory.EnumerateFiles(folderPath, "*.000.tiger", SearchOption.AllDirectories))
            {
                if (!ShouldLoad(archiveFilePath, includeGame, includeMods))
                    continue;

                bool isModArchive = Path.GetFileName(archiveFilePath).StartsWith(ModArchivePrefix);
                ArchiveMetaData? metaData;
                try
                {
                    using Stream stream = Archive.InstantiateArchive(archiveFilePath, null, Game).OpenPart(archiveFilePath, 0, FileMode.Open, FileAccess.Read);
                    using BinaryReader reader = new(stream);
                    var header = reader.ReadStruct<Archive.ArchiveHeader>();
                    metaData = metaDatas?.GetValueOrDefault(header.Id);
                }
                catch when (isModArchive)
                {
                    // The leftover of an install that was interrupted before the archive was complete
                    continue;
                }

                // A mod archive without a .nfo belongs to an install that never finished, so it doesn't count as installed
                if (isModArchive && metaData == null)
                    continue;

                if (metaData != null || !SupportsMetaData)
                    Load(archiveFilePath, metaData);
            }

            foreach (Archive archive in GetSortedArchives())
            {
                foreach (ArchiveFileDescriptor file in archive.Files)
                {
                    _files[file] = file;
                }
            }
        }

        public abstract CdcGame Game { get; }

        protected abstract bool SupportsMetaData { get; }

        public string FolderPath
        {
            get;
        }

        public IReadOnlyCollection<Archive> Archives => _archives.Values;

        public IReadOnlyCollection<Archive> DuplicateArchives => _duplicateArchives;

        public Archive? GetArchive(int id, int subId)
        {
            return _archives.GetValueOrDefault((id, subId));
        }

        public IReadOnlyCollection<ArchiveFileDescriptor> Files => _files.Values;

        private void EnsureFlattenedModArchiveBackup()
        {
            string? archiveFileName = GetActiveFlattenedModArchiveIdentity()?.FileName;
            if (archiveFileName == null)
                return;

            string archiveFilePath = Path.Combine(FolderPath, archiveFileName);
            string backupFilePath = Path.Combine(FolderPath, OriginalGameArchivePrefix + archiveFileName);
            if (File.Exists(archiveFilePath) && !File.Exists(backupFilePath))
                File.Copy(archiveFilePath, backupFilePath);
        }

        private bool ShouldLoad(string archiveFilePath, bool includeGame, bool includeMods)
        {
            string archiveFileName = Path.GetFileName(archiveFilePath);
            if (archiveFileName.StartsWith(ModArchivePrefix) ? !includeMods : !includeGame)
                return false;

            if (archiveFileName.StartsWith(OriginalGameArchivePrefix) &&
                archiveFileName.Substring(OriginalGameArchivePrefix.Length) != GetActiveFlattenedModArchiveIdentity()?.FileName)
            {
                return false;
            }

            if (GetAllFlattenedModArchiveIdentities().Any(i => i.FileName == archiveFileName))
                return false;

            return true;
        }

        private void Load(string archiveFilePath, ArchiveMetaData? metaData)
        {
            Archive archive = Archive.Open(archiveFilePath, metaData, Game);
            var archiveKey = (archive.Id, archive.SubId);
            if (!_archives.ContainsKey(archiveKey))
                _archives.Add(archiveKey, archive);
            else
                _duplicateArchives.Add(archive);
        }

        public Archive CreateFlattenedModArchive(int maxFiles)
        {
            ArchiveIdentity? ident = GetActiveFlattenedModArchiveIdentity();
            if (ident == null)
                throw new InvalidOperationException();

            return Archive.Create(Path.Combine(FolderPath, ident.FileName), ident.Id, 0, null, maxFiles, Game);
        }

        // Mods without a stored priority rank by install order (newest first), below every explicitly numbered mod
        public static int GetModPriority(Archive archive)
        {
            return archive.MetaData?.Priority ?? (1_000_000 - (archive.MetaData?.Version ?? 0));
        }

        private static void SetModPriority(IEnumerable<Archive> modArchives, int priority)
        {
            foreach (Archive archive in modArchives)
            {
                if (archive.MetaData == null)
                    continue;

                archive.MetaData.Priority = priority;
                archive.MetaData.Save();
            }
        }

        // The priority of each mod (by archive ID) for display: the stored numbers, or, while some mods don't have one yet, ranks that
        // match the order they are applied in now (newest install = 1). Nothing is written.
        public Dictionary<int, int> GetEffectiveModPriorities()
        {
            lock (_lock)
            {
                List<IGrouping<int, Archive>> mods = _archives.Values.Where(a => a.ModName != null && a.MetaData != null).GroupBy(a => a.Id).ToList();
                Dictionary<int, int> priorities = new();

                if (mods.All(g => g.First().MetaData!.Priority != null))
                {
                    foreach (IGrouping<int, Archive> mod in mods)
                    {
                        priorities[mod.Key] = mod.First().MetaData!.Priority!.Value;
                    }
                    return priorities;
                }

                int rank = 1;
                foreach (IGrouping<int, Archive> mod in mods.OrderBy(g => GetModPriority(g.First())).ThenByDescending(g => g.First().MetaData!.Version))
                {
                    priorities[mod.Key] = rank++;
                }
                return priorities;
            }
        }

        // Writes an explicit priority to every mod if any of them doesn't have one yet. Only called when a mod is installed.
        public void NormalizeModPriorities()
        {
            lock (_lock)
            {
                List<IGrouping<int, Archive>> mods = _archives.Values.Where(a => a.ModName != null && a.MetaData != null).GroupBy(a => a.Id).ToList();
                if (mods.Count == 0 || mods.All(g => g.First().MetaData!.Priority != null))
                    return;

                Dictionary<int, int> priorities = GetEffectiveModPriorities();
                foreach (IGrouping<int, Archive> mod in mods)
                {
                    SetModPriority(mod, priorities[mod.Key]);
                }
                ReassignModVersions();
            }
        }

        // Rise and Shadow decide which mod wins by the version number in each mod's .nfo (the highest version wins), so the versions
        // have to follow the priorities. The mods' existing versions are handed out again in priority order, which keeps them unique
        // and above the game's own archives. Mods with the same priority keep their current relative order.
        private void ReassignModVersions()
        {
            List<IGrouping<int, Archive>> mods = _archives.Values.Where(a => a.ModName != null && a.MetaData != null).GroupBy(a => a.Id).ToList();
            List<int> versions = mods.Select(g => g.First().MetaData!.Version).OrderBy(v => v).ToList();
            List<IGrouping<int, Archive>> inApplyOrder = mods.OrderByDescending(g => GetModPriority(g.First()))
                                                              .ThenBy(g => g.First().MetaData!.Version)
                                                              .ToList();

            for (int i = 0; i < inApplyOrder.Count; i++)
            {
                foreach (Archive archive in inApplyOrder[i])
                {
                    if (archive.MetaData!.Version == versions[i])
                        continue;

                    archive.MetaData.Version = versions[i];
                    archive.MetaData.Save();
                }
            }
        }

        public void SetModPriorities(IDictionary<int, int> prioritiesByArchiveId, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                try
                {
                    progress?.Begin("Updating mod priorities...");

                    foreach (IGrouping<int, Archive> mod in _archives.Values.Where(a => a.ModName != null).GroupBy(a => a.Id))
                    {
                        if (prioritiesByArchiveId.TryGetValue(mod.Key, out int priority))
                            SetModPriority(mod, priority);
                    }

                    ReassignModVersions();
                    UpdateResourceDescriptors(gameResourceUsageCache, progress, cancellationToken);
                }
                finally
                {
                    CloseStreams();
                    progress?.End();
                }
            }
        }

        // Applies a batch of changes with a single recalculation: mods to enable, mods to disable and (optionally) new priorities.
        public void ApplyModChanges(
            IList<int> archiveIdsToEnable,
            IList<int> archiveIdsToDisable,
            IDictionary<int, int>? prioritiesByArchiveId,
            ResourceUsageCache gameResourceUsageCache,
            ITaskProgress? progress,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                try
                {
                    progress?.Begin("Applying changes...");

                    foreach (int archiveId in archiveIdsToEnable)
                    {
                        Archive archive = _archives[(archiveId, 0)];
                        if (archive.MetaData != null && !archive.MetaData.Enabled)
                            archive.MetaData.Enabled = true;
                    }

                    foreach (int archiveId in archiveIdsToDisable)
                    {
                        Archive archive = _archives[(archiveId, 0)];
                        if (archive.MetaData != null && archive.MetaData.Enabled)
                            archive.MetaData.Enabled = false;
                    }

                    if (prioritiesByArchiveId != null)
                    {
                        foreach (IGrouping<int, Archive> mod in _archives.Values.Where(a => a.ModName != null).GroupBy(a => a.Id))
                        {
                            if (prioritiesByArchiveId.TryGetValue(mod.Key, out int priority))
                                SetModPriority(mod, priority);
                        }
                        ReassignModVersions();
                    }

                    UpdateResourceDescriptors(gameResourceUsageCache, progress, cancellationToken);
                }
                finally
                {
                    CloseStreams();
                    progress?.End();
                }
            }
        }

        // Archive IDs that the game format treats specially and that mod archives must therefore never be given
        protected virtual bool IsReservedArchiveId(int id) => false;

        public Dictionary<ulong, Archive> CreateModArchives(string modName, Dictionary<ulong, int> maxFilesByLocale)
        {
            lock (_lock)
            {
                string simplifiedName = Regex.Replace(modName, @"[^-.\w]", "_").ToLower();
                simplifiedName = Regex.Replace(simplifiedName, @"__+", "_").Trim('_');

                NormalizeModPriorities();

                // A newly installed mod gets priority 1, as it always won over earlier mods before priorities existed.
                // If priority 1 is already taken, the existing mods move down one place so their relative order is kept.
                List<IGrouping<int, Archive>> existingMods = _archives.Values.Where(a => a.ModName != null).GroupBy(a => a.Id).ToList();
                int newModPriority = 1;
                if (existingMods.Count > 0)
                {
                    int highestExistingPriority = existingMods.Min(g => GetModPriority(g.First()));
                    if (highestExistingPriority > 1)
                    {
                        newModPriority = highestExistingPriority - 1;
                    }
                    else
                    {
                        foreach (IGrouping<int, Archive> existingMod in existingMods)
                        {
                            SetModPriority(existingMod, GetModPriority(existingMod.First()) + 1);
                        }
                    }
                }

                int gameId = _archives.Values.Select(a => a.MetaData?.GameId ?? 0).FirstOrDefault(id => id > 0);
                int version = _archives.Values.Max(a => a.MetaData?.Version ?? 0) + 1;
                int id = _archives.Values.Max(a => a.Id) + 1;
                while (IsReservedArchiveId(id))
                    id++;
                string? steamId = _archives.Values
                                           .SelectMany(a => (IEnumerable<string>?)a.MetaData?.CustomEntries ?? [])
                                           .FirstOrDefault(c => c.StartsWith("steamID:"));

                string nfoFilePath = Path.Combine(FolderPath, $"{ModArchivePrefix}{simplifiedName}.000.000.nfo");
                // The .nfo is deliberately not written here. Without it the mod doesn't show as installed, so an install that
                // is interrupted before the archive is complete leaves nothing behind that looks like a mod. The installer saves it at the end.
                ArchiveMetaData metaData = ArchiveMetaData.Create(nfoFilePath, gameId, version, id, id, false);
                if (steamId != null)
                    metaData.CustomEntries.Add(steamId);

                metaData.CustomEntries.Add("mod:" + modName);
                metaData.Priority = newModPriority;

                int subId = 0;

                Dictionary<ulong, Archive> archives = new();
                SpecMasksToc toc = new();
                foreach (var localesOfSuffix in maxFilesByLocale.Keys.GroupBy(MakeLocaleSuffix))
                {
                    string archiveFileName = $"{ModArchivePrefix}{simplifiedName}.000{localesOfSuffix.Key}.000.tiger";
                    int maxFiles = localesOfSuffix.Sum(l => maxFilesByLocale[l]);
                    Archive archive = Archive.Create(Path.Combine(FolderPath, archiveFileName), id, subId, metaData, maxFiles, Game);
                    subId++;

                    foreach (ulong locale in localesOfSuffix)
                    {
                        toc.Entries.Add(locale, archiveFileName);
                        archives.Add(locale, archive);
                    }
                }

                if (toc.Entries.Count > 1 && RequiresSpecMaskFiles)
                    toc.Write(SpecMasksToc.GetFilePathForArchive(Path.ChangeExtension(nfoFilePath, ".tiger")));

                return archives;
            }
        }

        public virtual ICollection<ArchiveIdentity> GetAllFlattenedModArchiveIdentities()
        {
            return [];
        }

        public virtual ArchiveIdentity? GetActiveFlattenedModArchiveIdentity()
        {
            return null;
        }

        protected abstract string MakeLocaleSuffix(ulong locale);

        protected abstract bool RequiresSpecMaskFiles { get; }

        public void Add(Archive archive, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _archives.Add((archive.Id, archive.SubId), archive);
                UpdateResourceDescriptors(gameResourceUsageCache, progress, cancellationToken);
            }
        }

        public void Enable(int archiveId, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            Enable([archiveId], gameResourceUsageCache, progress, cancellationToken);
        }

        public void Enable(IList<int> archiveIds, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                try
                {
                    progress?.Begin($"Enabling mods...");

                    foreach (int archiveId in archiveIds)
                    {
                        Archive archive = _archives[(archiveId, 0)];
                        if (archive.MetaData == null || archive.MetaData.Enabled)
                            continue;

                        archive.MetaData.Enabled = true;
                    }

                    UpdateResourceDescriptors(gameResourceUsageCache, progress, cancellationToken);
                }
                finally
                {
                    CloseStreams();
                    progress?.End();
                }
            }
        }

        public void Disable(int archiveId, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            Disable([archiveId], gameResourceUsageCache, progress, cancellationToken);
        }

        public void Disable(IList<int> archiveIds, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Disable(archiveIds, gameResourceUsageCache, progress, $"Disabling mods...", cancellationToken);
            }
        }

        private void Disable(IList<int> archiveIds, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, string statusText, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                try
                {
                    progress?.Begin(statusText);

                    foreach (int archiveId in archiveIds)
                    {
                        Archive archive = _archives[(archiveId, 0)];
                        if (archive.MetaData == null || !archive.MetaData.Enabled)
                            continue;

                        archive.MetaData.Enabled = false;
                    }

                    UpdateResourceDescriptors(gameResourceUsageCache, progress, cancellationToken);
                }
                finally
                {
                    CloseStreams();
                    progress?.End();
                }
            }
        }

        public void Delete(int archiveId, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            Delete([archiveId], gameResourceUsageCache, progress, cancellationToken);
        }

        public void Delete(IList<int> archiveIds, ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Disable(archiveIds, gameResourceUsageCache, progress, $"Removing mods...", cancellationToken);

                foreach (Archive archive in _archives.Values.Where(a => archiveIds.Contains(a.Id)).Concat(_duplicateArchives.Where(a => archiveIds.Contains(a.Id))).ToList())
                {
                    archive.Delete();
                    archive.Dispose();
                    if (!_duplicateArchives.Remove(archive))
                        _archives.Remove((archive.Id, archive.SubId));
                }
            }
        }

        private void UpdateResourceDescriptors(ResourceUsageCache gameResourceUsageCache, ITaskProgress? progress, CancellationToken cancellationToken)
        {
            List<Archive> sortedArchives = GetSortedArchives();
            int firstModArchiveIdx = sortedArchives.IndexOf(a => a.ModName != null);
            if (firstModArchiveIdx < 0)
                firstModArchiveIdx = sortedArchives.Count;

            _files.Clear();
            foreach (Archive archive in sortedArchives.Take(firstModArchiveIdx))
            {
                foreach (ArchiveFileDescriptor file in archive.Files)
                {
                    _files[file] = file;
                }
            }

            int numTotalFiles = 0;
            foreach (Archive archive in sortedArchives.Skip(firstModArchiveIdx))
            {
                numTotalFiles += archive.Files.Count;
            }

            ResourceUsageCache resourceUsageCache = new(gameResourceUsageCache);

            int numUpdatedFiles = 0;
            HashSet<int> archiveIds = sortedArchives.Select(a => a.Id).ToHashSet();
            Dictionary<ResourceKey, ResourceDescriptor> modResources = new();
            foreach (Archive archive in sortedArchives.Skip(firstModArchiveIdx))
            {
                foreach (ArchiveFileDescriptor file in archive.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    numUpdatedFiles++;
                    progress?.Report((float)numUpdatedFiles / numTotalFiles);

                    ResourceCollection? collection = archive.GetResourceCollection(file);
                    if (collection == null)
                    {
                        _files[file] = file;
                        continue;
                    }

                    bool changesMade = false;
                    for (int resourceIdx = 0; resourceIdx < collection.Resources.Count; resourceIdx++)
                    {
                        ResourceDescriptor resource = collection.Resources[resourceIdx];
                        if (resource.ArchiveId == archive.Id && resource.ArchiveSubId == archive.SubId)
                        {
                            resourceUsageCache.AddResource(this, collection, resourceIdx);
                            modResources[resource] = resource;
                        }
                        else if (modResources.TryGetValue(resource, out ResourceDescriptor? modResource) &&
                                 (resource.ArchiveId != modResource.ArchiveId || resource.ArchiveSubId != modResource.ArchiveSubId))
                        {
                            collection.Set(resourceIdx, modResource);
                            changesMade = true;
                        }
                        else if (!archiveIds.Contains(resource.ArchiveId))
                        {
                            ResourceDescriptor? gameResource = resourceUsageCache.GetResourceDescriptor(this, resource);
                            if (gameResource == null)
                            {
                                gameResource = new ResourceDescriptor(
                                    resource.Type,
                                    resource.SubType,
                                    resource.Id,
                                    resource.Locale,
                                    sortedArchives[0].Id,
                                    sortedArchives[0].SubId,
                                    0,
                                    0,
                                    0,
                                    0,
                                    0,
                                    0
                                ) { Enabled = false };
                            }
                            collection.Set(resourceIdx, gameResource);
                            changesMade = true;
                        }
                    }

                    if (changesMade)
                    {
                        using Stream collectionStream = archive.OpenFile(file);
                        collection.Write(collectionStream);
                    }

                    _files[file] = file;
                }
            }
        }

        public ArchiveFileDescriptor? GetFile(ArchiveFileKey fileId)
        {
            return GetFile(fileId.NameHash, fileId.Locale);
        }

        public ArchiveFileDescriptor? GetFile(ulong nameHash, ulong locale = 0xFFFFFFFFFFFFFFFF)
        {
            return _files.GetValueOrDefault(new ArchiveFileKey(nameHash, locale));
        }

        public ArchiveFileDescriptor? GetFile(string name, ulong locale = 0xFFFFFFFFFFFFFFFF)
        {
            return GetFile(CdcHash.Calculate(name, Game), locale);
        }

        public ResourceCollection? GetResourceCollection(ArchiveFileDescriptor file)
        {
            return _archives[(file.ArchiveId, file.ArchiveSubId)].GetResourceCollection(file);
        }

        public string DescribeResourceCollectionFailure(ArchiveFileDescriptor file)
        {
            return _archives[(file.ArchiveId, file.ArchiveSubId)].DescribeResourceCollectionFailure(file);
        }

        public ResourceCollection? GetResourceCollection(ulong nameHash, ulong locale = 0xFFFFFFFFFFFFFFFF)
        {
            ArchiveFileDescriptor? file = GetFile(nameHash, locale);
            return file != null ? GetResourceCollection(file) : null;
        }

        public ResourceCollection? GetResourceCollection(string name, ulong locale = 0xFFFFFFFFFFFFFFFF)
        {
            ArchiveFileDescriptor? file = GetFile(name, locale);
            return file != null ? GetResourceCollection(file) : null;
        }

        public Stream OpenFile(ArchiveFileDescriptor file)
        {
            return _archives[(file.ArchiveId, file.ArchiveSubId)].OpenFile(file);
        }

        public Stream OpenResource(ResourceDescriptor resource)
        {
            return _archives[(resource.ArchiveId, resource.ArchiveSubId)].OpenResource(resource);
        }

        public Stream? TryOpenResource(ResourceDescriptor resource)
        {
            return _archives.GetValueOrDefault((resource.ArchiveId, resource.ArchiveSubId))?.OpenResource(resource);
        }

        public MemoryStream LoadResource(ResourceDescriptor resource)
        {
            MemoryStream? stream = TryLoadResource(resource);
            if (stream == null)
                throw new FileNotFoundException();

            return stream;
        }

        public MemoryStream? TryLoadResource(ResourceDescriptor resource)
        {
            using Stream? readStream = TryOpenResource(resource);
            if (readStream == null)
                return null;

            MemoryStream memStream = new();
            readStream.CopyTo(memStream);
            memStream.Position = 0;
            return memStream;
        }

        public virtual List<Archive> GetSortedArchives()
        {
            List<Archive> sortedArchives = _archives.Values.Where(a => a.MetaData?.Enabled ?? false).ToList();
            sortedArchives.Sort(CompareArchivePriority);
            return sortedArchives;
        }

        private static int CompareArchivePriority(Archive a, Archive b)
        {
            if (a.MetaData == null || b.MetaData == null)
                return 0;

            // Mods are applied from the lowest priority (largest number) to the highest priority (1), so the highest priority is applied last and wins
            if (a.ModName != null && b.ModName != null)
            {
                int priorityComparison = GetModPriority(b).CompareTo(GetModPriority(a));
                if (priorityComparison != 0)
                    return priorityComparison;
            }

            int comparison = a.MetaData.Version.CompareTo(b.MetaData.Version);
            if (comparison != 0)
                return comparison;

            comparison = b.MetaData.Required.CompareTo(a.MetaData.Required);
            if (comparison != 0)
                return comparison;

            comparison = a.MetaData.PackageId.CompareTo(b.MetaData.PackageId);
            if (comparison != 0)
                return comparison;

            comparison = a.MetaData.Chunk.CompareTo(b.MetaData.Chunk);
            if (comparison != 0)
                return comparison;

            return a.SubId.CompareTo(b.SubId);
        }

        public void CloseStreams()
        {
            foreach (Archive archive in _archives.Values.Concat(_duplicateArchives))
            {
                archive.CloseStreams();
            }
        }

        public void Dispose()
        {
            foreach (Archive archive in _archives.Values.Concat(_duplicateArchives))
            {
                archive.Dispose();
            }
            _archives.Clear();
            _duplicateArchives.Clear();
        }
    }
}
