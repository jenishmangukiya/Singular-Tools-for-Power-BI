using System;

namespace SingularTools_App.Shell;

public enum ToastSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public sealed class ToastRequest
{
    public ToastRequest(string message, ToastSeverity severity, int durationMs)
    {
        Message = message;
        Severity = severity;
        DurationMs = durationMs;
    }

    public string Message { get; }
    public ToastSeverity Severity { get; }
    public int DurationMs { get; }
}

/// <summary>
/// App-wide entry point for temporary notifications. The shell subscribes to
/// <see cref="Requested"/> and renders each request as a non-blocking overlay
/// toast, so notifications never reflow or intercept the underlying UI.
/// </summary>
public static class ToastService
{
    public static event Action<ToastRequest>? Requested;

    public static void Show(string message, ToastSeverity severity = ToastSeverity.Informational, int? durationMs = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var duration = durationMs ?? severity switch
        {
            ToastSeverity.Error => 6000,
            ToastSeverity.Warning => 6000,
            _ => 4000
        };

        Requested?.Invoke(new ToastRequest(message, severity, duration));
    }
}
