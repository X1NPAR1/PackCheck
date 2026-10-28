# Localization

PackCheck ships in English, Turkish, German, French and Russian.

## Where strings live

`tools/strings.py` is the only place UI text is written. Each key has one entry per language in the order `en, tr, de, fr, ru`:

```python
"Console_AskAi": ["Ask the assistant", "Asistana sor", "Assistent fragen", "Demander à l'assistant", "Спросить ассистента"],
```

Run `python tools/strings.py` after editing. It regenerates `src/PackCheck.App/Localization/Strings*.resx` and fails if any language is missing a key.

## Using a string

- XAML: `Text="{l:Loc Console_AskAi}"`. The binding updates when the language changes; no restart.
- C#: `Loc.T("Key")`, or `Loc.F("Key", arg0, ...)` for strings with `{0}` placeholders.
- Category labels for the plugin browser use `Category_<source key>`; unknown keys fall back to a title-cased key.

## Rules

- Keep placeholders (`{0}`) and keyboard hints in every translation.
- Numbers and dates are formatted with `Loc.Instance.Culture`.
- Server-side text (console output, plugin descriptions from Modrinth/Hangar) is shown as it comes and is not translated.
- Fonts (Inter, Exo 2, JetBrains Mono) are bundled with Latin Extended and Cyrillic glyphs; `tests/PackCheck.Tests/FontCoverageTests.cs` checks every character used in the RESX files against the bundled fonts.

## Adding a language

1. Add the culture to `LANGS` in `tools/strings.py` and a column to every entry.
2. Add the language to `Loc.Languages` with a round flag in `src/PackCheck.App/Assets/Flags`.
3. Add the Inno Setup messages file and the custom messages to `installer/PackCheck.iss`.
