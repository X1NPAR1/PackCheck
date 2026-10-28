# Design system

The palette comes from the logo: graphite background, moss-green cube, stone grey. No blue/purple gradients.

## Color tokens

Defined in `src/PackCheck.App/Themes/Dark.xaml` and `Light.xaml`. Always use the token through `DynamicResource` so theme switching works.

| Token | Dark | Light | Use |
|---|---|---|---|
| `BgBrush` | `#1A1A1A` | `#F3F3F1` | window background |
| `SurfaceBrush` | `#212121` | `#FFFFFF` | cards |
| `Surface2Brush` | `#2A2A2A` | `#EEEEEB` | inputs, chips, secondary fills |
| `LineBrush` | `#3A3A3A` | `#D9D9D4` | borders, separators |
| `TextBrush` | `#ECEAE3` | `#1F2220` | body text |
| `TextMutedBrush` | `#A6A6A1` | `#55574F` | secondary text |
| `PrimaryBrush` | `#5E9A3E` | `#3F7A2A` | primary buttons, active navigation |
| `PrimarySoftBrush` | `#2A3324` | `#E3ECDB` | selected rows, badges |
| `PassBrush` | `#7DBA57` | `#3E7A27` | pass, healthy |
| `WarnBrush` | `#D1AE45` | `#8A6A0E` | warnings |
| `FailBrush` | `#D0645A` | `#A93B32` | failures, destructive actions |
| `SkipBrush` | `#8A8B84` | `#6B6C66` | skipped / not run |
| `InfoBrush` | `#7F9F98` | `#4F6F68` | information |

The console has its own fixed dark palette (`Console*Brush`) in both themes so log colors stay readable.

Health gauges use five levels: excellent, good, fair, poor, critical.

## Type

| Resource | Font | Use |
|---|---|---|
| `DisplayFont` | Exo 2 | page titles (`H1`, `H2`), big numbers |
| `UiFont` | Inter | everything else |
| `MonoFont` | JetBrains Mono | paths, ids, console, code |

All three are bundled (SIL Open Font License) with Latin Extended and Cyrillic.

## Components (`Themes/Styles.xaml`)

- `Card`: surface, 1 px line, 10 px radius, 18 px padding.
- Buttons: `BaseButton` (secondary), `PrimaryButton`, `GhostButton` (toolbar/text actions). Destructive actions ask for confirmation.
- `TabPill` radio buttons for tabs, `Chip` toggle buttons for filters, `Switch` check boxes for settings.
- Text: `H1`, `H2`, `Label` (small caps-like labels above inputs), `Muted`.
- Inputs show their hint through `Tag` (placeholder text).

## Motion

`Anim.PopIn` (fade + slight scale) and `Anim.Stagger` (list items one after another). Page changes slide/fade. The Animations setting (Full / Reduced / Off) is the only switch; nothing animates when it is Off.

## Accessibility

- Every interactive control has an `AutomationProperties.Name` or visible text; important ones have an `AutomationId` (used by the UI tests).
- Status is never color-only: each status also has a word (Pass, Warning, Error, Skipped).
- Text and primary colors meet WCAG AA contrast on their surfaces in both themes.
