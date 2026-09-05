using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ControlPanel : UserControl
{
    private ApiService? _apiService;
    private AgentRunController? _controller;
    private AgentPermissionPreset _preset = AgentPermissionPreset.Standard;
    private string? _sessionId;

    public ControlPanel()
    {
        InitializeComponent();
        WorkspaceBox.Text = Environment.CurrentDirectory;
        UpdateButtons();
        UpdateActionPanels();
    }

    public string DefaultWorkspace => Environment.CurrentDirectory;

    public ApiService? ApiService
    {
        get => _apiService;
        set => _apiService = value;
    }

    public string? SessionId => _sessionId;

    public AgentRunController? Controller
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
                EventList.ItemsSource = _controller.Events;
            }

            UpdateButtons();
            UpdateActionPanels();
        }
    }

    public void SetSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _sessionId = sessionId.Trim();
    }

    public void SetPreset(AgentPermissionPreset preset)
    {
        _preset = preset;
        if (PresetBox is not null)
        {
            PresetBox.SelectedIndex = preset == AgentPermissionPreset.FullAccess ? 1 : 0;
        }
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (e.PropertyName is nameof(AgentRunController.StatusText) or null)
        {
            StatusText.Text = _controller.StatusText;
        }

        if (e.PropertyName is nameof(AgentRunController.CanCancel)
            or nameof(AgentRunController.CanContinue)
            or nameof(AgentRunController.IsBusy)
            or null)
        {
            UpdateButtons();
        }

        if (e.PropertyName is nameof(AgentRunController.CurrentRun)
            or nameof(AgentRunController.NeedsWorkspaceTrust)
            or nameof(AgentRunController.PendingApprovalId)
            or nameof(AgentRunController.PendingApprovalSummary)
            or null)
        {
            UpdateActionPanels();
        }
    }

    private void UpdateButtons()
    {
        if (_controller is null)
        {
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ContinueButton.IsEnabled = false;
            return;
        }

        CancelButton.IsEnabled = _controller.CanCancel;
        ContinueButton.IsEnabled = _controller.CanContinue;
        StartButton.IsEnabled = !_controller.IsBusy;
    }

    private void UpdateActionPanels()
    {
        if (_controller is null)
        {
            TrustPanel.Visibility = Visibility.Collapsed;
            ApprovalPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var run = _controller.CurrentRun;
        bool trustPending = _controller.NeedsWorkspaceTrust;
        TrustPanel.Visibility = trustPending ? Visibility.Visible : Visibility.Collapsed;
        if (trustPending && run is not null)
        {
            string displayHash = run.DisplayIdentityHash ?? string.Empty;
            string shortHash = displayHash.Length > 16 ? displayHash[..16] + "…" : displayHash;
            TrustText.Text = $"Path: {WorkspaceBox.Text}\nIdentity: {shortHash}";
            TrustApproveButton.IsEnabled = true;
            TrustDenyButton.IsEnabled = true;
        }

        bool approvalPending = _controller.HasPendingApproval;
        ApprovalPanel.Visibility = approvalPending ? Visibility.Visible : Visibility.Collapsed;
        if (approvalPending)
        {
            ApprovalText.Text = string.IsNullOrWhiteSpace(_controller.PendingApprovalSummary)
                ? "Agent is requesting an action that requires approval."
                : _controller.PendingApprovalSummary;
            ApprovalApproveButton.IsEnabled = true;
            ApprovalDenyButton.IsEnabled = true;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null || _controller.IsBusy)
        {
            return;
        }

        string workspace = WorkspaceBox.Text?.Trim() ?? string.Empty;
        string prompt = InstructionBox.Text?.Trim() ?? string.Empty;
        if (workspace.Length == 0 || prompt.Length == 0)
        {
            StatusText.Text = "Workspace directory and instruction are required.";
            return;
        }

        try
        {
            string sessionId = await EnsureSessionAsync();
            AgentPermissionPreset preset = SelectedPreset();
            if (preset == AgentPermissionPreset.FullAccess && !await ConfirmFullAccessAsync())
            {
                PresetBox.SelectedIndex = 0;
                _preset = AgentPermissionPreset.Standard;
                return;
            }

            long presetVersion = await SelectPresetAsync(sessionId, preset);
            StartButton.IsEnabled = false;
            var request = new CreateAgentRunRequest(
                SessionId: sessionId,
                WorkspacePath: workspace,
                Prompt: prompt,
                Preset: preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard",
                PresetSelectionVersion: presetVersion,
                IdempotencyKey: Guid.NewGuid().ToString("N"));
            await _controller.CreateAndRunAsync(request);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            UpdateButtons();
            UpdateActionPanels();
        }
    }

    private async Task<string> EnsureSessionAsync()
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            return _sessionId;
        }

        if (_apiService is null)
        {
            throw new InvalidOperationException("Session API is not ready.");
        }

        var session = await _apiService.CreateSessionAsync();
        if (session is null || string.IsNullOrWhiteSpace(session.Id))
        {
            throw new InvalidOperationException("Could not create Agent session.");
        }

        _sessionId = session.Id;
        return _sessionId;
    }

    private async Task<long> SelectPresetAsync(string sessionId, AgentPermissionPreset preset)
    {
        string wirePreset = preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard";
        bool interactive = preset == AgentPermissionPreset.FullAccess;
        AgentApiException? lastConflict = null;
        for (long expectedVersion = 0; expectedVersion <= 3; expectedVersion++)
        {
            try
            {
                return await _controller!.SetSessionPresetAsync(
                    sessionId,
                    wirePreset,
                    expectedVersion,
                    interactive);
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "preset_version_conflict")
            {
                lastConflict = ex;
            }
        }

        if (lastConflict is not null)
        {
            throw lastConflict;
        }
        throw new InvalidOperationException("Could not update permission preset for session.");
    }

    private async Task<bool> ConfirmFullAccessAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Confirm Full Access",
            Content = "Full Access allows the Agent to use the terminal and network under the run policy "
                + "with the permissions of the current Windows user. This is not a sandbox; high-risk actions "
                + "still require explicit approval.",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private AgentPermissionPreset SelectedPreset()
    {
        if (PresetBox.SelectedItem is ComboBoxItem item
            && string.Equals(item.Tag as string, "full_access", StringComparison.Ordinal))
        {
            _preset = AgentPermissionPreset.FullAccess;
            return _preset;
        }

        _preset = AgentPermissionPreset.Standard;
        return _preset;
    }

    private async void OnTrustApproveClick(object sender, RoutedEventArgs e)
    {
        await ResolveTrustAsync(approve: true);
    }

    private async void OnTrustDenyClick(object sender, RoutedEventArgs e)
    {
        await ResolveTrustAsync(approve: false);
    }

    private async Task ResolveTrustAsync(bool approve)
    {
        if (_controller is null)
        {
            return;
        }

        TrustApproveButton.IsEnabled = false;
        TrustDenyButton.IsEnabled = false;
        try
        {
            await _controller.TrustAsync(approve);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Workspace confirmation error: {ex.Message}";
        }
        finally
        {
            UpdateActionPanels();
        }
    }

    private async void OnApprovalApproveClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        ApprovalApproveButton.IsEnabled = false;
        ApprovalDenyButton.IsEnabled = false;
        try
        {
            await _controller.ApproveAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Approval error: {ex.Message}";
        }
        finally
        {
            UpdateActionPanels();
        }
    }

    private async void OnApprovalDenyClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        ApprovalApproveButton.IsEnabled = false;
        ApprovalDenyButton.IsEnabled = false;
        try
        {
            await _controller.DenyAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Denial error: {ex.Message}";
        }
        finally
        {
            UpdateActionPanels();
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.CancelAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cancel error: {ex.Message}";
        }
    }

    private async void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.ContinueAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Resume error: {ex.Message}";
        }
    }
}
