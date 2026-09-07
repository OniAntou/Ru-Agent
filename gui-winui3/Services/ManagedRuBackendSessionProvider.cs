using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;

namespace RuApp.Services;

public sealed class ManagedRuBackendSessionProvider : IRuBackendSessionProvider
{
    private readonly PublicHostStarter _starter;
    private readonly PublicHostLocator _locator;
    private readonly object _gate = new();
    private Process? _monitoredProcess;
    private bool _disposed;

    public ManagedRuBackendSessionProvider(PublicHostStarter? starter = null, PublicHostLocator? locator = null)
    {
        _locator = locator ?? new PublicHostLocator();
        _starter = starter ?? new PublicHostStarter(_locator);
    }

    public string DisplayName => "Public Host (managed)";
    public BackendSession? Session { get; private set; }
    public event EventHandler? BackendExited;

    public async Task<BackendSession?> StartAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (Session is not null)
            {
                if (_monitoredProcess is { HasExited: true })
                {
                    OnProcessExited(this, EventArgs.Empty);
                }
                else
                {
                    return Session;
                }
            }
        }

        try
        {
            var hostSession = await _starter.GetOrStartAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            var backendSession = BackendSession.FromLoopback(hostSession.BaseUri.Port, hostSession.BearerToken);

            lock (_gate)
            {
                _monitoredProcess?.Dispose();
                _monitoredProcess = null;

                if (hostSession.Descriptor.ProcessId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(hostSession.Descriptor.ProcessId);
                        proc.EnableRaisingEvents = true;
                        proc.Exited += OnProcessExited;
                        if (proc.HasExited)
                        {
                            OnProcessExited(this, EventArgs.Empty);
                            return null;
                        }
                        _monitoredProcess = proc;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ManagedRuBackendSessionProvider] Could not hook process {hostSession.Descriptor.ProcessId}: {ex.Message}");
                    }
                }

                Session = backendSession;
            }

            return backendSession;
        }
        catch (AgentApiException ex) when (ex.ErrorCode == "public_host_port_conflict")
        {
            Debug.WriteLine($"[ManagedRuBackendSessionProvider] Port conflict: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ManagedRuBackendSessionProvider] Start failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void NotifyBackendDown()
    {
        lock (_gate)
        {
            Session = null;
            if (_monitoredProcess != null)
            {
                try { _monitoredProcess.Exited -= OnProcessExited; } catch { }
                _monitoredProcess.Dispose();
                _monitoredProcess = null;
            }
        }
        BackendExited?.Invoke(this, EventArgs.Empty);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool shouldRaise = false;
        lock (_gate)
        {
            if (Session is not null)
            {
                Session = null;
                shouldRaise = true;
            }
            if (_monitoredProcess != null)
            {
                try { _monitoredProcess.Exited -= OnProcessExited; } catch { }
                _monitoredProcess.Dispose();
                _monitoredProcess = null;
            }
        }

        if (shouldRaise)
        {
            BackendExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Session = null;
            if (_monitoredProcess != null)
            {
                try { _monitoredProcess.Exited -= OnProcessExited; } catch { }
                _monitoredProcess.Dispose();
                _monitoredProcess = null;
            }
        }
        _starter.Dispose();
    }
}
