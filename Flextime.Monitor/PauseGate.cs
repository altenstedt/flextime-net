namespace Flextime.Monitor;

public enum PauseStep
{
    /// <summary>Not paused: measure.</summary>
    Measure,

    /// <summary>A pause begins: write the stop, once.</summary>
    Enter,

    /// <summary>Still paused: write nothing.</summary>
    Skip,

    /// <summary>The pause is over: write the start, once.</summary>
    Leave,
}

/// <summary>
/// Turns what the pause file says each tick into what listen does, so a
/// stop and a start are written once per change, however the file
/// comes and goes.
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
