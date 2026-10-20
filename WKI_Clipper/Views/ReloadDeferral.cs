namespace WKI_Clipper.Views;

/// <summary>
/// When a reload that a background job asked for may run. A finished Discord compress or clip cut reloads the
/// gallery, and a reload rebuilds every row - also the one whose note is being typed, whose box would vanish
/// mid-sentence. While a note is edited such a reload waits; it runs once the edit has ended. One waiting reload
/// covers any number of requests. (A reload the user asks for - refresh, filter - always runs at once.)
/// </summary>
internal sealed class ReloadDeferral
{
    private bool _pending;

    /// <summary>A background event wants a reload. True = run it now; false = it waits for the end of the edit.</summary>
    public bool Request(bool editing)
    {
        if (!editing) return true;
        _pending = true;
        return false;
    }

    /// <summary>The edit may have ended: true once when a reload is waiting and nothing is being edited any more.</summary>
    public bool TakeIfDue(bool editing)
    {
        if (!_pending || editing) return false;
        _pending = false;
        return true;
    }

    /// <summary>A reload ran for another reason: whatever waited has been satisfied by it.</summary>
    public void Done() => _pending = false;
}
