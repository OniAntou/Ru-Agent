namespace RuApp.Services;

/// <summary>
/// The composer's fixed vocabulary: the glyph, label, and announced name of the send
/// control in each of its two faces, and of the Agent mode chip.
///
/// These literals were previously written twice — once in <c>ChatView.xaml</c> for the
/// initial state and once in the code-behind for the runtime state — so the two could
/// drift apart and only the XAML copy was reachable by a test. They live here now, and
/// both the markup and the code-behind read them from one place.
///
/// These are properties rather than constants because XAML can only reach a static
/// member through <c>x:Static</c>, which does not accept a constant.
/// </summary>
public static class AgentComposerPresentation
{
    /// <summary>The send face: submits the typed message.</summary>
    public static string SendGlyph => "\uE725";

    /// <summary>Announced name of the send face.</summary>
    public static string SendName => "Send message";

    /// <summary>The stop face: cancels the run in flight.</summary>
    public static string StopGlyph => "\uE71A";

    /// <summary>Announced name of the stop face.</summary>
    public static string StopName => "Stop Coding Agent";

    /// <summary>Announced name of the Agent mode chip.</summary>
    public static string AgentModeName => "Coding Agent";

    /// <summary>Visible text of the Agent mode chip.</summary>
    public static string AgentModeLabelText => "Agent";

    /// <summary>Glyph of the Agent mode chip.</summary>
    public static string AgentModeGlyph => "\uEC25";

    /// <summary>
    /// The arrow points left when stopping and right when sending, so the control reads
    /// as its action rather than as a fixed icon that changes colour.
    /// </summary>
    public static string GlyphFor(bool isStopFace) => isStopFace ? StopGlyph : SendGlyph;

    /// <summary>The name a screen reader announces for the current face.</summary>
    public static string NameFor(bool isStopFace) => isStopFace ? StopName : SendName;

    /// <summary>Composer hint while nothing is running: the text becomes a turn.</summary>
    public static string MessagePlaceholder => "Message Ru…";

    /// <summary>
    /// Composer hint while a run is in hand: the text is queued instead of dropped.
    /// The old placeholder promised nothing and the Enter key then did nothing, which
    /// read as a broken input rather than as a rule.
    /// </summary>
    public static string QueuePlaceholder => "Type to queue — sent when this turn ends";

    /// <summary>Inline status after a turn lands in the queue.</summary>
    public static string QueuedStatus => "Queued — will send when this turn ends";

    /// <summary>Inline status when the queue is at its limit.</summary>
    public static string QueueFullStatus =>
        $"Queue is full ({AgentTurnQueuePolicy.MaxTurns}). Wait for this turn or remove one.";
}
