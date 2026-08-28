using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class AuditLogPanel : UserControl
{
    public AuditLogPanel()
    {
        this.InitializeComponent();
        if (Log is not null)
        {
            BindLog();
        }
    }

    public AgentAuditLog? Log
    {
        get => _log;
        set
        {
            if (_log == value)
            {
                return;
            }

            if (_log is not null)
            {
                _log.PropertyChanged -= OnLogChanged;
            }

            _log = value;
            if (_log is not null)
            {
                _log.PropertyChanged += OnLogChanged;
                AuditList.ItemsSource = _log.Entries;
            }

            BindLog();
        }
    }

    private AgentAuditLog? _log;

    private void BindLog()
    {
        if (_log is null)
        {
            return;
        }

        AuditStatus.Text = $"{_log.Entries.Count} sự kiện (seq {_log.LastSequence})";
    }

    private void OnLogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_log is null)
        {
            return;
        }

        if (e.PropertyName is nameof(AgentAuditLog.LastSequence) or null)
        {
            BindLog();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _log?.Clear();
    }
}
