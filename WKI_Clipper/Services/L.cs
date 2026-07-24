using System;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Minimal two-language helper. All user-facing strings are written inline as
/// <c>L.T("deutsch", "english")</c> — the translation lives right at the usage
/// site, which keeps the procedural UI code reviewable and makes missed strings
/// easy to grep. Initialized from settings at startup and re-evaluated on every
/// settings save. <see cref="LanguageChanged"/> fires when the language actually
/// flips so build-once views/windows can rebuild live (no restart needed).
/// </summary>
public static class L
{
    public static bool English { get; private set; }

    /// <summary>Raised when the effective language changed (not on every settings save).</summary>
    public static event Action? LanguageChanged;

    public static void Init(AppSettings settings)
    {
        bool next = settings.Behavior.Language == AppLanguage.English;
        if (next == English) return;
        English = next;
        LanguageChanged?.Invoke();
    }

    public static string T(string de, string en) => English ? en : de;
}
