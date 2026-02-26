using Avalonia.Controls;
using Avalonia.Threading;
using System;

namespace Orpheus.Desktop.Views;

public partial class QueuePanel : UserControl
{
    public QueuePanel()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.CurrentQueueIndex))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (sender is not MainWindowViewModel viewModel)
                return;

            if (QueueList is null)
                return;

            if (viewModel.CurrentQueueIndex < 0)
                return;

            QueueList.SelectedIndex = viewModel.CurrentQueueIndex;
            QueueList.ScrollIntoView(viewModel.CurrentQueueIndex);
        });
    }
}
