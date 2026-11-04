using System;
using TrRebootTools.Shared.Controls;

namespace TrRebootTools.ModManager.Mod
{
    public class InstalledMod : ICheckListBoxEntry, IPriorityListBoxEntry
    {
        private bool _enabled;
        private int _priority;

        public InstalledMod(int archiveId, string name, bool enabled, int priority = 1)
        {
            ArchiveId = archiveId;
            Name = name;
            _enabled = enabled;
            _priority = priority;
            SavedEnabled = enabled;
            SavedPriority = priority;
        }

        // 1 is the highest priority. This is the value shown in the list; it only takes effect once the changes are confirmed.
        public int Priority
        {
            get { return _priority; }
            set
            {
                if (value == _priority)
                    return;

                _priority = value;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // The state that is currently applied. The ticked state and the priority shown in the list can differ from it until they are confirmed.
        public bool SavedEnabled
        {
            get;
        }

        public int SavedPriority
        {
            get;
        }

        public bool IsChanged => Enabled != SavedEnabled || Priority != SavedPriority;

        public event EventHandler? StateChanged;

        public int ArchiveId
        {
            get;
        }

        public string Name
        {
            get;
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (value == _enabled)
                    return;

                _enabled = value;
                CheckedChanged?.Invoke(this, EventArgs.Empty);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        bool ICheckListBoxEntry.Checked
        {
            get => Enabled;
            set => Enabled = value;
        }

        public event EventHandler? CheckedChanged;

        public override string ToString()
        {
            return Name;
        }
    }
}
