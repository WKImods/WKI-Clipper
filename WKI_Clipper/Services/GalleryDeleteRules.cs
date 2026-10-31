using WKI_Clipper.Views;

namespace WKI_Clipper.Services;

/// <param name="ToolTip">What the item does when it is enabled, otherwise why it is not.</param>
public readonly record struct DeleteItemState(bool Enabled, string ToolTip);

/// <summary>
/// The decisions of the gallery's "Löschen" that need no shell or window: whether the menu item can run and why not,
/// what each Recycle Bin outcome tells the user, and which outcomes take the row away. Pure so each one is a unit test.
/// </summary>
public static class GalleryDeleteRules
{
    /// <param name="fileExists">The file is still there (a file deleted elsewhere is not "a drive without a bin").</param>
    /// <param name="recyclable"><see cref="RecycleBin.IsSupportedPath"/>: a local drive with a Recycle Bin and the file on it.</param>
    /// <param name="compressing">A Discord compress of this file is queued or running (it reads the file).</param>
    /// <param name="deleting">A delete of this file is still running (in use: it is tried again for a moment).</param>
    public static DeleteItemState ItemState(bool fileExists, bool recyclable, bool compressing, bool deleting)
    {
        if (deleting)
            return new(false, L.T("Wird gerade in den Papierkorb verschoben…", "Being moved to the Recycle Bin…"));
        if (!fileExists)
            return new(false, L.T("Die Datei gibt es nicht mehr. „Aktualisieren“ räumt die Liste auf.",
                                  "The file no longer exists. “Refresh” tidies up the list."));
        // A missing bin outlasts a compress, so it is the reason worth naming.
        if (!recyclable)
            return new(false, L.T("Auf diesem Laufwerk gibt es keinen Papierkorb. Über „Im Ordner zeigen“ selbst löschen.",
                                  "This drive has no Recycle Bin. Use “Show in folder” to delete it yourself."));
        if (compressing)
            return new(false, L.T("Dieser Clip wird gerade komprimiert. Danach lässt er sich löschen.",
                                  "This clip is being compressed. You can delete it once that is done."));
        return new(true, L.T("Verschiebt die Datei in den Papierkorb. Von dort lässt sie sich wiederherstellen.",
                             "Moves the file to the Recycle Bin. You can restore it from there."));
    }

    public static string ToastTitle => L.T("Galerie", "Gallery");

    /// <summary>What happened to the file; only a verified move says "in the Recycle Bin".</summary>
    public static string Message(RecycleOutcome outcome, string fileName) => outcome switch
    {
        RecycleOutcome.Recycled => L.T($"„{fileName}“ liegt im Papierkorb.", $"“{fileName}” is in the Recycle Bin."),
        RecycleOutcome.InUse => L.T($"Die Datei „{fileName}“ ist in Verwendung und wurde behalten.",
                                    $"The file “{fileName}” is in use and was kept."),
        RecycleOutcome.NotSupported => L.T(
            $"Die Datei „{fileName}“ ließ sich nicht in den Papierkorb verschieben (zu groß dafür oder der Papierkorb ist ausgeschaltet) und wurde behalten.",
            $"The file “{fileName}” could not go to the Recycle Bin (too big for it, or the bin is switched off) and was kept."),
        RecycleOutcome.Declined => L.T($"Nicht endgültig gelöscht — die Datei „{fileName}“ wurde behalten.",
                                       $"Not deleted permanently — the file “{fileName}” was kept."),
        RecycleOutcome.DeletedPermanently => L.T($"Die Datei „{fileName}“ wurde gelöscht, liegt aber nicht im Papierkorb.",
                                                 $"The file “{fileName}” was deleted but is not in the Recycle Bin."),
        _ => L.T($"Die Datei „{fileName}“ konnte nicht in den Papierkorb verschoben werden und wurde behalten.",
                 $"The file “{fileName}” could not be moved to the Recycle Bin and was kept.")
    };

    public static ToastKind ToastKindOf(RecycleOutcome outcome) => outcome == RecycleOutcome.Recycled ? ToastKind.Info : ToastKind.Warning;

    /// <summary>A warning is a sentence to read, not a glance: it stays longer.</summary>
    public static double ToastSeconds(RecycleOutcome outcome) => outcome == RecycleOutcome.Recycled ? 4 : 8;

    /// <summary>
    /// Switched-off toasts only silence the good news: the row disappearing says "done". A file that was kept, or one
    /// gone without landing in the bin, has no other way to reach the user.
    /// </summary>
    public static bool ShowsToast(RecycleOutcome outcome, bool toastsOn) => toastsOn || outcome != RecycleOutcome.Recycled;

    /// <summary>The file is gone (in the bin or deleted for good): the gallery reloads and the row goes.</summary>
    public static bool RemovesFile(RecycleOutcome outcome) => outcome is RecycleOutcome.Recycled or RecycleOutcome.DeletedPermanently;
}
