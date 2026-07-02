using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Perch;

/// Pure operations on the PaneNode split tree. No UI, no IO, no side effects
/// beyond mutating the tree passed in — every rule about how panes split,
/// close, move and collapse lives here so it can be unit-tested without a
/// window. MainWindow (and the message router) call these and then handle
/// persistence + state pushes.
internal static class PaneTree
{
    public static IEnumerable<PaneNode> AllLeaves(PaneNode node)
    {
        if (node.IsLeaf) { yield return node; yield break; }
        foreach (var c in node.Children)
            foreach (var leaf in AllLeaves(c)) yield return leaf;
    }

    public static PaneNode? FirstLeaf(PaneNode node)
    {
        if (node.IsLeaf) return node;
        foreach (var c in node.Children)
            if (FirstLeaf(c) is PaneNode leaf) return leaf;
        return null;
    }

    // Find any node (leaf OR split) by id within a subtree.
    public static PaneNode? FindNode(PaneNode node, Guid id)
    {
        if (node.Id == id) return node;
        if (node.IsLeaf) return null;
        foreach (var c in node.Children)
        {
            var f = FindNode(c, id);
            if (f != null) return f;
        }
        return null;
    }

    // Find the split that directly contains `id` and the child's index.
    public static bool FindParent(PaneNode node, Guid id, out PaneNode parent, out int index)
    {
        if (!node.IsLeaf)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                if (node.Children[i].Id == id) { parent = node; index = i; return true; }
                if (FindParent(node.Children[i], id, out parent, out index)) return true;
            }
        }
        parent = null!;
        index = -1;
        return false;
    }

    // Tree mutations. SplitImpl wraps the matching leaf in a new split node
    // with the original leaf + a fresh sibling as its two children, returning
    // the (possibly new) root. Mutates `node` in place when descending into
    // splits so the caller doesn't have to rebuild upper nodes.
    //
    // FLAT-SPLIT behavior: when the new split is in the SAME direction as
    // the parent we descended through, we flatten — the parent absorbs the
    // newly-wrapped split's children directly. So splitting pane-2 to the
    // right inside an already-vertical split yields three flat siblings
    // (pane-1 | pane-2 | pane-3 each at 1fr) instead of nested
    // (pane-1 | (pane-2 | pane-3) with the new pane taking half of pane-2).
    // Same applies to horizontal splits ("down" inside an already-down
    // split). flex: 1 1 0 on .split children then gives even sizing for
    // any pane count.
    public static PaneNode? SplitImpl(PaneNode node, Guid paneId, SplitOrientation dir, PaneNode newSibling)
    {
        if (node.IsLeaf)
        {
            if (node.Id != paneId) return null;
            // The new wrapper takes the leaf's slot in the parent split, so it
            // inherits the leaf's Weight; inside, the leaf and its new sibling
            // split that slot evenly (both 1.0). When nothing's been resized
            // every Weight is 1.0, so this is identical to the old behavior.
            var wrapper = new PaneNode
            {
                Split = dir,
                Weight = node.Weight,
                Children = new List<PaneNode> { node, newSibling },
            };
            node.Weight = 1.0;
            return wrapper;
        }
        for (int i = 0; i < node.Children.Count; i++)
        {
            var rep = SplitImpl(node.Children[i], paneId, dir, newSibling);
            if (rep == null) continue;
            // Flatten: if the replacement is a split in the same direction
            // as us, splice its children into our children list at the
            // same index instead of nesting it.
            if (rep.Split == node.Split)
            {
                node.Children.RemoveAt(i);
                node.Children.InsertRange(i, rep.Children);
            }
            else
            {
                node.Children[i] = rep;
            }
            return node;
        }
        return null;
    }

    // CloseAndCollapse removes the leaf with paneId from the subtree rooted
    // at `node`. If a split is left with only one child, the split is
    // replaced by that child (the parent then unwraps recursively too).
    // Returns the replacement node, or null if the whole subtree disappeared.
    public static PaneNode? CloseAndCollapse(PaneNode node, Guid paneId)
    {
        if (node.IsLeaf) return node.Id == paneId ? null : node;
        var newChildren = new List<PaneNode>();
        foreach (var c in node.Children)
        {
            var rc = CloseAndCollapse(c, paneId);
            if (rc != null) newChildren.Add(rc);
        }
        if (newChildren.Count == 0) return null;
        if (newChildren.Count == 1) return newChildren[0];   // collapse
        node.Children = newChildren;
        return node;
    }

    // Reset every node's flex-grow Weight to 1.0 so each split divides its
    // space evenly at every level. Used on pane close (redistribute survivors);
    // the Ctrl+Shift+E command does the same thing web-side via resizeSplit.
    public static void ResetWeights(PaneNode node)
    {
        node.Weight = 1.0;
        foreach (var c in node.Children) ResetWeights(c);
    }

    // Swap two nodes' positions in the tree (each keeps its own Weight, so
    // sizes travel with the panes). Reads both slots before writing so a
    // shared-parent swap works too.
    public static bool SwapNodes(PaneNode root, Guid a, Guid b)
    {
        if (!FindParent(root, a, out var pa, out var ia)) return false;
        if (!FindParent(root, b, out var pb, out var ib)) return false;
        (pa.Children[ia], pb.Children[ib]) = (pb.Children[ib], pa.Children[ia]);
        return true;
    }

    // Insert `newNode` immediately before/after the target leaf, wrapping
    // them in a split of `orient`. Mirrors SplitImpl's flat-split behavior:
    // when the new split matches the parent's orientation, the parent absorbs
    // the children directly instead of nesting. Returns the (possibly new)
    // root, or null if target wasn't found.
    public static PaneNode? InsertBesideImpl(PaneNode node, Guid targetId, PaneNode newNode, SplitOrientation orient, bool before)
    {
        if (node.IsLeaf)
        {
            if (node.Id != targetId) return null;
            // The wrapper takes the target's slot; inside, target + newNode
            // share it evenly (both 1.0). Default (all-1.0) trees stay even.
            var wrapperWeight = node.Weight;
            node.Weight = 1.0;
            var children = before
                ? new List<PaneNode> { newNode, node }
                : new List<PaneNode> { node, newNode };
            return new PaneNode { Split = orient, Weight = wrapperWeight, Children = children };
        }
        for (int i = 0; i < node.Children.Count; i++)
        {
            var rep = InsertBesideImpl(node.Children[i], targetId, newNode, orient, before);
            if (rep == null) continue;
            if (rep.Split == node.Split)
            {
                node.Children.RemoveAt(i);
                node.Children.InsertRange(i, rep.Children);
            }
            else node.Children[i] = rep;
            return node;
        }
        return null;
    }

    // Keyboard move (Ctrl+Shift+arrows): shift the pane one slot within its
    // DIRECT parent split. left/right act on a Vertical (side-by-side) split,
    // up/down on a Horizontal (stacked) split; a perpendicular direction or an
    // edge position is a no-op (returns false — callers skip save/push). Swaps
    // the pane with its adjacent sibling (which may be a whole subtree), so
    // the pane keeps its identity + state and its Weight travels with it.
    public static bool MoveWithinParent(PaneNode root, Guid paneId, string dir)
    {
        if (!FindParent(root, paneId, out var parent, out var idx)) return false; // root leaf: nowhere to go
        var wantVertical = dir is "left" or "right";
        var parentIsVertical = parent.Split == SplitOrientation.Vertical;
        if (wantVertical != parentIsVertical) return false;   // direction is across this split's axis
        var target = dir is "left" or "up" ? idx - 1 : idx + 1;
        if (target < 0 || target >= parent.Children.Count) return false; // already at the edge
        (parent.Children[idx], parent.Children[target]) = (parent.Children[target], parent.Children[idx]);
        return true;
    }

    // Next "pane-N" = highest existing N + 1, NOT a positional index. Positional
    // numbering collided after a close+split: close pane-1, the split collapses
    // and the survivor keeps "pane-2", then the next split's walker counts the
    // survivor as position 1 and assigns the new pane "pane-2" too — two panes,
    // same name. Scanning for the max keeps every auto name unique.
    public static void AutoName(PaneNode root)
    {
        int max = 0;
        foreach (var leaf in AllLeaves(root))
        {
            var m = Regex.Match(leaf.Name ?? "", @"^pane-(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var k) && k > max) max = k;
        }
        foreach (var leaf in AllLeaves(root))
            if (string.IsNullOrEmpty(leaf.Name)) leaf.Name = $"pane-{++max}";
    }
}
