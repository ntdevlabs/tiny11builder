# Interface language files

Tiny11 Builder reads UTF-8 JSON files from the `Languages` folder beside the executable. English is the baseline; the initial Simplified Chinese catalog is optional. Contributions for any language are welcome, without editing C# or XAML.

## Adding or improving a language

1. Copy `src\Tiny11Builder.App\Languages\en.json` to a culture-named file such as `fr.json` or `pt-BR.json` in the same folder.
2. Set `language` to that file's name without `.json`, and `name` to the language's native display name.
3. Translate values inside `strings`. Keep every key unchanged. You may omit unfinished entries: they explicitly fall back to English, never to a blank label.
4. Rebuild, or copy the JSON beside the executable into `Languages`, restart the app, and select it under **Interface language**. New files are discovered at startup. Editing a translation does not require recompiling the app.

Example of an intentionally incomplete contribution:

```json
{
  "language": "fr",
  "name": "Français",
  "strings": {
    "Common.Next": "Suivant",
    "Common.Cancel": "Annuler"
  }
}
```

Use the current `en.json` as the complete key catalog. `Text.*` keys cover static wizard labels and help; `Option.<id>.Title` and `.Description` cover all removal groups. `Common.*`, `Navigation.*`, `Review.*`, `Resume.*`, `Progress.*`, `Core.*`, `Updates.*` and `Result.*` cover dynamic interface text and confirmations.

## Formatting and validation

- Preserve placeholders exactly, including their number and formatting: `{0}`, `{1}`, `{0:N0}`, `{1:0}`. They may be reordered to fit the language, but not removed, duplicated, renumbered, or given different formatting/alignment.
- Use `\n` for a line break, `\"` for a quotation mark, and `\\` for a backslash inside a JSON string. Literal format braces must be doubled: `{{` and `}}`.
- Empty values, unknown or duplicate keys, unknown top-level properties, invalid JSON, and placeholder mismatches reject that file. Other languages still load; English always remains available from the built-in baseline. A warning offers the file-specific diagnostics.
- The file name must match `language`. Only top-level JSON files are loaded, with a 2 MiB size limit per file.
- Values are plain text. They cannot contain executable XAML, commands, navigation destinations, or payment settings. App links are fixed in code; translation files cannot change them.

Catalog tests validate the shipped files, fallback behavior, placeholder compatibility, serializable language choices, and the static XAML key mappings:

```powershell
dotnet test tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter FullyQualifiedName~LocalizationCatalogTests
```

## Selection and scope

The default is **System default**: a matching supported system language or language family is used, then English. An explicit choice is saved locally in `%LOCALAPPDATA%\Tiny11Builder\ui-language.json`. If a chosen file is later missing or invalid, selection falls back to the supported system language and then English. An incomplete valid file keeps the chosen language and uses English for its missing entries.

Language changes affect the interface only. They do not change the Windows ISO's language, selected removals, update-package order, or build recipe. The picker is guarded while an operation or modal interaction is active.

Navigation, settings, preset descriptions, removal-option titles/help, confirmations, cancellation prompts, status labels, review summaries and result labels use the catalog. Image names, package/component identifiers, enum/recipe values, Windows tool output, underlying Core errors, and detailed diagnostic logs remain original data and may be in English. The build page announces this distinction. Exported inventory uses stable English field headings and unchanged component kinds/identifiers, with documented escaping; it is a local file, not a network upload.

Windows' default WinUI fonts provide Chinese glyph fallback. No code-page setting or additional font package is required.
