namespace Fct.Abstractions.UI
{
    /// <summary>
    /// The stable, semantic <b>design-token contract</b> a plugin binds to so its in-shell UI matches
    /// the host's look without depending on the shell's private palette. Each field is an Avalonia
    /// <b>resource key</b> present in the shell's <c>Application</c> scope; the shell maps it onto its
    /// internal theme (today the "Evercold" palette) and may re-map it — add a light variant, re-accent —
    /// without breaking any plugin. Bind these with <c>{DynamicResource}</c> (or, in code, an observable
    /// resource binding) so a later restyle reaches attached plugin controls at runtime; a
    /// <c>StaticResource</c> would bake the value at load and defeat that.
    /// </summary>
    /// <remarks>
    /// These are the restyle-able single-value tokens (brushes, fonts). For ready-made typography and
    /// surface treatments, prefer the blessed style classes in <see cref="FctStyleClasses"/>, which are
    /// themselves defined over these tokens; for layout rhythm (spacing/radii) use the compile-time
    /// <see cref="FctMetrics"/> constants. Catalog + shell mapping: <c>docs/UI-TOKENS.md</c>.
    /// </remarks>
    public static class FctTokens
    {
        // ── Brushes ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The window/page backdrop — the darkest field. Rarely needed inside a settings page
        /// (the page already sits on it); use for a full-bleed custom surface.</summary>
        public const string Background = "FctBackground";

        /// <summary>The default raised surface: card and panel backgrounds.</summary>
        public const string Surface = "FctSurface";

        /// <summary>A slightly lifted surface — hover fills, nested rows, selected states.</summary>
        public const string SurfaceAlt = "FctSurfaceAlt";

        /// <summary>The most-lifted surface — selected/active nested rows.</summary>
        public const string SurfaceHi = "FctSurfaceHi";

        /// <summary>The primary accent — focus rings, active affordances, links, primary emphasis.</summary>
        public const string Accent = "FctAccent";

        /// <summary>A muted accent — idle borders on accented controls, secondary marks.</summary>
        public const string AccentDim = "FctAccentDim";

        /// <summary>Primary foreground text (headings, body copy on a surface).</summary>
        public const string Text = "FctText";

        /// <summary>Secondary/dimmed foreground — captions, metadata, placeholder labels.</summary>
        public const string TextDim = "FctTextDim";

        /// <summary>A warm emphasis accent for badges / "live" highlights, distinct from <see cref="Accent"/>.</summary>
        public const string Emphasis = "FctEmphasis";

        /// <summary>The danger/error accent — destructive actions, error text.</summary>
        public const string Danger = "FctDanger";

        /// <summary>The hairline/border color for separators and control outlines.</summary>
        public const string Hairline = "FctHairline";

        // ── Fonts ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>The display/heading font family (used by <see cref="FctStyleClasses.H1"/>/<see cref="FctStyleClasses.H2"/>).</summary>
        public const string FontDisplay = "FctFontDisplay";

        /// <summary>The body/UI text font family.</summary>
        public const string FontBody = "FctFontBody";

        /// <summary>The monospace font family for labels, metadata, and code/log text.</summary>
        public const string FontMono = "FctFontMono";
    }
}
