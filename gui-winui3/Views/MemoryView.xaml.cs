using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class MemoryView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private bool _disposed;
        public ObservableCollection<MemoryItemDto> Memories { get; } = new();

        public MemoryView()
        {
            this.InitializeComponent();
            MemoryListView.ItemsSource = Memories;
            Memories.CollectionChanged += OnMemoriesChanged;
        }

        private void OnMemoriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (EmptyMemoryPanel != null)
            {
                EmptyMemoryPanel.Visibility = Memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void Initialize(ApiService apiService)
        {
            _apiService = apiService;
            _ = LoadMemoryAsync();
        }

        private async Task LoadMemoryAsync()
        {
            if (_apiService == null) return;
            Memories.Clear();
            var items = await _apiService.GetMemoryAsync();
            if (items != null)
            {
                foreach (var item in items)
                {
                    Memories.Add(item);
                }
            }
            if (EmptyMemoryPanel != null)
            {
                EmptyMemoryPanel.Visibility = Memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void OnClearMemoryClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;

            var dialog = new ContentDialog
            {
                Title = "Confirm Memory Clear",
                Content = "Are you sure you want to clear all saved local memories? This action cannot be undone.",
                PrimaryButtonText = "Clear all",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                bool ok = await _apiService.ClearMemoryAsync();
                if (ok)
                {
                    Memories.Clear();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Memories.CollectionChanged -= OnMemoriesChanged;
            _apiService = null;
            Memories.Clear();
        }
    }
}