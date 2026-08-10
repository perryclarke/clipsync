using System;

namespace ClipSync.Security;

/// Logging seam for Core. The app assigns Sink at startup so Core's
/// diagnostics land in the same opt-in debug.log as everything else.
/// Messages must never contain clipboard content.
public static class Log
{
    public static Action<string>? Sink;

    public static void Write(string message)
    {
        try { Sink?.Invoke(message); } catch { }
    }
}
