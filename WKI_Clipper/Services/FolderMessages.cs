namespace WKI_Clipper.Services;

/// <summary>
/// The texts for <see cref="FolderProblem"/>s: what the Paths page says when it refuses an input, and the standing
/// line under a folder that was saved before the rules existed. Kept apart from the view so the wording is tested.
/// <c>leaf</c> is the suggested last folder name: "buffer", "Clips" or "Screenshots".
/// </summary>
internal static class FolderMessages
{
    private static string Sample(string leaf, string? typed)
        => PathRules.SuggestFolder(leaf, typed, L.T("<Laufwerk>", "<drive>"));

    /// <summary>Why <paramref name="typed"/> is refused, with a folder that would be accepted. Ends with "not applied".</summary>
    public static string Refusal(FolderProblem problem, string leaf, string? typed)
    {
        var sample = Sample(leaf, typed);
        return problem switch
        {
            FolderProblem.NotFullyQualified => L.T(
                $"Bitte einen vollständigen Pfad mit Laufwerk angeben, z. B. {sample}. Ein Laufwerk allein („D:“) oder ein relativer Pfad zeigt je nach Programm auf verschiedene Ordner.\n\nDie Änderung wurde nicht übernommen.",
                $"Please enter a full path including the drive, e.g. {sample}. A bare drive (“D:”) or a relative path points to different folders depending on the program.\n\nThe change was not applied."),
            FolderProblem.DriveRoot => L.T(
                $"Der Buffer-Ordner darf nicht das Hauptverzeichnis eines Laufwerks sein („D:\\“) — der Buffer schreibt laufend viele Dateien. Nimm einen Unterordner, z. B. {sample}.\n\nDie Änderung wurde nicht übernommen.",
                $"The buffer folder must not be the root of a drive (“D:\\”) — the buffer keeps writing many files. Use a subfolder, e.g. {sample}.\n\nThe change was not applied."),
            FolderProblem.InstallDirectory => L.T(
                $"Bitte nicht den Programmordner verwenden — Updates und Deinstallation arbeiten dort. Nimm z. B. {sample}.\n\nDie Änderung wurde nicht übernommen.",
                $"Please do not use the program folder — updates and uninstall work there. Use e.g. {sample}.\n\nThe change was not applied."),
            _ => ""
        };
    }

    /// <summary>
    /// The line that stays under a folder that was saved earlier and breaks a rule. Null when the folder is fine.
    /// The buffer is replaced by the default for the run; a clips or screenshots folder is NOT moved - the files in it
    /// stay where they are, the user is only told.
    /// </summary>
    public static string? StandingWarning(FolderProblem problem, string leaf, string? saved, string bufferDefault)
    {
        if (problem == FolderProblem.None) return null;
        var sample = Sample(leaf, saved);

        if (leaf == "buffer")
        {
            var why = problem switch
            {
                FolderProblem.NotFullyQualified => L.T("kein vollständiger Pfad", "not a full path"),
                FolderProblem.DriveRoot => L.T("Hauptverzeichnis eines Laufwerks", "root of a drive"),
                _ => L.T("im Programmordner", "inside the program folder")
            };
            return L.T(
                $"Der gespeicherte Buffer-Ordner „{saved}“ ist nicht nutzbar ({why}) — der Buffer läuft stattdessen im Standardordner {bufferDefault}. Bitte einen vollständigen Ordner wählen, z. B. {sample}.",
                $"The saved buffer folder “{saved}” cannot be used ({why}) — the buffer runs in the default folder {bufferDefault} instead. Please choose a full folder, e.g. {sample}.");
        }

        return problem == FolderProblem.InstallDirectory
            ? L.T(
                $"Hinweis: Dieser Ordner liegt im Programmordner. Es wird nichts verschoben — bitte wähle aber einen anderen Ordner (z. B. {sample}): Updates und Deinstallation arbeiten im Programmordner.",
                $"Note: this folder is inside the program folder. Nothing is moved — but please choose another folder (e.g. {sample}): updates and uninstall work in the program folder.")
            : L.T(
                $"Hinweis: Dieser Pfad ist nicht vollständig (z. B. nur ein Laufwerk) und zeigt je nach Programm auf verschiedene Ordner. Bitte einen vollständigen Pfad wählen, z. B. {sample}.",
                $"Note: this path is not complete (for example only a drive) and points to different folders depending on the program. Please choose a full path, e.g. {sample}.");
    }
}
