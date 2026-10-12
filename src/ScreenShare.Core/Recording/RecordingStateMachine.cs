namespace SocScreenShare.Recording;

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
}

/// <summary>
/// Los estados de la grabación y qué se puede hacer en cada uno. La interfaz, la bandeja y el atajo
/// de teclado preguntan aquí antes de hacer nada, así que un doble clic o el atajo pulsado mientras
/// arranca no lanzan dos grabaciones ni paran una que no ha empezado.
/// </summary>
public sealed class RecordingStateMachine
{
    private readonly object _gate = new();

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public event Action<RecordingState>? Changed;

    public bool CanStart => State == RecordingState.Idle;
    public bool CanPause => State == RecordingState.Recording;
    public bool CanResume => State == RecordingState.Paused;
    public bool CanStop => State is RecordingState.Recording or RecordingState.Paused;
    public bool IsBusy => State != RecordingState.Idle;

    public bool TryStart() => Move(RecordingState.Idle, RecordingState.Starting);

    public bool Started() => Move(RecordingState.Starting, RecordingState.Recording);

    public bool TryPause() => Move(RecordingState.Recording, RecordingState.Paused);

    public bool TryResume() => Move(RecordingState.Paused, RecordingState.Recording);

    public bool TryStop() => Move(RecordingState.Recording, RecordingState.Stopping) || Move(RecordingState.Paused, RecordingState.Stopping);

    public bool Stopped() => Move(RecordingState.Stopping, RecordingState.Idle);

    /// <summary>Algo falló (al arrancar o grabando): vuelta a reposo desde donde sea.</summary>
    public void Failed() => Set(RecordingState.Idle);

    private bool Move(RecordingState from, RecordingState to)
    {
        lock (_gate)
        {
            if (State != from)
                return false;
            State = to;
        }
        Changed?.Invoke(to);
        return true;
    }

    private void Set(RecordingState to)
    {
        lock (_gate)
        {
            if (State == to)
                return;
            State = to;
        }
        Changed?.Invoke(to);
    }
}
