using Xunit;

namespace Perch.Tests;

// The split-tree rules the whole pane UX hangs off: flat-split, close-collapse,
// drag-move insert, keyboard move, weight semantics, auto-naming. Every recent
// "pane layout looks wrong after X" regression traces to one of these ops, so
// each rule gets a named test. Trees are built with the same PaneNode API the
// app uses; no window involved.
public class PaneTreeTests
{
    private static PaneNode Leaf(string? name = null, double weight = 1.0) =>
        new() { Name = name, Weight = weight };

    private static PaneNode Split(SplitOrientation dir, params PaneNode[] children) =>
        new() { Split = dir, Children = children.ToList() };

    // ---- SplitImpl ---------------------------------------------------------

    [Fact]
    public void Split_WrapsLoneLeafInNewSplit()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var root = PaneTree.SplitImpl(a, a.Id, SplitOrientation.Vertical, b);

        Assert.NotNull(root);
        Assert.False(root!.IsLeaf);
        Assert.Equal(SplitOrientation.Vertical, root.Split);
        Assert.Equal(new[] { a, b }, root.Children);
        Assert.Equal(1.0, a.Weight);
        Assert.Equal(1.0, b.Weight);
    }

    [Fact]
    public void Split_SameDirectionFlattensIntoParent()
    {
        // (a | b) then split b right → (a | b | c), NOT (a | (b | c)).
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, b);

        var result = PaneTree.SplitImpl(root, b.Id, SplitOrientation.Vertical, c);

        Assert.Same(root, result);
        Assert.Equal(new[] { a, b, c }, root.Children);
    }

    [Fact]
    public void Split_PerpendicularDirectionNests()
    {
        // (a | b) then split b down → (a | (b / c)).
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, b);

        var result = PaneTree.SplitImpl(root, b.Id, SplitOrientation.Horizontal, c);

        Assert.Same(root, result);
        Assert.Equal(2, root.Children.Count);
        var nested = root.Children[1];
        Assert.Equal(SplitOrientation.Horizontal, nested.Split);
        Assert.Equal(new[] { b, c }, nested.Children);
    }

    [Fact]
    public void Split_WrapperInheritsResizedWeight()
    {
        // A leaf dragged to 2x width keeps its share when split: the wrapper
        // takes weight 2, and the two panes inside share it evenly.
        var a = Leaf("a", weight: 2.0);
        var b = Leaf("b");
        var other = Leaf("other");
        var root = Split(SplitOrientation.Horizontal, other, a);

        PaneTree.SplitImpl(root, a.Id, SplitOrientation.Vertical, b);

        var wrapper = root.Children[1];
        Assert.Equal(2.0, wrapper.Weight);
        Assert.Equal(1.0, a.Weight);
        Assert.Equal(1.0, b.Weight);
    }

    [Fact]
    public void Split_UnknownPaneReturnsNull()
    {
        var root = Split(SplitOrientation.Vertical, Leaf("a"), Leaf("b"));
        Assert.Null(PaneTree.SplitImpl(root, Guid.NewGuid(), SplitOrientation.Vertical, Leaf("c")));
    }

    // ---- CloseAndCollapse --------------------------------------------------

    [Fact]
    public void Close_CollapsesTwoPaneSplitToSurvivor()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var root = Split(SplitOrientation.Vertical, a, b);

        var result = PaneTree.CloseAndCollapse(root, b.Id);

        Assert.Same(a, result);
    }

    [Fact]
    public void Close_LoneLeafReturnsNull()
    {
        var a = Leaf("a");
        Assert.Null(PaneTree.CloseAndCollapse(a, a.Id));
    }

    [Fact]
    public void Close_CollapsesNestedSplitIntoParentSlot()
    {
        // (a | (b / c)), close c → (a | b).
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, Split(SplitOrientation.Horizontal, b, c));

        var result = PaneTree.CloseAndCollapse(root, c.Id);

        Assert.Same(root, result);
        Assert.Equal(new[] { a, b }, root.Children);
    }

    [Fact]
    public void Close_ThreeSiblingsKeepsSplit()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, b, c);

        var result = PaneTree.CloseAndCollapse(root, b.Id);

        Assert.Same(root, result);
        Assert.Equal(new[] { a, c }, root.Children);
    }

    // ---- ResetWeights (Ctrl+Shift+E / close-redistribute) ------------------

    [Fact]
    public void ResetWeights_EvensOutEveryLevel()
    {
        var a = Leaf("a", 3.0);
        var b = Leaf("b", 0.25);
        var inner = Split(SplitOrientation.Horizontal, a, b);
        inner.Weight = 7.0;
        var root = Split(SplitOrientation.Vertical, inner, Leaf("c", 0.5));

        PaneTree.ResetWeights(root);

        Assert.All(PaneTree.AllLeaves(root), l => Assert.Equal(1.0, l.Weight));
        Assert.Equal(1.0, inner.Weight);
    }

    // ---- SwapNodes (drag-to-center) ----------------------------------------

    [Fact]
    public void Swap_SharedParent()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var root = Split(SplitOrientation.Vertical, a, b);

        Assert.True(PaneTree.SwapNodes(root, a.Id, b.Id));
        Assert.Equal(new[] { b, a }, root.Children);
    }

    [Fact]
    public void Swap_AcrossDifferentParents_WeightsTravelWithPanes()
    {
        // (a | (b / c)): swap a and c. Each keeps its own Weight.
        var a = Leaf("a", 2.0);
        var b = Leaf("b");
        var c = Leaf("c", 0.5);
        var inner = Split(SplitOrientation.Horizontal, b, c);
        var root = Split(SplitOrientation.Vertical, a, inner);

        Assert.True(PaneTree.SwapNodes(root, a.Id, c.Id));

        Assert.Same(c, root.Children[0]);
        Assert.Same(a, inner.Children[1]);
        Assert.Equal(2.0, a.Weight);
        Assert.Equal(0.5, c.Weight);
    }

    [Fact]
    public void Swap_UnknownIdReturnsFalseAndLeavesTreeIntact()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var root = Split(SplitOrientation.Vertical, a, b);

        Assert.False(PaneTree.SwapNodes(root, a.Id, Guid.NewGuid()));
        Assert.Equal(new[] { a, b }, root.Children);
    }

    // ---- InsertBesideImpl (drag-to-edge) ------------------------------------

    [Fact]
    public void InsertBeside_BeforeAndAfter()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var root = PaneTree.InsertBesideImpl(a, a.Id, b, SplitOrientation.Vertical, before: true);
        Assert.Equal(new[] { b, a }, root!.Children);

        var c = Leaf("c");
        var d = Leaf("d");
        var root2 = PaneTree.InsertBesideImpl(c, c.Id, d, SplitOrientation.Vertical, before: false);
        Assert.Equal(new[] { c, d }, root2!.Children);
    }

    [Fact]
    public void InsertBeside_SameDirectionFlattens()
    {
        // (a | b), drop c on b's left edge → (a | c | b) flat.
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, b);

        var result = PaneTree.InsertBesideImpl(root, b.Id, c, SplitOrientation.Vertical, before: true);

        Assert.Same(root, result);
        Assert.Equal(new[] { a, c, b }, root.Children);
    }

    [Fact]
    public void InsertBeside_TargetKeepsItsShareViaWrapper()
    {
        var a = Leaf("a", 3.0);
        var b = Leaf("b");
        var other = Leaf("other");
        var root = Split(SplitOrientation.Vertical, other, a);

        PaneTree.InsertBesideImpl(root, a.Id, b, SplitOrientation.Horizontal, before: false);

        var wrapper = root.Children[1];
        Assert.Equal(3.0, wrapper.Weight);
        Assert.Equal(SplitOrientation.Horizontal, wrapper.Split);
        Assert.Equal(new[] { a, b }, wrapper.Children);
        Assert.Equal(1.0, a.Weight);
    }

    // ---- MoveWithinParent (Ctrl+Shift+arrows) --------------------------------

    [Fact]
    public void MoveDir_SwapsWithAdjacentSibling()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, b, c);

        Assert.True(PaneTree.MoveWithinParent(root, b.Id, "right"));
        Assert.Equal(new[] { a, c, b }, root.Children);

        Assert.True(PaneTree.MoveWithinParent(root, b.Id, "left"));
        Assert.Equal(new[] { a, b, c }, root.Children);
    }

    [Fact]
    public void MoveDir_AtEdgeIsNoOp()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var root = Split(SplitOrientation.Vertical, a, b);

        Assert.False(PaneTree.MoveWithinParent(root, a.Id, "left"));
        Assert.Equal(new[] { a, b }, root.Children);
    }

    [Fact]
    public void MoveDir_PerpendicularToSplitAxisIsNoOp()
    {
        // Side-by-side split: up/down don't apply.
        var a = Leaf("a");
        var b = Leaf("b");
        var root = Split(SplitOrientation.Vertical, a, b);

        Assert.False(PaneTree.MoveWithinParent(root, a.Id, "down"));
        Assert.Equal(new[] { a, b }, root.Children);
    }

    [Fact]
    public void MoveDir_RootLeafIsNoOp()
    {
        var a = Leaf("a");
        Assert.False(PaneTree.MoveWithinParent(a, a.Id, "right"));
    }

    [Fact]
    public void MoveDir_SwapsWholeSubtreeSibling()
    {
        // (a | (b / c)): moving a right swaps it past the whole nested split.
        var a = Leaf("a");
        var inner = Split(SplitOrientation.Horizontal, Leaf("b"), Leaf("c"));
        var root = Split(SplitOrientation.Vertical, a, inner);

        Assert.True(PaneTree.MoveWithinParent(root, a.Id, "right"));
        Assert.Same(inner, root.Children[0]);
        Assert.Same(a, root.Children[1]);
    }

    // ---- AutoName ------------------------------------------------------------

    [Fact]
    public void AutoName_FillsBlanksFromMaxPlusOne()
    {
        // Regression: close pane-1, survivor keeps "pane-2"; the next split's
        // fresh leaf must become pane-3, not a second pane-2.
        var survivor = Leaf("pane-2");
        var fresh = Leaf();
        var root = Split(SplitOrientation.Vertical, survivor, fresh);

        PaneTree.AutoName(root);

        Assert.Equal("pane-2", survivor.Name);
        Assert.Equal("pane-3", fresh.Name);
    }

    [Fact]
    public void AutoName_NeverTouchesUserNames()
    {
        var named = Leaf("simulator-fix");
        var fresh = Leaf();
        var root = Split(SplitOrientation.Vertical, named, fresh);

        PaneTree.AutoName(root);

        Assert.Equal("simulator-fix", named.Name);
        Assert.Equal("pane-1", fresh.Name);
    }

    [Fact]
    public void AutoName_NumbersMultipleBlanksSequentially()
    {
        var l1 = Leaf();
        var l2 = Leaf();
        var l3 = Leaf("pane-5");
        var root = Split(SplitOrientation.Vertical, l1, Split(SplitOrientation.Horizontal, l2, l3));

        PaneTree.AutoName(root);

        Assert.Equal("pane-6", l1.Name);
        Assert.Equal("pane-7", l2.Name);
    }

    // ---- Traversal helpers -----------------------------------------------------

    [Fact]
    public void AllLeaves_ReturnsDocumentOrder()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var root = Split(SplitOrientation.Vertical, a, Split(SplitOrientation.Horizontal, b, c));

        Assert.Equal(new[] { a, b, c }, PaneTree.AllLeaves(root).ToArray());
        Assert.Same(a, PaneTree.FirstLeaf(root));
    }

    [Fact]
    public void FindParent_LocatesDirectParentAndIndex()
    {
        var a = Leaf("a");
        var b = Leaf("b");
        var c = Leaf("c");
        var inner = Split(SplitOrientation.Horizontal, b, c);
        var root = Split(SplitOrientation.Vertical, a, inner);

        Assert.True(PaneTree.FindParent(root, c.Id, out var parent, out var idx));
        Assert.Same(inner, parent);
        Assert.Equal(1, idx);
    }

    // ---- Claude transcript dir key (resume pre-flight) -----------------------

    [Fact]
    public void SanitizeCwd_MatchesClaudesProjectDirKey()
    {
        // Must track Claude Code's own sanitization: separators AND the drive
        // colon become '-'. A drift here makes the resume pre-flight look in
        // the wrong folder and fall back to the slow whole-tree scan.
        Assert.Equal(
            "C--Users-josep-dev-projects-cmux-win",
            ClaudeTranscripts.SanitizeCwd(@"C:\Users\josep\dev-projects\cmux-win"));
        Assert.Equal(
            "-home-user-proj",
            ClaudeTranscripts.SanitizeCwd("/home/user/proj"));
    }
}
