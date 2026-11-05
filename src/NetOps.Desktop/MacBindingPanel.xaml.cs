using System.Windows;
using System.Windows.Controls;

namespace NetOps.Desktop;

public partial class MacBindingPanel : UserControl
{
    public MacBindingPanel() => InitializeComponent();
    private void CommitDraft() { MacDraftGrid.CommitEdit(DataGridEditingUnit.Cell, true); MacDraftGrid.CommitEdit(DataGridEditingUnit.Row, true); }
    private void StageMacClick(object sender, RoutedEventArgs e)
    { CommitDraft(); if (DataContext is MainViewModel vm) { if (!string.IsNullOrWhiteSpace(vm.MacPasteText)) vm.ImportMacRows.Execute(null); vm.StageMac.Execute(null); } }
    private void EditSelectedMacClick(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is not MainViewModel vm || vm.MacRows.FirstOrDefault(r => r.OriginalBinding is not null) is not { } row) return;
            MacDraftGrid.ScrollIntoView(row, MacDraftGrid.Columns[1]); MacDraftGrid.CurrentCell = new DataGridCellInfo(row, MacDraftGrid.Columns[1]); MacDraftGrid.Focus(); MacDraftGrid.BeginEdit();
        });
    }
    private void MacDraftCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    { if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is MacDraftRow row && DataContext is MainViewModel vm) Dispatcher.BeginInvoke(() => vm.NormalizeMacRow(row)); }
}
