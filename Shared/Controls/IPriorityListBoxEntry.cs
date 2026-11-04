namespace TrRebootTools.Shared.Controls
{
    // Implemented by entries that should show an editable priority number to the right of the name in a CheckListBox
    public interface IPriorityListBoxEntry
    {
        int Priority
        {
            get;
            set;
        }
    }
}
