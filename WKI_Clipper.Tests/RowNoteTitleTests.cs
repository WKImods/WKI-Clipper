using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace WKI_Clipper.Tests;

/// <summary>Shared by every test that needs the WPF application object (there can be only one).</summary>
internal static class WpfTestSupport
{
    private static readonly object Gate = new();

    public static void EnsureTheme()
    {
        lock (Gate)
        {
            if (Application.Current != null) return;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/WKI_Clipper;component/Views/Theme.xaml", UriKind.Relative)));
        }
    }

    public static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception("STA body failed", error);
        return result;
    }
}

/// <summary>The inline note editor of a gallery row, driven without a window.</summary>
[Collection("WpfApplication")]
public sealed class RowNoteTitleTests
{
    private static bool Store(string text) => true;
    private static void NoDiscard() { }

    [Fact]
    public void Without_a_note_the_title_takes_no_room()
    {
        var (visible, text) = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("", Store, NoDiscard);
            return (t.Visibility == Visibility.Visible, t.Note);
        });
        Assert.False(visible);
        Assert.Equal("", text);
    }

    [Fact]
    public void With_a_note_it_is_shown_semibold()
    {
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("cheater at the airfield", Store, NoDiscard);
            var label = (TextBlock)t.Child;
            return (t.Visibility, label.Text, label.FontWeight == FontWeights.SemiBold);
        });
        Assert.Equal(Visibility.Visible, info.Visibility);
        Assert.Equal("cheater at the airfield", info.Text);
        Assert.True(info.Item3);
    }

    [Fact]
    public void Editing_shows_a_limited_text_box_with_the_current_note()
    {
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("old", Store, NoDiscard);
            t.BeginEdit();
            var box = (TextBox)t.Child;
            return (t.IsEditing, box.Text, box.MaxLength);
        });
        Assert.True(info.IsEditing);
        Assert.Equal("old", info.Text);
        Assert.Equal(GalleryMetaService.MaxNoteLength, info.MaxLength);
    }

    [Fact]
    public void Enter_saves_the_typed_text_and_shows_it_as_the_title()
    {
        var saved = new List<string>();
        var raised = new List<string>();
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("", s => { saved.Add(s); return true; }, NoDiscard);
            t.NoteChanged += raised.Add;
            t.BeginEdit();
            Assert.Equal(Visibility.Visible, t.Visibility);   // visible while editing although empty
            ((TextBox)t.Child).Text = "  spawn-killing at the bridge  ";

            bool handled = t.HandleKey(Key.Enter);
            return (handled, t.IsEditing, t.Note, ((TextBlock)t.Child).Text, t.Visibility);
        });

        Assert.True(info.handled);
        Assert.False(info.IsEditing);
        Assert.Equal("spawn-killing at the bridge", info.Note);
        Assert.Equal("spawn-killing at the bridge", info.Item4);
        Assert.Equal(Visibility.Visible, info.Visibility);
        Assert.Single(saved);
        Assert.Equal(new[] { "spawn-killing at the bridge" }, raised);
    }

    [Fact]
    public void Escape_cancels_without_saving()
    {
        var saved = new List<string>();
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("keep", s => { saved.Add(s); return true; }, NoDiscard);
            t.BeginEdit();
            ((TextBox)t.Child).Text = "typed but abandoned";

            bool handled = t.HandleKey(Key.Escape);
            return (handled, t.IsEditing, t.Note, ((TextBlock)t.Child).Text);
        });

        Assert.True(info.handled);
        Assert.False(info.IsEditing);
        Assert.Equal("keep", info.Note);
        Assert.Equal("keep", info.Item4);
        Assert.Empty(saved);
    }

    [Fact]
    public void Clearing_the_text_removes_the_note_and_collapses_the_title()
    {
        var visibility = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("old", Store, NoDiscard);
            t.BeginEdit();
            ((TextBox)t.Child).Text = "   ";
            t.HandleKey(Key.Enter);
            return (t.Note, t.Visibility);
        });
        Assert.Equal("", visibility.Note);
        Assert.Equal(Visibility.Collapsed, visibility.Visibility);
    }

    [Fact]
    public void Leaving_the_box_unchanged_does_not_touch_the_store()
    {
        var saved = new List<string>();
        WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("same", s => { saved.Add(s); return true; }, NoDiscard);
            t.BeginEdit();
            t.Commit();   // what losing the keyboard focus does
            return 0;
        });
        Assert.Empty(saved);
    }

    [Fact]
    public void Leaving_the_box_saves_what_was_typed()
    {
        var saved = new List<string>();
        var note = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("", s => { saved.Add(s); return true; }, NoDiscard);
            t.BeginEdit();
            ((TextBox)t.Child).Text = "typed, then clicked elsewhere";
            t.Commit();   // what losing the keyboard focus does
            return t.Note;
        });
        Assert.Equal("typed, then clicked elsewhere", note);
        Assert.Single(saved);
    }

    [Fact]
    public void A_failed_save_keeps_the_editor_open_and_does_not_pretend_it_was_saved()
    {
        var raised = new List<string>();
        int calls = 0;
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("old", _ => { calls++; return false; }, NoDiscard);
            t.NoteChanged += raised.Add;
            t.BeginEdit();
            ((TextBox)t.Child).Text = "typed evidence";

            bool handled = t.HandleKey(Key.Enter);
            return (handled, t.IsEditing, t.SaveFailed, t.Note, t.Child is TextBox);
        });

        Assert.True(info.handled);
        Assert.True(info.IsEditing);       // still open: the text is not lost, the user sees it is unsaved
        Assert.True(info.SaveFailed);
        Assert.Equal("old", info.Note);    // the title still shows what is actually stored
        Assert.True(info.Item5);
        Assert.Empty(raised);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Enter_after_a_failed_save_tries_again_and_closes_on_success()
    {
        bool diskOk = false;
        var saved = new List<string>();
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("", s => { saved.Add(s); return diskOk; }, NoDiscard);
            t.BeginEdit();
            ((TextBox)t.Child).Text = "typed evidence";
            t.HandleKey(Key.Enter);          // fails
            diskOk = true;
            t.HandleKey(Key.Enter);          // retry
            return (t.IsEditing, t.SaveFailed, t.Note);
        });

        Assert.False(info.IsEditing);
        Assert.False(info.SaveFailed);
        Assert.Equal("typed evidence", info.Note);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public void Escape_after_a_failed_save_takes_the_typed_text_back_without_writing_anything()
    {
        var saved = new List<string>();
        int discarded = 0;
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            // "": the row shows an empty note although the store may hold a real one (the file was locked at load)
            var t = new RowNoteTitle("", s => { saved.Add(s); return false; }, () => discarded++);
            t.BeginEdit();
            ((TextBox)t.Child).Text = "abandoned";
            t.HandleKey(Key.Enter);          // fails; the store holds "abandoned" in memory
            t.HandleKey(Key.Escape);         // cancel: the store drops that edit - it must not write the displayed ""
            return (t.IsEditing, t.Note);
        });

        Assert.False(info.IsEditing);
        Assert.Equal("", info.Note);
        Assert.Equal(new[] { "abandoned" }, saved);   // the failed attempt only; Esc saved nothing
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void Escape_without_a_failed_save_does_not_even_ask_the_store()
    {
        int discarded = 0;
        WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("keep", Store, () => discarded++);
            t.BeginEdit();
            ((TextBox)t.Child).Text = "typed but abandoned";
            t.HandleKey(Key.Escape);
            return 0;
        });
        Assert.Equal(0, discarded);
    }

    [Fact]
    public void A_discard_that_throws_still_closes_the_editor()
    {
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("keep", _ => false, () => throw new InvalidOperationException("boom"));
            t.BeginEdit();
            ((TextBox)t.Child).Text = "x";
            t.HandleKey(Key.Enter);
            t.HandleKey(Key.Escape);
            return t.IsEditing;
        });
        Assert.False(info);
    }

    [Fact]
    public void Leaving_the_editor_raises_EditEnded_once_whatever_way_it_ends()
    {
        var ended = new List<string>();
        WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("a", Store, NoDiscard);
            t.EditEnded += () => ended.Add("ended:" + t.IsEditing);   // already closed when it is raised

            t.BeginEdit();
            t.HandleKey(Key.Enter);          // saved
            t.BeginEdit();
            t.HandleKey(Key.Escape);         // cancelled
            t.BeginEdit();
            t.Commit();                      // focus lost
            t.Commit();                      // not editing any more: nothing
            return 0;
        });
        Assert.Equal(new[] { "ended:False", "ended:False", "ended:False" }, ended);
    }

    [Fact]
    public void A_failed_save_does_not_end_the_edit()
    {
        int ended = 0;
        WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("a", _ => false, NoDiscard);
            t.EditEnded += () => ended++;
            t.BeginEdit();
            ((TextBox)t.Child).Text = "b";
            t.HandleKey(Key.Enter);
            return 0;
        });
        Assert.Equal(0, ended);
    }

    // ---- focus moves to the box's own Cut/Copy/Paste menu: the edit goes on ----

    [Fact]
    public void The_boxs_own_context_menu_taking_the_focus_does_not_end_the_edit()
    {
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var box = new TextBox();
            var menu = new ContextMenu { PlacementTarget = box };
            var item = new MenuItem { Header = "Einfügen" };
            menu.Items.Add(item);
            return (RowNoteTitle.KeepsEditingWhenFocusMovesTo(menu, box),
                    RowNoteTitle.KeepsEditingWhenFocusMovesTo(item, box));   // arrow keys move the focus to an item
        });
        Assert.True(info.Item1);
        Assert.True(info.Item2);
    }

    [Fact]
    public void Any_other_focus_target_ends_the_edit_and_so_does_no_target_at_all()
    {
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var box = new TextBox();
            var otherBox = new TextBox();
            var foreignMenu = new ContextMenu { PlacementTarget = otherBox };   // e.g. the row menu of another row
            var foreignItem = new MenuItem();
            foreignMenu.Items.Add(foreignItem);
            var menuWithoutTarget = new ContextMenu();
            return (RowNoteTitle.KeepsEditingWhenFocusMovesTo(otherBox, box),
                    RowNoteTitle.KeepsEditingWhenFocusMovesTo(new Button(), box),
                    RowNoteTitle.KeepsEditingWhenFocusMovesTo(foreignMenu, box),
                    RowNoteTitle.KeepsEditingWhenFocusMovesTo(foreignItem, box),
                    RowNoteTitle.KeepsEditingWhenFocusMovesTo(menuWithoutTarget, box),
                    // null = nothing has the focus, which is also what happens when the row is removed by a refresh
                    // while the box has it: that must still save what was typed.
                    RowNoteTitle.KeepsEditingWhenFocusMovesTo(null, box));
        });
        Assert.False(info.Item1);
        Assert.False(info.Item2);
        Assert.False(info.Item3);
        Assert.False(info.Item4);
        Assert.False(info.Item5);
        Assert.False(info.Item6);
    }

    [Fact]
    public void Opening_the_menu_and_choosing_paste_keeps_the_box_so_the_pasted_text_is_in_it()
    {
        // the focus handler itself: LostKeyboardFocus towards the box's menu must not commit
        var saved = new List<string>();
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("", s => { saved.Add(s); return true; }, NoDiscard);
            t.BeginEdit();
            var box = (TextBox)t.Child;
            box.Text = "Cheater: ";
            var menu = new ContextMenu { PlacementTarget = box };

            t.OnBoxLostFocus(menu);                 // the menu opens and takes the keyboard focus
            bool stillEditing = t.IsEditing;
            box.Text = "Cheater: 76561198000000000"; // Paste ran on the still-open box
            t.OnBoxLostFocus(new Button());         // later: a click somewhere else
            return (stillEditing, t.IsEditing, t.Note);
        });

        Assert.True(info.stillEditing);
        Assert.False(info.Item2);
        Assert.Equal("Cheater: 76561198000000000", info.Note);
        Assert.Equal(new[] { "Cheater: 76561198000000000" }, saved);   // one save, with the pasted ID
    }

    [Fact]
    public void Other_keys_are_not_swallowed()
    {
        var handled = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var t = new RowNoteTitle("x", Store, NoDiscard);
            t.BeginEdit();
            return t.HandleKey(Key.A);
        });
        Assert.False(handled);
    }
}

/// <summary>The theme's new context-menu look must load and open.</summary>
[Collection("WpfApplication")]
public sealed class ThemeContextMenuTests
{
    [Fact]
    public void ContextMenu_and_MenuItem_pick_up_the_theme_styles_and_open()
    {
        var info = WpfTestSupport.OnSta(() =>
        {
            WpfTestSupport.EnsureTheme();
            var host = new Window
            {
                Left = -20000, Top = -20000, Width = 200, Height = 100,
                ShowActivated = false, ShowInTaskbar = false,
                Content = new Button { Content = "target" }
            };
            try
            {
                host.Show();
                var menu = new ContextMenu();
                var item = new MenuItem { Header = "Edit note" };
                menu.Items.Add(item);
                menu.PlacementTarget = (Button)host.Content;
                menu.IsOpen = true;

                menu.ApplyTemplate();
                item.ApplyTemplate();
                var isOpen = menu.IsOpen;
                menu.IsOpen = false;
                return (isOpen, menu.OverridesDefaultStyle, item.Template is not null, item.OverridesDefaultStyle);
            }
            finally { host.Close(); }
        });

        Assert.True(info.isOpen);
        Assert.True(info.Item2);   // the theme style (not the system look) is applied
        Assert.True(info.Item3);
        Assert.True(info.Item4);
    }
}
