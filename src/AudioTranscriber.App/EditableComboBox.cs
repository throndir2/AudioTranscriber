using Avalonia;
using Avalonia.Controls;

namespace AudioTranscriber.App;

/// <summary>Syncs an editable ComboBox selection to its text when the drop-down opens.</summary>
public static class EditableComboBox
{
    public static readonly AttachedProperty<bool> SyncSelectionProperty = AvaloniaProperty.RegisterAttached<ComboBox, bool>(
        "SyncSelection", typeof(EditableComboBox));

    static EditableComboBox() => SyncSelectionProperty.Changed.AddClassHandler<ComboBox>((combo, e) =>
    {
        combo.DropDownOpened -= OnDropDownOpened;
        if (e.NewValue is true) combo.DropDownOpened += OnDropDownOpened;
    });

    public static bool GetSyncSelection(AvaloniaObject element) => element.GetValue(SyncSelectionProperty);
    public static void SetSyncSelection(AvaloniaObject element, bool value) => element.SetValue(SyncSelectionProperty, value);

    private static void OnDropDownOpened(object? sender, EventArgs e) => SyncSelectionToText((ComboBox)sender!);

    public static void SyncSelectionToText(ComboBox combo)
    {
        var text = combo.Text ?? "";
        var items = combo.ItemsSource?.Cast<object?>().ToArray() ?? combo.Items.Cast<object?>().ToArray();
        var match = Array.FindIndex(items, item => string.Equals(item?.ToString(), text, StringComparison.CurrentCulture));
        if (combo.SelectedIndex == match) return;
        combo.SelectedIndex = match;
        if (combo.Text != text) combo.Text = text;
    }
}
