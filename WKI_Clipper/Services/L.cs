using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// Minimal two-language helper. All user-facing strings are written inline as
/// <c>L.T("deutsch", "english")</c> — the translation lives right at the usage
/// site, which keeps the procedural UI code reviewable and makes missed strings
/// easy to grep. Initialized from settings at startup and re-evaluated on every
/// settings save, so services pick the language up live; the build-once views
/// apply it fully after an app restart.
/// </summary>
public static class L
{
    public static bool English { get; private set; }

    public static void Init(AppSettings settings)
        => English = settings.Behavior.Language == AppLanguage.English;

    public static string T(string de, string en) => English ? en : de;
}
