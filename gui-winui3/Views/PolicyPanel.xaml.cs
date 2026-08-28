using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class PolicyPanel : UserControl
{
    public PolicyPanel()
    {
        this.InitializeComponent();
        if (Controller is not null)
        {
            BindController();
        }
    }

    public AgentPolicyController? Controller
    {
        get => _controller;
        set
        {
            if (_controller == value)
            {
                return;
            }

            if (_controller is not null)
            {
                _controller.PropertyChanged -= OnControllerChanged;
            }

            _controller = value;
            if (_controller is not null)
            {
                _controller.PropertyChanged += OnControllerChanged;
                PolicyList.ItemsSource = _controller.Policies;
            }

            BindController();
        }
    }

    private AgentPolicyController? _controller;

    private void BindController()
    {
        if (_controller is null)
        {
            return;
        }

        PolicyStatus.Text = _controller.StatusText;
        UpdateButtons();
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (e.PropertyName is nameof(AgentPolicyController.StatusText) or null)
        {
            PolicyStatus.Text = _controller.StatusText;
        }

        if (e.PropertyName is nameof(AgentPolicyController.IsBusy) or null)
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        if (_controller is null)
        {
            return;
        }

        LoadButton.IsEnabled = !_controller.IsBusy;
        CreateButton.IsEnabled = !_controller.IsBusy;
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        LoadButton.IsEnabled = false;
        try
        {
            await _controller.LoadAsync();
        }
        catch (Exception ex)
        {
            PolicyStatus.Text = $"Lỗi: {ex.Message}";
        }
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            var model = new AgentPolicyModel(
                IdBox.Text,
                ScopeBox.Text,
                KindBox.Text,
                DescBox.Text,
                EnabledBox.IsChecked ?? false,
                0);
            await _controller.CreateAsync(model);
        }
        catch (Exception ex)
        {
            PolicyStatus.Text = $"Lỗi: {ex.Message}";
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null || sender is not Button button)
        {
            return;
        }

        var id = button.Tag as string;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        try
        {
            await _controller.DeleteAsync(id);
        }
        catch (Exception ex)
        {
            PolicyStatus.Text = $"Lỗi: {ex.Message}";
        }
    }
}
