using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
namespace NetOps.Desktop;
public partial class EasyWorkflowView : UserControl
{
    private MainViewModel? model;
    private int displayedStep = -1;
    public EasyWorkflowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveModel();
        Loaded += (_, _) => ObserveModel();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= ModelChanged; model = null; };
    }
    private void ObserveModel()
    {
        if (model is not null) model.PropertyChanged -= ModelChanged;
        model = DataContext as MainViewModel; displayedStep = model?.EasyStep ?? -1;
        if (model is not null) model.PropertyChanged += ModelChanged;
        BodyScroll.ScrollToTop();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (model is null) return;
        if (e.PropertyName == nameof(MainViewModel.EasyStep) && displayedStep != model.EasyStep || e.PropertyName == nameof(MainViewModel.EasyActive) && model.EasyActive)
        { displayedStep = model.EasyStep; BodyScroll.ScrollToTop(); }
    }
    private void ConnectionSettingsClick(object sender, RoutedEventArgs e)
    { if (DataContext is MainViewModel vm && vm.SelectedDevice is not null) new AddDeviceWindow(vm, vm.SelectedDevice) { Owner = Window.GetWindow(this) }.ShowDialog(); }
}
