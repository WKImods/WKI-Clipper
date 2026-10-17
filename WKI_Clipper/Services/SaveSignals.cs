namespace WKI_Clipper.Services;

/// <summary>Which save a <see cref="ReplayBufferService"/> signal is about.</summary>
public enum SaveKind { Clip, Gif }

/// <summary>Why a clip/GIF save did not produce a file.</summary>
public enum SaveFailureReason
{
    /// <summary>Another save is still running.</summary>
    Busy,
    /// <summary>The buffer is off (or restarting).</summary>
    NotRunning,
    /// <summary>The ring stopped producing data - the clip would be stale.</summary>
    Stale,
    /// <summary>No complete segment yet (buffer just started or restarted).</summary>
    NotEnoughMaterial,
    /// <summary>The concat/encode was killed: it hung, ran over its cap or copied slower than the recording.</summary>
    Aborted,
    /// <summary>ffmpeg failed, was missing, or an unexpected error interrupted the save.</summary>
    Failed,
    /// <summary>The clip exists but could not be given its final name (it is still there as .part).</summary>
    RenameFailed
}

/// <summary>A refused or failed save. Distinct from the free-text BufferInfo/BufferError notices.</summary>
public readonly record struct SaveFailure(SaveKind Kind, SaveFailureReason Reason);
