using System;

namespace WKI_Clipper.Services;

/// <summary>
/// "Is a game in the foreground right now?" for the option that holds a Discord compress back while playing.
///
/// There is ONE definition of "a game is in front": <see cref="GameDetector"/> (blocklist, window covers its
/// monitor or is the Window-mode target, the clipper's own windows and shell transients never change it). This
/// is only a thin adapter so the compress wait asks the same question as the overlay, the FPS measurement and
/// the game profiles. It used to carry a second, slightly different heuristic.
/// </summary>
internal static class GameForegroundDetector
{
    /// <param name="detector">The live game detector; null while the app is still starting = no game.</param>
    public static bool IsGameInForeground(GameDetector? detector)
    {
        if (detector is null) return false;
        try
        {
            // Live, not debounced: re-probes the window in front (or, with the gallery open over the game, the
            // game underneath) with the detector's own rules, and drops a game that quit behind our window.
            return detector.IsGameInFrontNow();
        }
        catch (Exception ex)
        {
            Logger.Warn("Game-in-foreground check failed: " + ex.Message);
            return false;   // when in doubt do not hold the job back forever
        }
    }
}
