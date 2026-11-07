using System.Windows;

namespace ChromeTaskbar.App;

/// <summary>Fixed-cell button layout and grow-to-fit sizing (mirrors vscode-selector LayoutMath).</summary>
public static class BarLayout
{
    public const double ButtonWidth = 180;
    public const double ButtonHeight = 28;
    public const double ButtonMargin = 2;
    public const double IconSize = 20;
    public const double CellWidth = ButtonWidth + 2 * ButtonMargin;
    public const double RowHeight = ButtonHeight + 2 * ButtonMargin;
    public const double GripSize = 24;
    public const double WindowBorder = 1;
    public const double DefaultWidth = 800;
    public static readonly Thickness ButtonMarginThickness = new(ButtonMargin);
    // One button fits exactly; a landscape window (left grip) adds the grip width.
    public const double MinWidth = CellWidth + 2 * WindowBorder;
    public const double MinWidthWithSideBar = MinWidth + GripSize;
    public const double MinHeight = RowHeight + 2 * WindowBorder;

    /// <summary>Grip goes on the short side: landscape -> left (vertical bar), portrait -> top.</summary>
    public static bool IsLandscape(Size window) => window.Width >= window.Height;

    /// <summary>Keeps the column count and spreads the leftover width so no gap remains on the right.</summary>
    public static double FillCellWidth(double listWidth)
    {
        int cols = Math.Max(1, (int)Math.Floor(listWidth / CellWidth));
        return Math.Max(CellWidth, Math.Floor(listWidth / cols));
    }

    /// <summary>
    /// List size that holds all <paramref name="count"/> buttons: keeps the rows (landscape) or columns (portrait)
    /// of the short side and grows only the long side. Returns <paramref name="list"/> when everything already fits.
    /// </summary>
    public static Size FitListSize(Size list, int count)
    {
        if (count <= 0) return list;
        if (list.Width >= list.Height)
        {
            int rows = Math.Max(1, (int)Math.Floor(list.Height / RowHeight));
            int cols = (count + rows - 1) / rows;
            return new Size(Math.Max(list.Width, cols * CellWidth), list.Height);
        }
        else
        {
            int cols = Math.Max(1, (int)Math.Floor(list.Width / CellWidth));
            int rows = (count + cols - 1) / cols;
            return new Size(list.Width, Math.Max(list.Height, rows * RowHeight));
        }
    }

    /// <summary>
    /// Exact list size for <paramref name="count"/> buttons after a manual resize: keeps the user's rows (landscape)
    /// or columns (portrait), then trims both sides to whole cells so no empty space remains.
    /// </summary>
    public static Size SnapListSize(Size list, int count)
    {
        int n = Math.Max(1, count);
        int rows, cols;
        if (list.Width >= list.Height)
        {
            rows = Math.Clamp((int)Math.Floor(list.Height / RowHeight), 1, n);
            cols = (n + rows - 1) / rows;
            rows = (n + cols - 1) / cols;
        }
        else
        {
            cols = Math.Clamp((int)Math.Floor(list.Width / CellWidth), 1, n);
            rows = (n + cols - 1) / cols;
            cols = (n + rows - 1) / rows;
        }
        return new Size(cols * CellWidth, rows * RowHeight);
    }

    /// <summary>Window size whose list area is <paramref name="list"/>, with the grip on the resulting short side.</summary>
    public static Size WindowSizeFor(Size list)
    {
        double b = 2 * WindowBorder;
        var landscape = new Size(list.Width + b + GripSize, list.Height + b);
        return IsLandscape(landscape) ? landscape : new Size(list.Width + b, list.Height + b + GripSize);
    }
}
