using System;
using System.Collections.Generic;
using System.Linq;

namespace TrRebootTools.Shared.Cdc
{
    public abstract partial class ArchiveSet
    {
        private readonly HashSet<int> _gameNfoNumbers = [];

        protected virtual int MaxModArchiveId => int.MaxValue;

        protected virtual bool CanUseArchiveIdsBelowGame => false;

        public int? HighestUsableModArchiveId => MaxModArchiveId == int.MaxValue ? null : MaxModArchiveId;

        public bool IsUsableModArchiveId(int id) => id <= MaxModArchiveId;

        public List<ArchiveNumberClash> GetArchiveNumberClashes()
        {
            lock (_lock)
            {
                List<ArchiveNumberClash> clashes = [];
                foreach (Archive modArchive in _duplicateArchives.Where(a => a.ModName != null && a.SubId == 0))
                {
                    Archive? gameArchive = _archives.Values.FirstOrDefault(a => a.Id == modArchive.Id && a.ModName == null);
                    if (gameArchive != null)
                        clashes.Add(new ArchiveNumberClash(modArchive.ModName!, modArchive.Id, gameArchive.ToString()));
                }
                return clashes;
            }
        }

        private Archive? FindModArchive(int archiveId)
        {
            return _archives.Values
                            .Concat(_duplicateArchives)
                            .FirstOrDefault(a => a.Id == archiveId && a.SubId == 0 && a.ModName != null);
        }

        private int GetHighestGameArchiveId()
        {
            return _archives.Values.Where(a => a.ModName == null).Select(a => a.Id).DefaultIfEmpty(0).Max();
        }

        private HashSet<int> GetUsedArchiveIds()
        {
            HashSet<int> usedIds = _archives.Values.Concat(_duplicateArchives).Select(a => a.Id).ToHashSet();
            usedIds.UnionWith(_gameNfoNumbers);
            usedIds.UnionWith(_loadOrderArchiveIds);
            return usedIds;
        }

        private bool IsFreeArchiveId(int id, HashSet<int> usedIds)
        {
            return id > 0 && id <= MaxModArchiveId && !usedIds.Contains(id) && !IsReservedArchiveId(id);
        }

        private int FindFreeModArchiveId()
        {
            HashSet<int> usedIds = GetUsedArchiveIds();
            int? id = FindFreeArchiveId(usedIds);

            if (id != null && UsesLoadOrderArchive && _loadOrderArchiveIds.Count == 0)
            {
                usedIds.Add(id.Value);
                if (FindFreeArchiveId(usedIds) == null)
                    id = null;
            }

            if (id == null)
            {
                throw new NoFreeArchiveNumberException(
                    "There is no free archive number left for a new mod.\n\n" +
                    $"{CdcGameInfo.Get(Game).ShortName} can only use archive numbers up to {MaxModArchiveId}, and the game and the installed mods " +
                    "(ticked or not) have taken all the ones a mod can have. Remove a mod you no longer use and try again."
                );
            }
            return id.Value;
        }

        private int? FindFreeArchiveId(HashSet<int> usedIds)
        {
            int highestGameId = GetHighestGameArchiveId();

            long lastCandidate = Math.Min(MaxModArchiveId, (long)highestGameId + usedIds.Count + 1);
            for (long id = highestGameId + 1; id <= lastCandidate; id++)
            {
                if (IsFreeArchiveId((int)id, usedIds))
                    return (int)id;
            }

            if (CanUseArchiveIdsBelowGame)
            {
                (int first, int last) = FindLargestUnusedRunBelowGame(highestGameId);
                for (int id = last; id >= first; id--)
                {
                    if (IsFreeArchiveId(id, usedIds))
                        return id;
                }
            }

            return null;
        }

        private (int First, int Last) FindLargestUnusedRunBelowGame(int highestGameId)
        {
            HashSet<int> gameNumbers = _archives.Values.Where(a => a.ModName == null).Select(a => a.Id).ToHashSet();
            gameNumbers.UnionWith(_gameNfoNumbers);

            (int First, int Last) best = (1, 0);
            int runStart = 1;
            for (int id = 1; id <= highestGameId; id++)
            {
                if (id < highestGameId && !gameNumbers.Contains(id))
                    continue;

                int runEnd = id - 1;
                if (runEnd >= runStart && runEnd - runStart >= best.Last - best.First)
                    best = (runStart, runEnd);

                runStart = id + 1;
            }
            return best;
        }
    }

    public record ArchiveNumberClash(string ModName, int ArchiveId, string GameArchiveFileName);

    public class NoFreeArchiveNumberException : Exception
    {
        public NoFreeArchiveNumberException(string message)
            : base(message)
        {
        }
    }
}
