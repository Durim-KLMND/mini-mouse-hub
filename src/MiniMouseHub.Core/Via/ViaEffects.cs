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

    private static readonly IReadOnlyList<string> Numbered = Enumerable.Range(1, 44).Select(n => $"Effect {n}").ToArray();

    public static IReadOnlyList<string> For(int vendorId, int productId) => (vendorId, productId) switch
    {
        // GMMK Pro, ANSI and ISO, both board revisions.
        (0x320F, 0x5044) => GmmkPro,
        _ => Numbered,
    };
}
