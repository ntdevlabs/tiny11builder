# Interface translations

The WinUI app resolves its interface text through `Strings\<language>\Resources.resw`.
English is the source language; every other language must contain the same keys.

| Language | Tag | File |
| --- | --- | --- |
| English | `en-US` | `src/Tiny11Builder.App/Strings/en-US/Resources.resw` |
| Traditional Chinese | `zh-Hant` | `src/Tiny11Builder.App/Strings/zh-Hant/Resources.resw` |
| Simplified Chinese | `zh-Hans` | `src/Tiny11Builder.App/Strings/zh-Hans/Resources.resw` |

Choose a language under **Language** in the sidebar. The choice is saved in
`%LOCALAPPDATA%\Tiny11Builder\settings.json` and applied after a restart, because
XAML resolves its text when a page is loaded.

## Add a language

1. Copy `Strings\en-US\Resources.resw` to `Strings\<tag>\Resources.resw`, using a
   BCP-47 tag such as `ja-JP` or `de-DE`.
2. Translate every value. Keep the `name` attributes and the `{0}` placeholders
   unchanged; a placeholder may move within a sentence.
3. Add the tag and its native name to `LanguageCatalog.All` in
   `src/Tiny11Builder.App/LanguageCatalog.cs`.
4. Run `dotnet test tests/Tiny11Builder.Core.Tests`. `LocalizationTests` checks
   that every language has exactly the English keys, no empty values, and the
   same placeholders, and that every `{loc:Tr Key=…}` used by XAML exists.

Keys are flat and use `__` between words (`Build__Result__Saved`). A key must not
be a prefix of another key: MRT treats dots as resource-map separators, so flat
keys keep a resource from also becoming a scope.

## What stays English

Build-engine operation names, stage status words, the raw build recipe and the
build log are technical diagnostics. They are not translated by these files; the
interactive interface, removal catalog and user-facing notices are.
