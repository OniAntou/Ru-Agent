namespace RuAgent.Cli.TerminalGui;

internal enum TgWelcomeEffects { On, Low, Off }

internal sealed class TgWelcomeEffectsState
{
    internal TgWelcomeEffects Mode { get; set; }
    internal void Advance() => Mode = Mode switch
    {
        TgWelcomeEffects.On => TgWelcomeEffects.Low,
        TgWelcomeEffects.Low => TgWelcomeEffects.Off,
        _ => TgWelcomeEffects.On,
    };
}
