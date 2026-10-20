using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The title line of a gallery row: the note in SemiBold, turning into an inline TextBox while it
/// is edited. Enter saves, Esc cancels. Leaving the box also saves — a stray click must not throw
/// away text someone just typed (the notes can be evidence).
/// If the note could not be saved (file locked, disk full) the box STAYS OPEN and says so, so the
/// text is never shown as saved when it is not. Esc then takes that failed edit back out of the
/// store - through its own discard path, never by saving the note the row happens to show (after an
/// unreadable notes file that is not the stored note).
/// Moving the focus to the box's own Cut/Copy/Paste menu does not end the edit: the pasted text has to
/// land in the box that is still open.
/// Collapsed while there is no note and nobody is editing, so a note-less row looks as before.
/// </summary>
internal sealed class RowNoteTitle : Border
{
    private readonly Func<string, bool> _save;
    private readonly Action _discard;
    private readonly TextBlock _label;
    private TextBox? _box;
    private string _note;
    private bool _saveFailed;

    /// <summary>Raised with the stored note after an edit changed it.</summary>
    public event Action<string>? NoteChanged;

    /// <summary>Raised when the editor has closed (saved, cancelled or left), with <see cref="IsEditing"/> already false.</summary>
    public event Action? EditEnded;

    /// <param name="save">Persists the text; false = it did NOT reach the disk.</param>
    /// <param name="discard">Takes back an edit whose save failed. Writes nothing.</param>
    public RowNoteTitle(string note, Func<string, bool> save, Action discard)
    {
        _note = note;
        _save = save;
        _discard = discard;
        _label = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Child = _label;
        Render();
    }

    public string Note => _note;
    public bool IsEditing => _box != null;
    /// <summary>The last Enter/blur could not save; the box is still open.</summary>
    public bool SaveFailed => _saveFailed;

    public void BeginEdit()
    {
        if (_box != null) return;

        var box = new TextBox
        {
            Text = _note,
            MaxLength = GalleryMetaService.MaxNoteLength,
            FontWeight = FontWeights.SemiBold,
            MinHeight = 26,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 0, 3),
            ToolTip = L.T("Notiz — Enter speichert, Esc bricht ab", "Note — Enter saves, Esc cancels")
        };
        box.PreviewKeyDown += OnKeyDown;
        box.LostKeyboardFocus += (_, e) => OnBoxLostFocus(e.NewFocus);

        _box = box;
        _saveFailed = false;
        Child = box;
        Visibility = Visibility.Visible;

        // After the click (or the context menu that started the edit) has finished moving focus around.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_box != box) return;
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key)) e.Handled = true;
    }

    /// <summary>The box lost the keyboard focus: leaving it saves, unless the focus only went to the box's own menu.</summary>
    internal void OnBoxLostFocus(IInputElement? newFocus)
    {
        if (_box is not { } box) return;
        if (KeepsEditingWhenFocusMovesTo(newFocus, box)) return;
        Commit();
    }

    /// <summary>
    /// True when the focus went to the Cut/Copy/Paste menu of <paramref name="box"/> itself (or one of its items):
    /// the menu takes the keyboard focus while it is open and gives it back on close, and the command it runs
    /// targets this very box. Everything else - another control, a menu of another row, and also "nothing" (null) -
    /// ends the edit. Null must save: a refresh that removes the row while the box has the focus reports it, and
    /// skipping the save there would throw away what was typed.
    /// </summary>
    internal static bool KeepsEditingWhenFocusMovesTo(IInputElement? newFocus, TextBox box)
        => OwningMenu(newFocus as DependencyObject) is { } menu && ReferenceEquals(menu.PlacementTarget, box);

    private static ContextMenu? OwningMenu(DependencyObject? d)
    {
        for (int hops = 0; d != null && hops < 64; hops++)
        {
            if (d is ContextMenu menu) return menu;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Enter saves, Esc cancels. True when the key was one of those.</summary>
    internal bool HandleKey(Key key)
    {
        if (key == Key.Enter) { Commit(); return true; }
        if (key == Key.Escape) { Cancel(); return true; }
        return false;
    }

    /// <summary>
    /// Saves the box content and shows it as the title again. No-op when not editing. When the save
    /// fails the box stays open with a warning.
    /// </summary>
    public void Commit()
    {
        if (_box is not { } box) return;
        var text = box.Text;
        var stored = GalleryMetaService.NormalizeNote(text);

        // After a failed save the service holds the failed text even if the box now matches the old note again.
        if (stored != _note || _saveFailed)
        {
            bool saved;
            try { saved = _save(text); }
            catch (Exception ex)
            {
                Logger.Error("Saving the gallery note failed", ex);
                saved = false;
            }

            if (!saved)
            {
                ShowSaveFailed(box);
                return;
            }
        }

        Leave();
        if (stored == _note) return;
        _note = stored;
        Render();
        NoteChanged?.Invoke(stored);
    }

    public void Cancel()
    {
        if (_box == null) return;

        // The service keeps a note whose save failed (and retries it) — cancelling has to take it back. Not by saving
        // _note again: that is what the row shows, and after an unreadable notes file it is not what is stored.
        if (_saveFailed)
        {
            try { _discard(); } catch (Exception ex) { Logger.Warn("Discarding the unsaved note failed: " + ex.Message); }
        }
        Leave();
    }

    private void ShowSaveFailed(TextBox box)
    {
        _saveFailed = true;
        box.Foreground = box.TryFindResource("DangerBrush") as Brush ?? Brushes.IndianRed;
        box.ToolTip = L.T("Konnte nicht gespeichert werden (Datei gesperrt?). Der Text bleibt erhalten — Enter versucht es erneut.",
                          "Could not be saved (file locked?). The text is kept — press Enter to try again.");
    }

    private void Leave()
    {
        // _box first: replacing the child makes the box lose focus, which calls Commit() again.
        _box = null;
        _saveFailed = false;
        Child = _label;
        Render();
        EditEnded?.Invoke();
    }

    private void Render()
    {
        _label.Text = _note;
        _label.ToolTip = _note.Length == 0 ? null : _note;
        Visibility = _note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
