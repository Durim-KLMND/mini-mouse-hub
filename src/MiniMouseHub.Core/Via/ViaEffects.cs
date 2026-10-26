namespace MiniMouseHub.Core.Via;

/// <summary>
/// Names for RGB effect numbers. VIA can't list a keyboard's effects: the numbers depend on which
/// effects its firmware was built with, so known keyboards get their list here and others get numbers.
/// </summary>
public static class ViaEffects
{
    /// <summary>
    /// The GMMK Pro's effects in QMK's "via" keymap (from keyboards/gmmk/pro/info.json, in the order
    /// of quantum/rgb_matrix/animations/rgb_matrix_effects.inc).
    /// </summary>
    public static readonly IReadOnlyList<string> GmmkPro =
    [
        "Solid color", "Alphas and mods", "Gradient up and down", "Gradient left and right", "Breathing",
        "Band (saturation)", "Band (brightness)", "Pinwheel band (saturation)", "Pinwheel band (brightness)",
        "Spiral band (saturation)", "Spiral band (brightness)", "Cycle all", "Cycle left and right",
        "Cycle up and down", "Rainbow chevron", "Cycle out and in", "Cycle out and in (dual)", "Cycle pinwheel",
        "Cycle spiral", "Dual beacon", "Rainbow beacon", "Rainbow pinwheels", "Raindrops", "Jellybean raindrops",
        "Hue breathing", "Hue pendulum", "Hue wave", "Pixel rain", "Pixel flow", "Pixel fractal",
        "Typing heatmap", "Digital rain", "Reactive (simple)", "Reactive", "Reactive wide", "Reactive multi-wide",
        "Reactive cross", "Reactive multi-cross", "Reactive nexus", "Reactive multi-nexus", "Splash",
        "Multi-splash", "Solid splash", "Solid multi-splash",
    ];

    /// <summary>
    /// The 22 effects Keychron's RGB boards (Q, V and K Pro series) are usually built with, from the
    /// ENABLE_RGB_MATRIX_* list in their config.h plus solid color, in QMK's effect order. Models differ, so this is
    /// only used when the keyboard reports exactly this many effects.
    /// </summary>
    public static readonly IReadOnlyList<string> Keychron =
    [
        "Solid color", "Breathing", "Band spiral", "Cycle all", "Cycle left and right", "Cycle up and down",
        "Rainbow chevron", "Cycle out and in", "Cycle out and in (dual)", "Cycle pinwheel", "Cycle spiral",
        "Dual beacon", "Rainbow beacon", "Jellybean raindrops", "Pixel rain", "Typing heatmap", "Digital rain",
        "Reactive (simple)", "Reactive multi-wide", "Reactive multi-nexus", "Splash", "Solid splash",
    ];

    public const int KeychronVendorId = 0x3434;

    private const int MostEffects = 44;

    /// <param name="count">How many effects the keyboard says it has, when it was asked.</param>
    public static IReadOnlyList<string> For(int vendorId, int productId, int? count = null) => (vendorId, productId) switch
    {
        // GMMK Pro, ANSI and ISO, both board revisions.
        (0x320F, 0x5044) => GmmkPro,
        (KeychronVendorId, _) when count == Keychron.Count => Keychron,
        _ => Numbered(count ?? MostEffects),
    };

    /// <summary>Whether the app should ask the keyboard how many effects it has, because it has no list for it.</summary>
    public static bool NeedsCount(int vendorId, int productId) => (vendorId, productId) != (0x320F, 0x5044);

    private static IReadOnlyList<string> Numbered(int count) => Enumerable.Range(1, Math.Clamp(count, 1, 255)).Select(n => $"Effect {n}").ToArray();
}
