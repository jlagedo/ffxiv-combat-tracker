using System;
using System.Globalization;

namespace Fct.App.Lang;

// Resolves the UI culture once, at process start, from the OS locale — falling back to English
// when no matching Resources.<culture>.resx satellite exists yet. Path A (see docs/ARCHITECTURE.md
// discussion): x:Static bindings don't refresh at runtime, so a future language switch in Settings
// takes effect on next launch rather than live — restart-on-change, not a reactive service.
public static class LocalizationStartup
{
    // Every culture with a Resources.<culture>.resx satellite. English is the neutral default and
    // always first; add an entry here when a translated satellite resx is added for a new language.
    public static readonly CultureInfo[] SupportedCultures = { CultureInfo.GetCultureInfo("en") };

    public static void Initialize()
    {
        var resolved = Resolve(CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = resolved;
        CultureInfo.CurrentUICulture = resolved;
        Resources.Culture = resolved;
    }

    // Walks from the requested culture up through its parents (e.g. pt-BR -> pt) looking for a
    // supported match, so a regional variant still resolves to its base language when only the
    // base is translated. Falls back to the default (first) supported culture.
    internal static CultureInfo Resolve(CultureInfo requested)
    {
        for (var c = requested; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            foreach (var supported in SupportedCultures)
                if (supported.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
                    return supported;
        }
        return SupportedCultures[0];
    }
}
