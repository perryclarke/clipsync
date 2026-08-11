using System;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// What the suppression decision needs to know about the foreground: which
/// app was in front at a given instant. `ForegroundRing` and
/// `ForegroundTracker` both satisfy it, so tests can drive the decision from
/// a ring populated synthetically instead of from real windows.
public interface IForegroundSource
{
    AppIdentity? AppAt(DateTime utc);
}

/// The one decision the excluded-apps feature exists to make: given the
/// instant a copy happened, may the item be transmitted to peers?
///
/// Lives here, apart from the clipboard plumbing, because it is the
/// assertion the feature is judged on and it must be directly testable.
public static class SuppressionPolicy
{
    /// True when the item must NOT be transmitted.
    ///
    /// Fails open in every uncertain case: an unresolved source app, or any
    /// exception on the way to the answer, yields false (transmit). Silent
    /// non-delivery is a worse failure than a rare miss, and a confidently
    /// wrong identity is worse than none.
    ///
    /// `source` is the resolved app, or null when it could not be
    /// determined. It is presentation/logging data only — never clipboard
    /// content — and is set whether or not the item is suppressed.
    public static bool ShouldSuppress(IForegroundSource foreground, AppSettings settings,
                                      DateTime copiedAtUtc, out AppIdentity? source)
    {
        source = null;
        try
        {
            source = foreground.AppAt(copiedAtUtc);
            if (source is null) return false;
            return settings.IsExcluded(source);
        }
        catch (Exception ex)
        {
            Security.Log.Write($"SuppressionPolicy: decision failed, transmitting: {ex.GetType().Name}");
            return false;
        }
    }
}
