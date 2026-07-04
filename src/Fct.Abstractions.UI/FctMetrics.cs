namespace Fct.Abstractions.UI
{
    /// <summary>
    /// Numeric layout constants (device-independent pixels) matching the shell's rhythm — spacing scale,
    /// type ramp, and corner radius. These are <b>compile-time constants</b>, not resources: layout
    /// rhythm is not a theme concern, so code-built views get plain <see cref="double"/> values for
    /// <c>Thickness</c>/<c>Spacing</c>/<c>FontSize</c> without a resource lookup. (Color and font <em>are</em>
    /// theme concerns — those stay restyle-able tokens in <see cref="FctTokens"/>.) The type-ramp values
    /// mirror the <see cref="FctStyleClasses"/> typography classes; prefer the classes when you can.
    /// </summary>
    public static class FctMetrics
    {
        // ── Spacing scale ────────────────────────────────────────────────────────────────────────────

        /// <summary>Extra-small gap (4).</summary>
        public const double SpaceXs = 4;

        /// <summary>Small gap (8) — tight stacks, inline spacing.</summary>
        public const double SpaceSm = 8;

        /// <summary>Medium gap (12) — the default control-to-control spacing.</summary>
        public const double SpaceMd = 12;

        /// <summary>Large gap (16) — section padding and separation.</summary>
        public const double SpaceLg = 16;

        /// <summary>Extra-large gap (24) — major section separation.</summary>
        public const double SpaceXl = 24;

        // ── Type ramp (mirrors the fct-* typography classes) ─────────────────────────────────────────

        /// <summary>Eyebrow label size (11).</summary>
        public const double FontEyebrow = 11;

        /// <summary>Metadata size (12).</summary>
        public const double FontMeta = 12;

        /// <summary>Body copy size (13).</summary>
        public const double FontBody = 13;

        /// <summary>Secondary heading size (19).</summary>
        public const double FontH2 = 19;

        /// <summary>Primary heading size (30).</summary>
        public const double FontH1 = 30;

        // ── Shape ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Corner radius of a card surface (14), matching <see cref="FctStyleClasses.Card"/>.</summary>
        public const double CardCornerRadius = 14;
    }
}
