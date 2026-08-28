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
            TrustText.Text = $"Đường dẫn: {WorkspaceBox.Text}\nMã nhận diện: {shortHash}";
            TrustApproveButton.IsEnabled = true;
            TrustDenyButton.IsEnabled = true;
        }

        bool approvalPending = _controller.HasPendingApproval;
        ApprovalPanel.Visibility = approvalPending ? Visibility.Visible : Visibility.Collapsed;
        if (approvalPending)
        {
            ApprovalText.Text = string.IsNullOrWhiteSpace(_controller.PendingApprovalSummary)
                ? "Agent đang yêu cầu một hành động cần phê duyệt."
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
            StatusText.Text = "Cần nhập thư mục làm việc và chỉ thị.";
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
            StatusText.Text = $"Lỗi: {ex.Message}";
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
            throw new InvalidOperationException("Session API chưa sẵn sàng.");
        }

        var session = await _apiService.CreateSessionAsync();
        if (session is null || string.IsNullOrWhiteSpace(session.Id))
        {
            throw new InvalidOperationException("Không tạo được Agent session.");
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
        throw new InvalidOperationException("Không cập nhật được preset quyền cho session.");
    }

    private async Task<bool> ConfirmFullAccessAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Xác nhận Full Access",
            Content = "Full Access cho phép Agent dùng terminal và mạng theo chính sách của run "
                + "với quyền của người dùng Windows hiện tại. Đây không phải sandbox; các hành động "
                + "rủi ro vẫn yêu cầu phê duyệt riêng.",
            PrimaryButtonText = "Tiếp tục",
            CloseButtonText = "Hủy",
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
            StatusText.Text = $"Lỗi xác nhận workspace: {ex.Message}";
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
            StatusText.Text = $"Lỗi phê duyệt: {ex.Message}";
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
            StatusText.Text = $"Lỗi từ chối: {ex.Message}";
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
            StatusText.Text = $"Lỗi hủy: {ex.Message}";
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
            StatusText.Text = $"Lỗi tiếp tục: {ex.Message}";
        }
    }
}
