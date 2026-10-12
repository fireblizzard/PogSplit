using System;
using System.Collections.Generic;
using System.Drawing;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Where things are on screen, as reported by components while they draw.
/// Effects target an anchor by key, so they follow a split row when the splits list scrolls.
/// All rectangles are in window pixels.
/// </summary>
public class EffectAnchors
{
    private const string SplitRowPrefix = "split-row:";

    /// <summary>
    /// The main timer's area.
    /// </summary>
    public const string Timer = "timer";

    /// <summary>
    /// The whole timer window.
    /// </summary>
    public const string Window = "window";

    private readonly Dictionary<string, RectangleF> _anchors = [];
    private readonly List<string> _removeBuffer = [];

    public static string SplitRow(int splitIndex)
    {
        return SplitRowPrefix + splitIndex;
    }

    /// <summary>
    /// Records <paramref name="localRect"/>, given in the coordinate space of <paramref name="g"/>, as the anchor <paramref name="key"/>.
    /// </summary>
    public void Set(string key, Graphics g, RectangleF localRect)
    {
        PointF[] corners =
        [
            new PointF(localRect.Left, localRect.Top),
            new PointF(localRect.Right, localRect.Bottom)
        ];
        using (System.Drawing.Drawing2D.Matrix transform = g.Transform)
        {
            transform.TransformPoints(corners);
        }

        float left = Math.Min(corners[0].X, corners[1].X);
        float top = Math.Min(corners[0].Y, corners[1].Y);
        float right = Math.Max(corners[0].X, corners[1].X);
        float bottom = Math.Max(corners[0].Y, corners[1].Y);
        _anchors[key] = RectangleF.FromLTRB(left, top, right, bottom);
    }

    /// <summary>
    /// Records <paramref name="windowRect"/>, already in window pixels, as the anchor <paramref name="key"/>.
    /// </summary>
    public void Set(string key, RectangleF windowRect)
    {
        _anchors[key] = windowRect;
    }

    public bool TryGet(string key, out RectangleF rect)
    {
        return _anchors.TryGetValue(key, out rect);
    }

    /// <summary>
    /// Finds the on-screen split row closest to <paramref name="preferredSplitIndex"/>.
    /// </summary>
    /// <returns>False if no split row is on screen.</returns>
    public bool TryGetSplitRowNear(int preferredSplitIndex, out int splitIndex)
    {
        splitIndex = -1;
        int bestDistance = int.MaxValue;
        foreach (KeyValuePair<string, RectangleF> anchor in _anchors)
        {
            if (anchor.Key.StartsWith(SplitRowPrefix, StringComparison.Ordinal)
                && int.TryParse(anchor.Key.Substring(SplitRowPrefix.Length), out int index)
                && anchor.Value.Width >= 1f && anchor.Value.Height >= 1f
                && Math.Abs(index - preferredSplitIndex) < bestDistance)
            {
                bestDistance = Math.Abs(index - preferredSplitIndex);
                splitIndex = index;
            }
        }

        return splitIndex >= 0;
    }

    public void Remove(string key)
    {
        _anchors.Remove(key);
    }

    /// <summary>
    /// Removes the split row anchors whose split index is not in <paramref name="visibleSplitIndices"/>,
    /// so effects don't draw on rows that have scrolled out of view.
    /// </summary>
    public void RetainSplitRows(ICollection<int> visibleSplitIndices)
    {
        _removeBuffer.Clear();
        foreach (string key in _anchors.Keys)
        {
            if (key.StartsWith(SplitRowPrefix, StringComparison.Ordinal)
                && int.TryParse(key.Substring(SplitRowPrefix.Length), out int index)
                && !visibleSplitIndices.Contains(index))
            {
                _removeBuffer.Add(key);
            }
        }

        foreach (string key in _removeBuffer)
        {
            _anchors.Remove(key);
        }
    }

    public void Clear()
    {
        _anchors.Clear();
    }
}
