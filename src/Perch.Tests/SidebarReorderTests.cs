using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Perch;
using Xunit;

namespace Perch.Tests;

// The splice behind sidebar drag-reorder. Order is purely array position, so the
// whole feature rests on this being right: before/after edges, moving to the
// ends, and refusing a no-op or a stale drag whose row vanished.
public class SidebarReorderTests
{
    private sealed record Item(Guid Id);

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static List<Item> Make(params Guid[] ids) => ids.Select(i => new Item(i)).ToList();
    private static Guid[] Ids(IEnumerable<Item> l) => l.Select(x => x.Id).ToArray();

    [Fact]
    public void MoveBefore_PlacesItemAheadOfTarget()
    {
        var l = Make(A, B, C);
        Assert.True(SidebarReorder.Move(l, x => x.Id, C, A, after: false));
        Assert.Equal(new[] { C, A, B }, Ids(l));
    }

    [Fact]
    public void MoveAfter_PlacesItemBehindTarget()
    {
        var l = Make(A, B, C);
        Assert.True(SidebarReorder.Move(l, x => x.Id, A, B, after: true));
        Assert.Equal(new[] { B, A, C }, Ids(l));
    }

    [Fact]
    public void MoveAfterLast_LandsAtTheEnd()
    {
        var l = Make(A, B, C);
        Assert.True(SidebarReorder.Move(l, x => x.Id, A, C, after: true));
        Assert.Equal(new[] { B, C, A }, Ids(l));
    }

    [Fact]
    public void MoveBeforeFirst_LandsAtTheStart()
    {
        var l = Make(A, B, C);
        Assert.True(SidebarReorder.Move(l, x => x.Id, C, A, after: false));
        Assert.Equal(C, Ids(l)[0]);
    }

    [Fact]
    public void SelfMove_IsANoOp()
    {
        var l = Make(A, B, C);
        Assert.False(SidebarReorder.Move(l, x => x.Id, A, A, after: true));
        Assert.Equal(new[] { A, B, C }, Ids(l));
    }

    [Fact]
    public void UnknownTarget_IsANoOp()
    {
        var l = Make(A, B);
        Assert.False(SidebarReorder.Move(l, x => x.Id, A, Guid.NewGuid(), after: true));
        Assert.Equal(new[] { A, B }, Ids(l));
    }

    [Fact]
    public void WorksOnAnObservableCollection_TheSessionsStoreType()
    {
        var c = new ObservableCollection<Item>(Make(A, B, C));
        Assert.True(SidebarReorder.Move(c, x => x.Id, B, A, after: false));
        Assert.Equal(new[] { B, A, C }, Ids(c));
    }
}
