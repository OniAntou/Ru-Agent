using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RuAgent.Broker.Tools.Computer;

/// <summary>
/// UI Automation reads for <c>inspect</c> (ADR 0031, amended 2026-11-05). This layer
/// only reads raw elements of one window's control view; visibility, grants and bounds
/// are decided by <see cref="ComputerUseService"/>. The vtable prefixes below follow
/// UIAutomationClient.h (Windows SDK 10.0.26100.0) slot for slot.
/// </summary>
public sealed partial class WindowsDesktopController
{
    // A hung target can block a cross-process UIA call indefinitely, and COM calls
    // ignore cancellation, so the read runs on its own thread with a deadline.
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(8);
    private const int MaxElementsRead = 4000;
    private const int TreeScopeChildren = 0x2;
    private const int MaxWalkDepth = 64;
    private const int BoundingRectanglePropertyId = 30001;
    private const int ControlTypePropertyId = 30003;
    private const int NamePropertyId = 30005;
    private const int IsEnabledPropertyId = 30010;
    private const int IsOffscreenPropertyId = 30022;
    private const int FirstControlTypeId = 50000;

    private static readonly string[] ControlTypeNames =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu",
        "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab",
        "TabItem", "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid",
        "DataItem", "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar",
        "Separator", "SemanticZoom", "AppBar",
    ];

    public IReadOnlyList<DesktopElement> Elements(nint window, CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? ElementsOnReaderThread(window, cancellationToken)
            : throw new ComputerUseException("computer_inspect_failed", "UI Automation needs Windows.");

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DesktopElement> ElementsOnReaderThread(nint window, CancellationToken cancellationToken)
    {
        IReadOnlyList<DesktopElement>? result = null;
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try
            {
                using var dpi = DpiScope.Enter();
                result = ReadElements(window, cancellationToken);
            }
            catch (Exception error)
            {
                failure = error;
            }
        })
        {
            IsBackground = true,
            Name = "Ru computer inspect",
        };
        reader.SetApartmentState(ApartmentState.MTA);
        reader.Start();
        var deadline = Task.Delay(InspectTimeout, cancellationToken);
        while (!reader.Join(TimeSpan.FromMilliseconds(50)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCompleted)
                throw new ComputerUseException("computer_inspect_timeout",
                    "The app did not answer UI Automation in time; take a screenshot or try again.");
        }

        if (failure is OperationCanceledException) throw failure;
        if (failure is ComputerUseException) throw failure;
        if (failure is not null)
            throw new ComputerUseException("computer_inspect_failed", "UI Automation could not read the window.");
        return result ?? [];
    }

    [SupportedOSPlatform("windows")]
    private static List<DesktopElement> ReadElements(nint window, CancellationToken cancellationToken)
    {
        var automation = (IUIAutomation)new CUIAutomation();
        var found = new List<DesktopElement>();
        var condition = automation.ControlViewCondition;
        try
        {
            var root = automation.ElementFromHandle(window);
            try
            {
                Walk(root, condition, depth: 0, found, cancellationToken);
            }
            finally
            {
                Marshal.ReleaseComObject(root);
            }
        }
        catch (COMException)
        {
            // An element that vanished mid-read ends the walk with what was read.
        }
        finally
        {
            Marshal.Release(condition);
            Marshal.ReleaseComObject(automation);
        }

        return found;
    }

    /// <summary>
    /// Reads children one level at a time and never descends below an offscreen element:
    /// a browser's background tabs keep documents with the same bounds as the visible one,
    /// and their descendants are not reliably flagged offscreen themselves.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Walk(IUIAutomationElement parent, nint condition, int depth, List<DesktopElement> found,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxWalkDepth || found.Count >= MaxElementsRead) return;
        var children = parent.FindAll(TreeScopeChildren, condition);
        try
        {
            var count = children.Length;
            for (var index = 0; index < count && found.Count < MaxElementsRead; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IUIAutomationElement child;
                try
                {
                    child = children.GetElement(index);
                }
                catch (COMException)
                {
                    continue;
                }

                try
                {
                    var read = Read(child);
                    if (read is { Offscreen: true }) continue;
                    if (read is not null) found.Add(read);
                    Walk(child, condition, depth + 1, found, cancellationToken);
                }
                catch (COMException)
                {
                    // A child that vanished mid-read is skipped with its subtree.
                }
                finally
                {
                    Marshal.ReleaseComObject(child);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(children);
        }
    }

    private static DesktopElement? Read(IUIAutomationElement element)
    {
        try
        {
            // Offscreen is read first so an offscreen element without bounds still prunes its subtree.
            if (element.GetCurrentPropertyValue(IsOffscreenPropertyId) is true)
                return new DesktopElement("Unknown", "", default, Offscreen: true, Enabled: false);
            if (element.GetCurrentPropertyValue(BoundingRectanglePropertyId) is not double[] { Length: 4 } rect
                || double.IsInfinity(rect[0]) || rect[2] <= 0 || rect[3] <= 0)
                return null;
            var bounds = new PixelRect(
                (int)Math.Round(rect[0]), (int)Math.Round(rect[1]),
                (int)Math.Round(rect[0] + rect[2]), (int)Math.Round(rect[1] + rect[3]));
            var typeId = element.GetCurrentPropertyValue(ControlTypePropertyId) is int type ? type : 0;
            var role = typeId - FirstControlTypeId is var slot && slot >= 0 && slot < ControlTypeNames.Length
                ? ControlTypeNames[slot]
                : "Unknown";
            var name = element.GetCurrentPropertyValue(NamePropertyId) as string ?? "";
            var enabled = element.GetCurrentPropertyValue(IsEnabledPropertyId) is not false;
            return new DesktopElement(role, name, bounds, Offscreen: false, enabled);
        }
        catch (COMException)
        {
            return null;
        }
    }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    /// <summary>IUIAutomation, slots 3 to 18 (through get_ControlViewCondition).</summary>
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(nint hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        nint ControlViewCondition { get; }
    }

    /// <summary>IUIAutomationElement, slots 3 to 10 (through GetCurrentPropertyValue).</summary>
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        IUIAutomationElementArray FindAll(int scope, nint condition);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }
}
