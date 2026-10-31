using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;
using RuApp.ViewModels;

namespace RuApp.Views
{
    /// <summary>The "Custom model API" editor for one named custom provider (ADR 0034).</summary>
    public sealed partial class CustomProviderEditorView : UserControl
    {
        public CustomProviderEditorView()
        {
            InitializeComponent();
        }

        public CustomProviderEditorViewModel? ViewModel { get; private set; }

        internal void Attach(CustomProviderEditorViewModel viewModel)
        {
            if (ViewModel is not null) ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel = viewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            Bindings.Update();
            ApplyStatusTone();
        }

        /// <summary>Puts the caret where typing starts: the ID for a new provider, the name otherwise.</summary>
        internal void FocusFirstField()
        {
            if (ViewModel is null) return;
            (ViewModel.IsNew ? (Control)ProviderIdBox : DisplayNameBox).Focus(FocusState.Programmatic);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CustomProviderEditorViewModel.StatusTone)) ApplyStatusTone();
        }

        private void ApplyStatusTone()
        {
            var key = ViewModel?.StatusTone switch
            {
                ProviderConnectionTest.ErrorTone => "RuDangerBrush",
                ProviderConnectionTest.SuccessTone => "RuSuccessBrush",
                _ => "RuMutedBrush",
            };
            if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush value)
                StatusText.Foreground = value;
        }

        private void OnRemoveModelClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: CustomModelRow row }) ViewModel?.RemoveModelCommand.Execute(row);
        }

        private void OnAddDiscoveredClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: DiscoveredModelRow row }) ViewModel?.AddDiscoveredCommand.Execute(row);
        }
    }
}
