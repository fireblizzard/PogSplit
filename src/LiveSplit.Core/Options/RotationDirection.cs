namespace LiveSplit.Options;

/// <summary>
/// Which way the time saved number turns.
/// </summary>
public enum RotationDirection
{
    Clockwise,
    Anticlockwise,

    /// <summary>
    /// Picks a direction and an amount up to the rotation setting each time.
    /// </summary>
    Random
}
