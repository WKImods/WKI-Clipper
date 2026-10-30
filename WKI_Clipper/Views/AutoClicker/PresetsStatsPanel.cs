using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>"Presets": the three factory presets, the user presets (apply, save as, rename, delete, export/import) and the statistics.</summary>
public sealed class PresetsStatsPanel : ClickerPanel
{
    /// <summary>Runs listed below the totals (the settings keep up to 100).</summary>
    private const int ShownRuns = 20;

    private readonly TextBox _name = new() { Width = 220, MaxLength = ClickerSettingsRules.MaxNameLength, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _userRows = new();
    private readonly TextBlock _userEmpty;
    private readonly TextBlock _totals;
    private readonly StackPanel _runRows = new();
    private readonly TextBlock _runsEmpty;
    private string? _presetsShown, _statsShown;

    public PresetsStatsPanel()
    {
        var factory = Section(L.T("Vorgaben", "Built-in presets"),
            L.T("Setzen nur Geschwindigkeit, Haltedauer, Zufall und Klick-Limit.", "Set only speed, hold time, randomization and click limit."));
        foreach (var preset in ClickerPresets.Factory())
        {
            var p = preset;
            var detail = L.T($"{ClickerNumber.Format(p.ClickSpeed)} pro Sekunde · Haltedauer {ClickerNumber.Format(p.DutyCyclePercent)} % · Zufall {ClickerNumber.Format(p.SpeedRandomizationPercent)} %",
                             $"{ClickerNumber.Format(p.ClickSpeed)} per second · hold {ClickerNumber.Format(p.DutyCyclePercent)} % · random {ClickerNumber.Format(p.SpeedRandomizationPercent)} %")
                + (p.ClickLimitEnabled ? L.T($" · Limit {p.ClickLimit} Klicks", $" · limit {p.ClickLimit} clicks") : "");
            factory.Children.Add(ListRow(TwoLines(p.Name, detail), Btn(L.T("Anwenden", "Apply"), () => Commit(c => ClickerPresets.ApplyFactory(p, c)))));
        }

        var user = Section(L.T("Eigene Presets", "Your presets"), L.T(
            "Speichern alle Klick-, Schutz-, Zonen-, Klickpunkt- und Whitelist-Einstellungen - keine Hotkeys.",
            "Store all click, failsafe, zone, click-point and whitelist settings - no hotkeys."));
        user.Children.Add(Row(L.T("Name", "Name"), _name,
            Btn(L.T("Als Preset speichern", "Save as preset"), SaveAs, accent: true,
                tooltip: L.T("Ein Preset mit demselben Namen wird überschrieben.", "A preset of the same name is overwritten.")),
            Btn(L.T("Importieren…", "Import…"), Import)));
        _userRows.Margin = new Thickness(0, 10, 0, 0);
        user.Children.Add(_userRows);
        _userEmpty = Muted(L.T("Noch kein eigenes Preset.", "No preset of your own yet."), new Thickness(0, 6, 0, 0));
        user.Children.Add(_userEmpty);

        var stats = Section(L.T("Statistik", "Statistics"));
        _totals = Label("");
        stats.Children.Add(_totals);
        _runRows.Margin = new Thickness(0, 8, 0, 0);
        stats.Children.Add(_runRows);
        _runsEmpty = Muted(L.T("Noch keine Läufe.", "No runs yet."), new Thickness(0, 6, 0, 0));
        stats.Children.Add(_runsEmpty);
        stats.Children.Add(Buttons(Btn(L.T("Statistik zurücksetzen", "Reset statistics"), ResetStats)));

        Loaded += (_, _) => Refresh();
    }

    protected override void OnRefresh(ClickerSettings c)
    {
        RenderPresets(c);
        RenderStats(c);
    }

    // ---------------- user presets ----------------

    private void RenderPresets(ClickerSettings c)
    {
        var key = string.Join("|", c.Presets.Select(p => p.Name));
        if (key == _presetsShown) return;
        _presetsShown = key;
        _userRows.Children.Clear();
        _userEmpty.Visibility = c.Presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var preset in c.Presets)
        {
            var name = preset.Name;
            var apply = Btn(L.T("Anwenden", "Apply"), () =>
                Commit(s => { if (s.Presets.FirstOrDefault(p => p.Name == name) is { } p) ClickerPresets.Apply(p, s); }), accent: true);
            var rename = Btn(L.T("Umbenennen", "Rename"), () => Rename(name),
                tooltip: L.T("Bekommt den Namen aus dem Feld oben.", "Takes the name from the box above."));
            var export = Btn(L.T("Exportieren…", "Export…"), () => Export(name));
            var delete = Btn(L.T("Löschen", "Delete"), () => Delete(name));
            _userRows.Children.Add(ListRow(TwoLines(name, Describe(preset)), apply, rename, export, delete));
        }
    }

    private static string Describe(ClickerPreset p)
    {
        string rate = p.RateInputMode == RateInputMode.Duration
            ? $"{p.DurationMs} ms"
            : ClickerNumber.Format(p.ClickSpeed) + " " + p.ClickUnit switch
            {
                RateUnit.Minute => L.T("pro Minute", "per minute"),
                RateUnit.Hour => L.T("pro Stunde", "per hour"),
                RateUnit.Day => L.T("pro Tag", "per day"),
                _ => L.T("pro Sekunde", "per second")
            };
        string input = p.InputType == ClickerInput.Keyboard
            ? (p.KeyboardVk > 0 ? ClickerHotkey.KeyName(p.KeyboardVk) : L.T("Tastatur", "Keyboard"))
            : p.MouseButton switch
            {
                ClickerMouseButton.Right => L.T("rechte Maustaste", "right button"),
                ClickerMouseButton.Middle => L.T("mittlere Maustaste", "middle button"),
                _ => L.T("linke Maustaste", "left button")
            };
        return $"{rate} · {input}";
    }

    private void SaveAs()
    {
        var name = _name.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            Notice(L.T("Bitte zuerst einen Namen eingeben.", "Enter a name first."));
            return;
        }
        bool saved = false;
        Commit(c => saved = ClickerPresets.SaveAs(c, name) is not null);
        if (!saved) Notice(L.T($"Höchstens {ClickerSettingsRules.MaxPresets} Presets.", $"At most {ClickerSettingsRules.MaxPresets} presets."));
        else _name.Clear();
    }

    private void Rename(string oldName)
    {
        var newName = _name.Text;
        bool ok = false;
        Commit(c => ok = ClickerPresets.Rename(c.Presets, oldName, newName));
        if (ok) _name.Clear();
        else Notice(string.IsNullOrWhiteSpace(newName)
            ? L.T("Den neuen Namen oben eingeben, dann \"Umbenennen\".", "Enter the new name above, then \"Rename\".")
            : L.T("Diesen Namen hat schon ein anderes Preset.", "Another preset already has that name."));
    }

    private void Delete(string name)
    {
        var answer = MessageBox.Show(Window.GetWindow(this), L.T($"Preset \"{name}\" löschen?", $"Delete preset \"{name}\"?"),
            "AutoClicker", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Commit(c => ClickerPresets.Delete(c.Presets, name));
    }

    private void Export(string name)
    {
        if (Clicker?.Presets.FirstOrDefault(p => p.Name == name) is not { } preset) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("Preset exportieren", "Export preset"),
            FileName = SafeFileName(name) + ".json",
            Filter = L.T("Preset (*.json)|*.json", "Preset (*.json)|*.json"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, ClickerPresets.Export(preset)); }
        catch (Exception ex)
        {
            Logger.Warn("AutoClicker preset export failed: " + ex.Message);
            Notice(L.T("Speichern fehlgeschlagen: ", "Saving failed: ") + ex.Message);
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Preset importieren", "Import preset"),
            Filter = L.T("Preset (*.json)|*.json|Alle Dateien (*.*)|*.*", "Preset (*.json)|*.json|All files (*.*)|*.*"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        string json;
        try
        {
            // A huge file is refused before it is read in full.
            if (new FileInfo(dialog.FileName).Length > ClickerPresets.MaxImportLength * 4L) { Notice(ImportError(PresetImportError.TooLarge)); return; }
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            Logger.Warn("AutoClicker preset import failed: " + ex.Message);
            Notice(L.T("Lesen fehlgeschlagen: ", "Reading failed: ") + ex.Message);
            return;
        }

        PresetImportResult? result = null;
        Commit(c => result = ClickerPresets.Import(c.Presets, json));
        if (result is { Success: true, Preset: { } added })
            ToastService.Show(ToastKind.Info, "AutoClicker", L.T($"Preset \"{added.Name}\" importiert.", $"Preset \"{added.Name}\" imported."), durationSeconds: 4);
        else if (result is { } failed)
            Notice(ImportError(failed.Error));
    }

    private static string ImportError(PresetImportError error) => error switch
    {
        PresetImportError.UnknownVersion => L.T("Diese Preset-Datei stammt aus einer anderen Version.", "That preset file comes from another version."),
        PresetImportError.MissingPreset => L.T("In der Datei steht kein Preset.", "The file holds no preset."),
        PresetImportError.TooLarge => L.T("Die Datei ist zu groß für ein Preset.", "The file is too large for a preset."),
        PresetImportError.ListFull => L.T($"Höchstens {ClickerSettingsRules.MaxPresets} Presets.", $"At most {ClickerSettingsRules.MaxPresets} presets."),
        _ => L.T("Keine gültige Preset-Datei.", "Not a valid preset file.")
    };

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return clean.Length == 0 ? "Preset" : clean;
    }

    private static void Notice(string text) => ToastService.Show(ToastKind.Warning, "AutoClicker", text, durationSeconds: 5);

    // ---------------- statistics ----------------

    private void RenderStats(ClickerSettings c)
    {
        var st = c.Stats;
        var key = $"{st.TotalClicks};{st.TotalSeconds};{st.Sessions};{st.LastRuns.Count};{st.LastRuns.FirstOrDefault()?.StartUtc.Ticks};{L.English}";
        if (key == _statsShown) return;
        _statsShown = key;

        var culture = CultureInfo.GetCultureInfo(L.English ? "en-US" : "de-DE");
        _totals.Text = L.T(
            $"{st.TotalClicks.ToString("N0", culture)} Klicks in {st.Sessions.ToString("N0", culture)} Läufen · {ClickerStatusRules.Length(st.TotalSeconds)} insgesamt",
            $"{st.TotalClicks.ToString("N0", culture)} clicks in {st.Sessions.ToString("N0", culture)} runs · {ClickerStatusRules.Length(st.TotalSeconds)} in total");

        _runRows.Children.Clear();
        _runsEmpty.Visibility = st.LastRuns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var run in st.LastRuns.Take(ShownRuns))
        {
            var when = run.StartUtc.ToLocalTime().ToString(L.English ? "MM/dd HH:mm" : "dd.MM. HH:mm", culture);
            var line = $"{when} · {ClickerStatusRules.Length(run.Seconds)} · {run.Clicks.ToString("N0", culture)} {L.T("Klicks", "clicks")}";
            var reason = ClickerStatusRules.RunReason(run.StopReason);
            _runRows.Children.Add(TwoLines(line, reason));
        }
    }

    private void ResetStats()
    {
        var answer = MessageBox.Show(Window.GetWindow(this), L.T("Statistik zurücksetzen?", "Reset the statistics?"),
            "AutoClicker", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Commit(c => c.Stats = new ClickerStats());
    }
}
