using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RuApp.Harness;

public sealed class UiAutomationScenario : IDisposable
{
    private const string MicAutomationId = "MicButton";
    private const string VoiceStatusAutomationId = "VoiceConversationStatus";
    private const string ChatMessagesAutomationId = "ChatMessages";
    private const string NewSessionAutomationId = "NewSessionButton";
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
                && ContainsIgnoreCase(GetName(status), "sẵn sàng"));
    }

    public async Task<HarnessGuiCheck> StartVoiceAsync(TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            AutomationElement mic = await WaitForElementAsync(MicAutomationId, timeout);
            Invoke(mic);
            bool changed = await WaitUntilAsync(
                () => ContainsIgnoreCase(GetName(mic), "Dừng trò chuyện bằng giọng nói")
                    && ContainsIgnoreCase(GetName(FindByAutomationId(VoiceStatusAutomationId)), "Đang nghe"),
                timeout);
            return Check(
                "gui.voice_start",
                changed,
                started,
                changed ? null : "voice button did not reach listening state");
        }
        catch (Exception ex)
        {
            return Failure("gui.voice_start", started, ex);
        }
    }

    public async Task<HarnessGuiCheck> WaitForVoiceTurnAsync(TimeSpan timeout, int minimumTurn = 1)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            bool eventsReady = await WaitUntilAsync(
                () => HasAtLeast("voice.capture.started", minimumTurn)
                    && HasAtLeast("voice.capture.ended", minimumTurn)
                    && HasAtLeast("voice.transcribe.completed", minimumTurn)
                    && HasAtLeast("voice.chat.sent", minimumTurn)
                    && HasAtLeast("voice.reply.completed", minimumTurn)
                    && HasAtLeast("voice.playback.ended", minimumTurn),
                timeout);
            bool messagesReady = eventsReady
                && await WaitUntilAsync(HasUserAndAssistantMessages, timeout);
            bool privateMarkerAbsent = messagesReady && !VisibleMessageContainsPrivateMarker();
            bool passed = eventsReady && messagesReady && privateMarkerAbsent;
            return Check(
                minimumTurn == 1 ? "gui.voice_turn" : "gui.voice_relisten",
                passed,
                started,
                passed ? null : "voice lifecycle or visible chat assertions did not complete");
        }
        catch (Exception ex)
        {
            return Failure(minimumTurn == 1 ? "gui.voice_turn" : "gui.voice_relisten", started, ex);
        }
    }

    public Task<HarnessGuiCheck> RequestSecondTurnAsync(TimeSpan timeout)
        => WaitForVoiceTurnAsync(timeout, minimumTurn: 2);

    public async Task<HarnessGuiCheck> StopVoiceAsync(TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            AutomationElement mic = await WaitForElementAsync(MicAutomationId, timeout);
            Invoke(mic);
            bool stopped = await WaitUntilAsync(
                () => ContainsIgnoreCase(GetName(FindByAutomationId(VoiceStatusAutomationId)), "Đã dừng")
                    && ContainsIgnoreCase(GetName(mic), "Bắt đầu trò chuyện bằng giọng nói"),
                timeout);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var counts = ReadEventCounts();
            bool noLateTurn = counts.GetValueOrDefault("voice.chat.sent") == 0
                && counts.GetValueOrDefault("voice.playback.started") == 0;
            return Check(
                "gui.voice_stop",
                stopped && noLateTurn,
                started,
                stopped && noLateTurn ? null : "voice conversation did not stop cleanly");
        }
        catch (Exception ex)
        {
            return Failure("gui.voice_stop", started, ex);
        }
    }

    public async Task<HarnessGuiCheck> CancelVoiceAsync(TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            bool transcribing = await WaitUntilAsync(
                () => HasAtLeast("voice.capture.ended", 1),
                timeout);
            if (!transcribing)
            {
                return Check("gui.voice_cancel", false, started, "transcription phase was not reached");
            }

            AutomationElement mic = await WaitForElementAsync(MicAutomationId, timeout);
            Invoke(mic);
            bool stopped = await WaitUntilAsync(
                () => ContainsIgnoreCase(GetName(FindByAutomationId(VoiceStatusAutomationId)), "Đã dừng")
                    && ContainsIgnoreCase(GetName(mic), "Bắt đầu trò chuyện bằng giọng nói")
                    && HasAtLeast("voice.conversation.cancelled", 1),
                timeout);
            var counts = ReadEventCounts();
            bool noLatePlayback = counts.GetValueOrDefault("voice.playback.started") == 0
                && counts.GetValueOrDefault("voice.reply.completed") == 0
                && !HasAtLeast("voice.chat.sent", 1)
                && !HasUserAndAssistantMessages();
            return Check(
                "gui.voice_cancel",
                stopped && noLatePlayback,
                started,
                stopped && noLatePlayback ? null : "cancelled STT produced a late UI or playback event");
        }
        catch (Exception ex)
        {
            return Failure("gui.voice_cancel", started, ex);
        }
    }

    public async Task<HarnessGuiCheck> ReplaceSessionAsync(TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            AutomationElement button = await WaitForElementAsync(NewSessionAutomationId, timeout);
            Invoke(button);
            bool replaced = await WaitUntilAsync(
                () => HasAtLeast("voice.session.replaced", 1),
                timeout);
            return Check(
                "gui.voice_stale_result",
                replaced && !VisibleMessageContainsPrivateMarker(),
                started,
                replaced ? null : "session replacement event was not observed");
        }
        catch (Exception ex)
        {
            return Failure("gui.voice_stale_result", started, ex);
        }
    }

    public async Task<HarnessGuiCheck> AssertNoLateUpdateAsync(TimeSpan duration)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var before = ReadEventCounts();
        await Task.Delay(duration);
        var after = ReadEventCounts();
        bool unchanged = before.SequenceEqual(after)
            && ContainsIgnoreCase(GetName(FindByAutomationId(VoiceStatusAutomationId)), "Đã dừng")
            && !HasUserAndAssistantMessages()
            && !VisibleMessageContainsPrivateMarker();
        return Check(
            "gui.voice_no_late_update",
            unchanged,
            started,
            unchanged ? null : "a post-cancellation UI or voice event was observed");
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

    public void Dispose()
    {
        if (!_closed)
        {
            CloseAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }

        _process.Dispose();
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
        AutomationElement? window = TryGetWindow();
        return window?.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
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
    {
        AutomationElement? messages = FindByAutomationId(ChatMessagesAutomationId);
        if (messages == null)
        {
            return false;
        }

        AutomationElementCollection items = messages.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        return items.Count >= 2;
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
