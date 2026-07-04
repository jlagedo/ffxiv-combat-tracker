# UI Token Contract

The **stable, semantic styling seam** a plugin binds to so its in-shell UI (its `IUiContributor`
settings page, corner controls) matches the host shell without depending on the shell's private
palette. It is the theming counterpart to the typed data contract: a documented surface the shell
maps onto its internal look, so the shell can restyle — re-accent, add a light variant — without
breaking any plugin.

Ships in **`Fct.Abstractions.UI`** as three constant classes (`FctTokens`, `FctStyleClasses`,
`FctMetrics`). The shell (`Fct.App`) supplies the values: token brushes/fonts live in
[`src/Fct.App/App.axaml`](../src/Fct.App/App.axaml) (aliased onto the internal "Evercold" palette);
the blessed style classes live in [`src/Fct.App/Styles/PluginTokens.axaml`](../src/Fct.App/Styles/PluginTokens.axaml).
The reference consumer is [`samples/Fct.SamplePlugin`](../samples/Fct.SamplePlugin/SamplePlugin.cs).

## Two rules

1. **Bind tokens dynamically.** Use `{DynamicResource FctSurface}` in XAML, or `GetResourceObservable`/
   `DynamicResourceExtension` in code — never `{StaticResource}`. A static reference resolves once at
   load and would not follow a later restyle (light variant, re-accent). The blessed `fct-*` classes
   already use `DynamicResource` internally, so a control that only wears classes is automatically
   restyle-safe.
2. **Bind only the contract.** The `Fct*` keys and `fct-*` classes are contract. The shell's internal
   palette keys (`FrostBrush`, `GlacierBrush`, …) and internal classes (`.card`, `.nav`, …) are
   implementation — they can change without notice, so never bind them from a plugin.

## Brush tokens (`FctTokens`)

Resolve to `Avalonia.Media.IBrush`. Current mapping is the Dark-only "Evercold" palette; the shell may
re-map any of these.

| `FctTokens` | Resource key | Semantic role | Shell mapping (Dark) |
|---|---|---|---|
| `Background` | `FctBackground` | window/page backdrop | Abyss `#0A0D12` |
| `Surface` | `FctSurface` | card/panel surface | Glacier `#121822` |
| `SurfaceAlt` | `FctSurfaceAlt` | raised / hover surface | Rime `#1B2430` |
| `SurfaceHi` | `FctSurfaceHi` | selected / active surface | RimeHi `#22303F` |
| `Accent` | `FctAccent` | primary accent (focus, links, emphasis) | Frost `#8FD4F0` |
| `AccentDim` | `FctAccentDim` | muted accent / idle accented border | FrostDim `#5E93AE` |
| `Text` | `FctText` | primary text | Glint `#D8E8F2` |
| `TextDim` | `FctTextDim` | secondary / muted text | Hoarfrost `#6E8197` |
| `Emphasis` | `FctEmphasis` | warm highlight / badge | Ember `#E8A24C` |
| `Danger` | `FctDanger` | destructive / error | Warn `#D9695A` |
| `Hairline` | `FctHairline` | borders / dividers | Hairline `#22303C` |

## Font tokens (`FctTokens`)

Resolve to `Avalonia.Media.FontFamily`.

| `FctTokens` | Resource key | Role |
|---|---|---|
| `FontDisplay` | `FctFontDisplay` | display / heading typeface |
| `FontBody` | `FctFontBody` | body / UI typeface (Inter) |
| `FontMono` | `FctFontMono` | monospace (labels, metadata, log text) |

## Blessed style classes (`FctStyleClasses`)

The ergonomic path — each bundles the right token brushes, font, and sizing. Apply with
`Classes="fct-h1"` (XAML) or `control.Classes.Add(FctStyleClasses.H1)` (code).

| `FctStyleClasses` | Class | Target | Role |
|---|---|---|---|
| `Eyebrow` | `fct-eyebrow` | `TextBlock` | small, letter-spaced mono label |
| `H1` | `fct-h1` | `TextBlock` | page title |
| `H2` | `fct-h2` | `TextBlock` | section title |
| `Subtitle` | `fct-subtitle` | `TextBlock` | muted, wrapping subtitle |
| `Meta` | `fct-meta` | `TextBlock` | mono metadata line |
| `Body` | `fct-body` | `TextBlock` | body copy |
| `Card` | `fct-card` | `Border` | rounded surface + hairline border + padding |
| `Rule` | `fct-rule` | `Border` | 1px separator |
| `Ghost` | `fct-ghost` | `Button` | outlined accent action button |

## Layout metrics (`FctMetrics`)

Compile-time `double` constants (not resources): layout rhythm is not a theme concern, so code-built
views get plain values for `Thickness`/`Spacing`/`FontSize` with no resource lookup.

- Spacing: `SpaceXs` 4 · `SpaceSm` 8 · `SpaceMd` 12 · `SpaceLg` 16 · `SpaceXl` 24
- Type ramp: `FontEyebrow` 11 · `FontMeta` 12 · `FontBody` 13 · `FontH2` 19 · `FontH1` 30
- Shape: `CardCornerRadius` 14

## Usage

**XAML** (a UI-contributing plugin's view):

```xml
<Border Classes="fct-card">
  <StackPanel Spacing="8">
    <TextBlock Classes="fct-eyebrow" Text="MY PLUGIN"/>
    <TextBlock Classes="fct-h1" Text="Settings"/>
    <Border Height="2" Width="44" Background="{DynamicResource FctAccent}"/>
    <TextBlock Classes="fct-body" Text="Everything here matches the shell."/>
    <Button Classes="fct-ghost" Content="Do the thing"/>
  </StackPanel>
</Border>
```

**Code** (the pattern `Fct.SamplePlugin` uses — a page built as a `Func<Control>`):

```csharp
var title = new TextBlock { Text = "Settings" };
title.Classes.Add(FctStyleClasses.H1);

// A raw brush token via DynamicResource in code, so a shell restyle reaches it at runtime.
var rule = new Border { Height = 2, Width = 44 };
rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(FctTokens.Accent));

var card = new Border { Padding = new Thickness(FctMetrics.SpaceLg) };
card.Classes.Add(FctStyleClasses.Card);
```

Both work because a plugin's contributed `Control` is hosted (via `PluginSurfaceView`) inside the
shell's visual tree, and `Avalonia.*` is shared to the default `AssemblyLoadContext` — so the plugin's
control is the shell's own `Control` type and resolves `Application`-scoped resources and style
selectors. `DynamicResource` re-resolves when the control attaches, so it succeeds even though
`CreateView()` builds the control before it is parented.

## Versioning & the light-variant path

The contract is **additive-only**: keys and classes are added, never renamed or removed within a major
`Fct.Abstractions.UI` version. The shell maps them, so values change freely.

Today the shell is `RequestedThemeVariant="Dark"` with a flat palette. A future light variant needs no
contract change: move the token brushes into `ResourceDictionary.ThemeDictionaries` (`Light`/`Dark`
keys) in the shell — because every plugin already binds via `DynamicResource`, attached plugin controls
re-theme automatically.
