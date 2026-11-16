namespace Flextime.Monitor;

public enum PauseStep
{
    /// <summary>Not paused: measure.</summary>
    Measure,

    /// <summary>A pause begins: from now on, write nothing.</summary>
    Enter,

    /// <summary>Still paused: write nothing.</summary>
    Skip,

    /// <summary>The pause is over: measure, as after a wake.</summary>
    Leave,
}

/// <summary>
/// Turns what the pause file says each tick into what listen does, so
/// the start and end of a pause are each seen once per change, however
/// the file comes and goes.
/// </summary>
public sealed class PauseGate
{
    private volatile bool paused;

    public bool Paused => paused;

    public PauseStep Next(bool pausedNow)
    {
        var step = (paused, pausedNow) switch
        {
            (false, false) => PauseStep.Measure,
            (false, true) => PauseStep.Enter,
            (true, true) => PauseStep.Skip,
            _ => PauseStep.Leave,
        };

        paused = pausedNow;

        return step;
    }
}
