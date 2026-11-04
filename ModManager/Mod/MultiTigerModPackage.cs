using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrRebootTools.Shared.Cdc;

namespace TrRebootTools.ModManager.Mod
{
    internal class MultiTigerModPackage : ModPackage
    {
        private readonly List<TigerModPackage> _packages = [];
        private readonly Dictionary<ArchiveFileKey, TigerModPackage> _files = new();
        private readonly Dictionary<ResourceKey, TigerModPackage> _resources = new();
        private readonly Dictionary<ResourceKey, List<string>> _resourceProviders = new();

        public MultiTigerModPackage(string name, IEnumerable<TigerModPackage> packages)
        {
            Name = name;

            foreach (TigerModPackage package in packages)
            {
                _packages.Add(package);

                foreach (ArchiveFileKey fileKey in package.Files)
                {
                    _files[fileKey] = package;
                }

                foreach (ResourceKey resourceKey in package.Resources)
                {
                    _resources[resourceKey] = package;

                    if (!_resourceProviders.TryGetValue(resourceKey, out List<string>? providers))
                    {
                        providers = [];
                        _resourceProviders[resourceKey] = providers;
                    }
                    providers.Add(GetPackageDisplayName(package));
                }
            }
        }

        public override string Name
        {
            get;
        }

        // Names of the packages in the order they are applied: lowest priority first, highest priority last
        public IEnumerable<string> PackageNames => _packages.Select(GetPackageDisplayName);

        // For each package in the order it is applied: how many resources it provides, and how many of those the archive actually takes from it
        public IEnumerable<(string Name, int Provided, int Used)> GetResourceCountsPerSource()
        {
            foreach (string name in PackageNames)
            {
                int provided = 0;
                int used = 0;
                foreach (List<string> providers in _resourceProviders.Values)
                {
                    if (!providers.Contains(name))
                        continue;

                    provided++;
                    if (providers[providers.Count - 1] == name)
                        used++;
                }
                yield return (name, provided, used);
            }
        }

        // The number of distinct resources in all packages, how many of them the named package provides, and how many only it provides
        public (int Total, int FromSource, int OnlyFromSource) CountResourcesOf(string name)
        {
            int fromSource = 0;
            int onlyFromSource = 0;
            foreach (List<string> providers in _resourceProviders.Values)
            {
                if (!providers.Contains(name))
                    continue;

                fromSource++;
                if (providers.Count == 1)
                    onlyFromSource++;
            }
            return (_resourceProviders.Count, fromSource, onlyFromSource);
        }

        // The package whose version of the resource is used, or null if no package provides it
        public string? GetWinnerName(ResourceKey key)
        {
            return _resourceProviders.TryGetValue(key, out List<string>? providers) ? providers[providers.Count - 1] : null;
        }

        // Resources provided by more than one package. The last provider listed is the one that wins.
        public IEnumerable<(ResourceKey Key, IReadOnlyList<string> Providers)> GetConflictingResources()
        {
            foreach ((ResourceKey key, List<string> providers) in _resourceProviders)
            {
                if (providers.Count > 1)
                    yield return (key, providers);
            }
        }

        private static string GetPackageDisplayName(TigerModPackage package)
        {
            return package.Name ?? "original game archive";
        }

        public override IEnumerable<ArchiveFileKey> Files => _files.Keys;

        public override IEnumerable<ResourceKey> Resources => _resources.Keys;

        public override Stream? OpenFile(ArchiveFileKey fileKey)
        {
            return _files.GetValueOrDefault(fileKey)?.OpenFile(fileKey);
        }

        public override Stream? OpenResource(ResourceKey resourceKey)
        {
            return _resources.GetValueOrDefault(resourceKey)?.OpenResource(resourceKey);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (TigerModPackage package in _packages)
            {
                package.Dispose();
            }
        }
    }
}
