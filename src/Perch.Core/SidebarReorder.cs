using System;
using System.Collections.Generic;

namespace Perch;

/// The splice behind sidebar drag-reorder, pulled out of MainWindow so it can be
/// tested without a store or a WebView. Order is purely array position (projects
/// and tabs have no order field), so a reorder IS a remove-and-reinsert.
internal static class SidebarReorder
{
    /// Move the item identified by <paramref name="moved"/> to sit immediately
    /// before (<paramref name="after"/> = false) or after (true)
    /// <paramref name="target"/> within <paramref name="items"/>. Returns false —
    /// leaving the list untouched — when moved == target, or either id is absent
    /// (a stale drag whose row vanished). Works on any IList, so it drives both
    /// the projects List and the sessions ObservableCollection.
    public static bool Move<T>(IList<T> items, Func<T, Guid> id, Guid moved, Guid target, bool after)
    {
        if (moved == target) return false;
        int from = IndexOf(items, id, moved);
        if (from < 0 || IndexOf(items, id, target) < 0) return false;

        var item = items[from];
        items.RemoveAt(from);
        int ti = IndexOf(items, id, target);   // recompute — removal may have shifted it
        items.Insert(after ? ti + 1 : ti, item);
        return true;
    }

    private static int IndexOf<T>(IList<T> items, Func<T, Guid> id, Guid g)
    {
        for (int i = 0; i < items.Count; i++)
            if (id(items[i]) == g) return i;
        return -1;
    }
}
