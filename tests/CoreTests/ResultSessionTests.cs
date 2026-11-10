using System.Collections.Generic;
using System.Linq;
using deepduplicates;
using Xunit;

namespace CoreTests;

public class ResultSessionTests
{
    private static ResultSession Session(params int[][] groups)
    {
        var set = new ResultSet();
        int next = 1;
        foreach (var members in groups)
        {
            var g = new DuplicateGroup { Id = members[0], SuggestedKeepId = members[0] };
            foreach (var id in members) g.Members.Add(new VideoEntry { Id = id, Path = $"c:/v/{id}.mp4", SizeBytes = 100 * id, DurationSeconds = 60 });
            set.Groups.Add(g);
            next++;
        }
        return new ResultSession(set);
    }

    private static VideoEntry Entry(ResultSession s, int id) => s.Results.AllEntries().Single(e => e.Id == id);

    [Fact]
    public void BothCopiesCanBeSelected_WithAWarning_AndTheKeepMarkGoes()
    {
        var s = Session(new[] { 1, 2 });                       // 1 is the original (kept), 2 the duplicate
        Assert.Null(s.SetSelected(2, true));                    // the duplicate: no warning
        Assert.True(s.IsKept(Entry(s, 1)));
        Assert.False(s.IsDoomed(1));

        var warning = s.SetSelected(1, true);                   // and the original too: allowed, but it says so
        Assert.NotNull(warning);
        Assert.Contains("Both copies", warning);
        Assert.Contains("both files", warning);

        Assert.True(s.IsSelected(1) && s.IsSelected(2));
        Assert.True(s.IsDoomed(1) && s.IsDoomed(2));            // both rows turn red
        Assert.False(s.IsKept(Entry(s, 1)) || s.IsKept(Entry(s, 2)));   // nothing is kept
        Assert.Equal(1, s.GroupsFullySelected());
        Assert.Equal(2, s.SelectedCount);
    }

    [Fact]
    public void UnselectingOne_KeepsThatOne_AndTheGroupIsSafeAgain()
    {
        var s = Session(new[] { 1, 2 });
        s.SetSelected(2, true); s.SetSelected(1, true);

        Assert.Null(s.SetSelected(2, false));                   // the duplicate is unselected: it is the copy that stays now
        Assert.False(s.IsDoomed(1)); Assert.False(s.IsDoomed(2));
        Assert.Equal(0, s.GroupsFullySelected());
        Assert.True(s.IsKept(Entry(s, 2)));                     // keep moved to the one that is not selected
        Assert.False(s.IsKept(Entry(s, 1)));
        Assert.True(s.IsSelected(1));                            // the original is still selected for removal
        Assert.Equal(2, s.KeepId(s.Results.Groups[0]));
    }

    [Fact]
    public void AGroupOfThree_IsDoomedOnlyWhenAllThreeAreSelected()
    {
        var s = Session(new[] { 1, 2, 3 });
        Assert.Null(s.SetSelected(2, true));
        Assert.Null(s.SetSelected(3, true));
        Assert.False(s.IsDoomed(1));
        var warning = s.SetSelected(1, true);
        Assert.Contains("All 3 copies", warning);
        Assert.True(s.IsDoomed(2));

        s.SetSelected(3, false);
        Assert.False(s.IsDoomed(1));
        Assert.True(s.IsKept(Entry(s, 3)));
        Assert.Equal(2, s.SelectedCount);
    }

    [Fact]
    public void TheButtonsThatSelectManyFilesStillKeepOneCopy()
    {
        var s = Session(new[] { 1, 2, 3 }, new[] { 10, 11 });
        s.SelectAllDuplicates();
        Assert.Equal(0, s.GroupsFullySelected());
        Assert.True(s.IsKept(Entry(s, 1)) && s.IsKept(Entry(s, 10)));
        Assert.Equal(3, s.SelectedCount);

        s.SelectNone();
        s.SelectFolder(Entry(s, 1).Folder);                      // every copy is in this folder: one is kept
        Assert.Equal(0, s.GroupsFullySelected());
    }

    [Fact]
    public void SelectingTheKeptCopy_WhileAnotherIsFree_StillMovesTheKeepMark()
    {
        var s = Session(new[] { 1, 2, 3 });
        s.SetSelected(2, true);
        Assert.Null(s.SetSelected(1, true));                     // 3 is still free: no warning, 3 is kept now
        Assert.True(s.IsKept(Entry(s, 3)));
        Assert.False(s.IsDoomed(1));
    }
}
