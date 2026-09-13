using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RuApp.Harness;

public sealed class UiAutomationScenario : IAsyncDisposable
{
    private const string ChatMessagesAutomationId = "ChatMessages";
    private const string NewSessionAutomationId = "NewSessionButton";
    private const string AgentActivityAutomationId = "AgentStatusText";
    private const string ChatInputAutomationId = "ChatInput";
    private const string SendButtonAutomationId = "SendButton";
    private readonly Process _process;
    private readonly string _eventsPath;
    private AutomationElement? _window;
    private bool _closed;

    public UiAutomationScenario(Process process, string eventsPath)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _eventsPath = Path.GetFullPath(eventsPath);
    }

    public async Task<HarnessGuiCheck> WaitForBackendReadyAsync(TimeSpan timeout)
    {
        return await CheckAsync(
            "gui.backend_ready",
            timeout,
            () => FindByAutomationId("BackendStatus") is { } status
                && ContainsIgnoreCase(GetName(status), "ready"));
    }

    public async Task<HarnessGuiCheck> ReplaceSessionAsync(TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            AutomationElement button = await WaitForElementAsync(NewSessionAutomationId, timeout);
            Invoke(button);
            bool replaced = await WaitUntilAsync(
                () => CountVisibleMessageItems() == 0,
                timeout);
            return Check(
                "gui.session_replace",
                replaced && !VisibleMessageContainsPrivateMarker(),
                started,
                replaced ? null : "session replacement did not reset the visible transcript");
        }
        catch (Exception ex)
        {
            return Failure("gui.session_replace", started, ex);
        }
    }

    public Task<HarnessGuiCheck> AssertPrivateMetadataAsync()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool absent = !VisibleMessageContainsPrivateMarker();
        return Task.FromResult(Check(
            "gui.private_metadata",
            absent,
            started,
            absent ? null : "private voice metadata is visible in chat"));
    }

    public async Task<HarnessGuiCheck> RunAgentScenarioAsync(
        string scenario,
        TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string stage = "find_chat_input";
        try
        {
            AutomationElement input = await WaitForElementAsync(ChatInputAutomationId, timeout);
            stage = "set_chat_input";
            SetValue(input, "Harness agent turn");
            stage = "find_send_button";
            AutomationElement send = await WaitForElementAsync(SendButtonAutomationId, timeout);
            stage = "invoke_send";
            Invoke(send);

            stage = "wait_agent_activity";
            bool activity = await WaitUntilAsync(
                () => HasAtLeast("agent.activity", 1),
                timeout);
            if (activity && scenario is "agent-cancel" or "agent-stale-result")
            {
                HarnessGuiCheck replaced = await ReplaceSessionAsync(timeout);
                await Task.Delay(TimeSpan.FromSeconds(2));
                bool noLateAgentUpdate = !HasUserAndAssistantMessages()
                    && !VisibleMessageContainsPrivateMarker()
                    && !AgentSurfaceContainsPrivateMarker();
                bool lifecyclePassed = replaced.Status == "passed"
                    && noLateAgentUpdate;
                return Check(
                    "gui." + scenario,
                    lifecyclePassed,
                    started,
                    lifecyclePassed
                        ? null
                        : "In-flight Agent session produced a late visible update");
            }
            bool messages = activity
                && await WaitUntilAsync(
                    () => HasUserAndAssistantMessages()
                        && (scenario != "agent-tool-contract"
                            || VisibleMessagesContain("Harness agent tool loop completed.")),
                    timeout);
            bool truthfulAgentReply = scenario != "agent-tool-contract"
                || VisibleMessagesContain("Harness agent tool loop completed.");
            bool privateAbsent = !VisibleMessageContainsPrivateMarker()
                && !AgentSurfaceContainsPrivateMarker();
            bool passed = activity
                && messages
                && truthfulAgentReply
                && privateAbsent;

            return Check(
                "gui." + scenario,
                passed,
                started,
                passed
                    ? null
                    : $"agent_assertions_failed:activity={activity};"
                        + $"message_items={CountVisibleMessageItems()};"
                        + $"reply_contract={truthfulAgentReply};"
                        + $"private_absent={privateAbsent}");
        }
        catch (Exception ex)
        {
            return Check(
                "gui." + scenario,
                false,
                started,
                $"{stage}:{ex.GetType().Name}");
        }
    }

    public IReadOnlyDictionary<string, int> ReadEventCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(_eventsPath))
        {
            return counts;
        }

        foreach (string line in File.ReadLines(_eventsPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("code", out JsonElement code)
                    && code.ValueKind == JsonValueKind.String)
                {
                    string? value = code.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        counts[value] = counts.GetValueOrDefault(value) + 1;
                    }
                }
            }
            catch (JsonException)
            {
                // The app may be between append and flush; the next poll retries.
            }
        }

        return counts;
    }

    public async Task CloseAsync(TimeSpan timeout)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            AutomationElement? window = _window ?? TryGetWindow();
            if (window != null
                && window.TryGetCurrentPattern(WindowPattern.Pattern, out object pattern)
                && pattern is WindowPattern closePattern)
            {
                closePattern.Close();
            }
        }
        catch
        {
            // The bounded process cleanup below is owned by this driver.
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            await CloseAsync(TimeSpan.FromSeconds(2));
        }

        _process.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task<AutomationElement> WaitForElementAsync(string automationId, TimeSpan timeout)
    {
        AutomationElement? result = null;
        bool found = await WaitUntilAsync(
            () => (result = FindByAutomationId(automationId)) != null,
            timeout);
        return found && result != null
            ? result
            : throw new TimeoutException($"Automation control {automationId} was not found.");
    }

    private AutomationElement? FindByAutomationId(string automationId)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
            new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id));
        AutomationElement? window = TryGetWindow();
        AutomationElement? result = window?.FindFirst(TreeScope.Descendants, condition);
        if (result != null)
        {
            return result;
        }

        try
        {
            return AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private AutomationElement? TryGetWindow()
    {
        if (_window != null)
        {
            return _window;
        }

        if (_process.HasExited || _process.MainWindowHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            _window = AutomationElement.FromHandle(_process.MainWindowHandle);
        }
        catch (ElementNotAvailableException)
        {
            _window = null;
        }

        return _window;
    }

    private bool HasUserAndAssistantMessages()
        => CountVisibleMessageItems() >= 2;

    private int CountVisibleMessageItems()
    {
        AutomationElement? messages = FindByAutomationId(ChatMessagesAutomationId);
        if (messages == null)
        {
            return 0;
        }

        AutomationElementCollection items = messages.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        return items.Count;
    }

    private bool VisibleMessageContainsPrivateMarker()
    {
        AutomationElement? messages = FindByAutomationId(ChatMessagesAutomationId);
        if (messages == null)
        {
            return false;
        }

        string[] forbidden = ["[[VTS:", "[[VOICE_PACE:", "voice_pace", "expression_codes"];
        foreach (AutomationElement element in messages.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string name = GetName(element);
            if (forbidden.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private bool VisibleMessagesContain(string expected)
    {
        AutomationElement? messages = FindByAutomationId(ChatMessagesAutomationId);
        return messages != null
            && messages.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element => GetName(element).Contains(expected, StringComparison.Ordinal));
    }

    private bool AgentSurfaceContainsPrivateMarker()
    {
        string activity = GetName(FindByAutomationId(AgentActivityAutomationId));
        string[] forbidden = ["token", "secret", "arguments", "request_body", "transcript"];
        return forbidden.Any(marker => activity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetValue(AutomationElement element, string value)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern)
            || pattern is not ValuePattern valuePattern)
        {
            throw new InvalidOperationException("Automation control does not support ValuePattern.");
        }

        valuePattern.SetValue(value);
    }

    private static void Invoke(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)
            || pattern is not InvokePattern invokePattern)
        {
            throw new InvalidOperationException("Automation control does not support InvokePattern.");
        }

        invokePattern.Invoke();
    }

    private static string GetName(AutomationElement? element)
        => element?.Current.Name ?? string.Empty;

    private static bool ContainsIgnoreCase(string value, string expected)
        => value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private bool HasAtLeast(string code, int expected)
        => ReadEventCounts().GetValueOrDefault(code) >= expected;

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return predicate();
    }

    private static async Task<HarnessGuiCheck> CheckAsync(
        string id,
        TimeSpan timeout,
        Func<bool> predicate)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool passed = await WaitUntilAsync(predicate, timeout);
        return Check(id, passed, started, passed ? null : "condition timed out");
    }

    private static HarnessGuiCheck Check(
        string id,
        bool passed,
        DateTimeOffset started,
        string? reason)
    {
        return new HarnessGuiCheck(
            id,
            passed ? "passed" : "failed",
            (DateTimeOffset.UtcNow - started).TotalMilliseconds,
            reason);
    }

    private static HarnessGuiCheck Failure(string id, DateTimeOffset started, Exception exception)
        => Check(id, false, started, exception.GetType().Name);
}