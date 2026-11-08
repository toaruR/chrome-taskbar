using System.Windows;
using ChromeTaskbar.App;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class BarLayoutTests
{
    [Fact]
    public void FitListSize_Landscape_KeepsRowsAndWidens()
    {
        var list = new Size(BarLayout.CellWidth * 2, BarLayout.RowHeight);
        var fit = BarLayout.FitListSize(list, 5);
        Assert.Equal(BarLayout.CellWidth * 5, fit.Width);
        Assert.Equal(list.Height, fit.Height);
    }

    [Fact]
    public void FitListSize_Portrait_KeepsColumnsAndHeightens()
    {
        var list = new Size(BarLayout.CellWidth, BarLayout.RowHeight * 6); // taller than wide
        var fit = BarLayout.FitListSize(list, 8);
        Assert.Equal(list.Width, fit.Width);
        Assert.Equal(BarLayout.RowHeight * 8, fit.Height);
    }

    [Fact]
    public void FitListSize_AlreadyFits_ReturnsSameSize()
    {
        var list = new Size(BarLayout.CellWidth * 4, BarLayout.RowHeight);
        Assert.Equal(list, BarLayout.FitListSize(list, 3));
        Assert.Equal(list, BarLayout.FitListSize(list, 0));
    }

    [Fact]
    public void FillCellWidth_SpreadsLeftoverAcrossColumns()
    {
        Assert.Equal(Math.Floor((BarLayout.CellWidth * 2 + 50) / 2), BarLayout.FillCellWidth(BarLayout.CellWidth * 2 + 50));
        Assert.Equal(BarLayout.CellWidth, BarLayout.FillCellWidth(10));
    }

    [Fact]
    public void MinSize_HoldsExactlyOneButton()
    {
        Assert.Equal(BarLayout.CellWidth + 2 * BarLayout.WindowBorder, BarLayout.MinWidth);
        Assert.Equal(BarLayout.MinWidth + BarLayout.GripSize, BarLayout.MinWidthWithSideBar);
        Assert.Equal(BarLayout.RowHeight + 2 * BarLayout.WindowBorder, BarLayout.MinHeight);
    }

    [Fact]
    public void SnapListSize_Landscape_KeepsRowsAndShrinksToWholeCells()
    {
        // 2 rows fit the height; 5 buttons -> 3 columns x 2 rows, extra width trimmed.
        var list = new Size(BarLayout.CellWidth * 6 + 40, BarLayout.RowHeight * 2 + 10);
        Assert.Equal(new Size(BarLayout.CellWidth * 3, BarLayout.RowHeight * 2), BarLayout.SnapListSize(list, 5));
    }

    [Fact]
    public void SnapListSize_RowsCappedAtButtonCount()
    {
        var list = new Size(BarLayout.CellWidth * 6, BarLayout.RowHeight * 5);
        Assert.Equal(new Size(BarLayout.CellWidth, BarLayout.RowHeight * 2), BarLayout.SnapListSize(list, 2)); // rows capped at count
    }

    [Fact]
    public void SnapListSize_Portrait_KeepsColumns()
    {
        var list = new Size(BarLayout.CellWidth * 2 + 20, BarLayout.RowHeight * 20);
        Assert.Equal(new Size(BarLayout.CellWidth * 2, BarLayout.RowHeight * 3), BarLayout.SnapListSize(list, 5));
    }

    [Fact]
    public void WindowSizeFor_PlacesGripOnShortSide()
    {
        var wide = BarLayout.WindowSizeFor(new Size(BarLayout.CellWidth * 3, BarLayout.RowHeight));
        Assert.Equal(BarLayout.CellWidth * 3 + 2 + BarLayout.GripSize, wide.Width);
        var tall = BarLayout.WindowSizeFor(new Size(BarLayout.CellWidth, BarLayout.RowHeight * 10));
        Assert.Equal(BarLayout.RowHeight * 10 + 2 + BarLayout.GripSize, tall.Height);
    }

    [Fact]
    public void RoundingLoss_StillCountsAsFittingCells()
    {
        // Window sized for 11 cells, list rounded a fraction of a DIP short (150% DPI).
        double list = 11 * BarLayout.CellWidth - 0.67;
        double cell = BarLayout.FillCellWidth(list);
        Assert.True(11 * cell <= list); // 11 columns, none wrapped into a hidden row
        Assert.Equal(new Size(list, BarLayout.RowHeight - 0.67), BarLayout.FitListSize(new Size(list, BarLayout.RowHeight - 0.67), 11));
        // One more button still grows the bar.
        Assert.Equal(12 * BarLayout.CellWidth, BarLayout.FitListSize(new Size(list, BarLayout.RowHeight - 0.67), 12).Width);
    }
}
