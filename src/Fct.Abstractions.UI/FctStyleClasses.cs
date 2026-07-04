namespace Fct.Abstractions.UI
{
    /// <summary>
    /// The blessed, plugin-facing <b>style-class</b> contract — the ergonomic path to a native-looking
    /// settings page. Add one of these class names to a control (<c>Classes="fct-h1"</c> in XAML, or
    /// <c>control.Classes.Add(FctStyleClasses.H1)</c> in code) and the shell styles it to match, using
    /// the tokens in <see cref="FctTokens"/> underneath. The shell owns the <c>fct-*</c> definitions and
    /// keeps its own internal classes (<c>.card</c>, <c>.nav</c>, …) private, so this set is stable
    /// across shell restyles.
    /// </summary>
    /// <remarks>
    /// Each constant is the raw class string, applied to the noted control type. Catalog: <c>docs/UI-TOKENS.md</c>.
    /// </remarks>
    public static class FctStyleClasses
    {
        // ── Typography (on TextBlock) ────────────────────────────────────────────────────────────────

        /// <summary>Small, letter-spaced monospace label (a section "eyebrow"). On <c>TextBlock</c>.</summary>
        public const string Eyebrow = "fct-eyebrow";

        /// <summary>Primary heading. On <c>TextBlock</c>.</summary>
        public const string H1 = "fct-h1";

        /// <summary>Secondary heading. On <c>TextBlock</c>.</summary>
        public const string H2 = "fct-h2";

        /// <summary>Dimmed, wrapping subtitle/description under a heading. On <c>TextBlock</c>.</summary>
        public const string Subtitle = "fct-subtitle";

        /// <summary>Dimmed monospace metadata (ids, counts, timestamps). On <c>TextBlock</c>.</summary>
        public const string Meta = "fct-meta";

        /// <summary>Default wrapping body copy. On <c>TextBlock</c>.</summary>
        public const string Body = "fct-body";

        // ── Surfaces (on Border) ─────────────────────────────────────────────────────────────────────

        /// <summary>A rounded card surface (background + hairline border + padding). On <c>Border</c>.</summary>
        public const string Card = "fct-card";

        /// <summary>A 1px horizontal separator rule. On <c>Border</c>.</summary>
        public const string Rule = "fct-rule";

        // ── Buttons (on Button) ──────────────────────────────────────────────────────────────────────

        /// <summary>The outline/"ghost" accent button — the default styled action button. On <c>Button</c>.</summary>
        public const string Ghost = "fct-ghost";
    }
}
