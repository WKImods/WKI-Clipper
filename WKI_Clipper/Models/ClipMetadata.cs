using System;

namespace WKI_Clipper.Models;

public sealed class ClipMetadata
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public DateTime CreatedAt { get; init; }
    public long FileSizeBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ThumbnailPath { get; set; }
    public ClipKind Kind { get; init; }
}

public enum ClipKind { Replay, ManualRecording, Screenshot }
