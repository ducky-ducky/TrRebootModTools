using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System;

namespace TrRebootTools.Shared.Controls
{
    internal class CheckListBoxItem : ListBoxItem
    {
        private readonly CheckBox _checkbox;
        private readonly TextBlock _label;
        private readonly TextBox _priorityBox;

        private ICheckListBoxEntry? _entry;
        private IPriorityListBoxEntry? _priorityEntry;
        private bool _updatingPriorityText;
        private bool _paddingAdjusted;

        public CheckListBoxItem()
        {
            StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
            
            _checkbox = new() { VerticalAlignment = VerticalAlignment.Center };
            _checkbox.IsCheckedChanged += OnCheckBoxChanged;
            panel.Children.Add(_checkbox);

            _label = new() { VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(_label);

            // The name takes the remaining width and the optional priority box sits in a fixed column on the right
            Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,90") };
            Grid.SetColumn(panel, 0);
            grid.Children.Add(panel);

            _priorityBox = new()
                           {
                               MinHeight = 0,
                               Padding = new Thickness(4, 1),
                               HorizontalAlignment = HorizontalAlignment.Stretch,
                               VerticalAlignment = VerticalAlignment.Center,
                               IsVisible = false
                           };
            _priorityBox.PropertyChanged += OnPriorityBoxPropertyChanged;
            _priorityBox.LostFocus += OnPriorityBoxLostFocus;
            Grid.SetColumn(_priorityBox, 1);
            grid.Children.Add(_priorityBox);

            // Make the row fill the width of the item so the priority column lines up on the right
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Content = grid;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            // The priority box has to reach the right edge of the row, so drop the theme's padding on that side only
            if (!_paddingAdjusted)
            {
                _paddingAdjusted = true;
                Padding = new Thickness(Padding.Left, Padding.Top, 0, Padding.Bottom);
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_entry != null)
                _entry.CheckedChanged -= OnEntryCheckedChanged;

            _entry = DataContext as ICheckListBoxEntry;
            _checkbox.IsChecked = _entry?.Checked ?? false;
            _label.Text = _entry?.Name;
            Opacity = (_entry?.Checked ?? false) ? 1.0 : 0.5;

            if (_entry != null)
                _entry.CheckedChanged += OnEntryCheckedChanged;

            _priorityEntry = DataContext as IPriorityListBoxEntry;
            _priorityBox.IsVisible = _priorityEntry != null;
            SetPriorityText(_priorityEntry?.Priority.ToString() ?? "");
        }

        private void SetPriorityText(string text)
        {
            _updatingPriorityText = true;
            try
            {
                _priorityBox.Text = text;
            }
            finally
            {
                _updatingPriorityText = false;
            }
        }

        private void OnPriorityBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != TextBox.TextProperty || _updatingPriorityText || _priorityEntry == null)
                return;

            // Only valid numbers are stored; anything else is put back to the last valid value when the box loses focus
            if (int.TryParse(_priorityBox.Text?.Trim(), out int priority) && priority >= 1)
                _priorityEntry.Priority = priority;
        }

        private void OnPriorityBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (_priorityEntry != null)
                SetPriorityText(_priorityEntry.Priority.ToString());
        }

        private void OnEntryCheckedChanged(object? sender, EventArgs e)
        {
            if (_entry != null)
            {
                _checkbox.IsChecked = _entry.Checked;
                Opacity = _entry.Checked ? 1.0 : 0.5;
                IsCheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnCheckBoxChanged(object? sender, RoutedEventArgs e)
        {
            if (_entry != null)
                _entry.Checked = _checkbox.IsChecked ?? false;
        }

        public event EventHandler? IsCheckedChanged;

        protected override Type StyleKeyOverride => typeof(ListBoxItem);
    }
}
