using System;

namespace WKI_Clipper.Services;

/// <summary>
/// "Is a game in the foreground right now?" for the option that holds a Discord compress back while playing.
///
/// There is ONE definition of "a game is in front": <see cref="GameDetector"/> (blocklist, window covers its
/// monitor or is the Window-mode target, the clipper's own windows never change it, 1.5 s debounce). This is
/// only a thin adapter so the compress wait asks the same question as the overlay, the FPS measurement and the
/// game profiles. It used to carry a second, slightly different heuristic.
/// </summary>
internal static class GameForegroundDetector
{
    /// <param name="detector">The live game detector; null while the app is still starting = no game.</param>
    public static bool IsGameInForeground(GameDetector? detector)
    {
        if (detector is null) return false;
        try
        {
            // A game that quit while one of our windows (the gallery the compress was started from) had the
            // focus produces no foreground change; the cheap liveness check lets the wait end anyway.
            detector.Verify();
            return detector.Current is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn("Game-in-foreground check failed: " + ex.Message);
            return false;   // when in doubt do not hold the job back forever
        }
    }
}
