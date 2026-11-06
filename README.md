# Antril

Antril is a programming language (`.ant`) compiled by [Larva](../Larva). This repository holds Antril itself:

| Folder | What it is |
| --- | --- |
| `src/Antril` | The language pack: the frontend (Antril source to Larva HIR), its view (HIR written back as Antril, for decompiled types) and its editor services, behind the entry points larva loads. |
| `tests/Antril.Tests` | xUnit v3 tests: the frontend, Larva's standard library (written in Antril), the editor services, the pack loaded by larva (`larva build`, `larva lsp`, `larva bind --profile`), and Antril programs run on every Larva back end. |
| `vscode-antril` | The VS Code extension for `.ant` files: syntax, snippets, icon and semantic colors. It depends on the Larva extension, which brings the language server and the commands. |
| `tools` | `publish-pack.ps1` / `publish-pack.sh`: the pack compiled natively, for Larva's installers. |

## Building

Antril builds against Larva's libraries. Keep a Larva working copy next to this one (`..\Larva`), or set `LarvaRoot`:

```
dotnet build Antril.slnx
dotnet test --project tests/Antril.Tests
```

A build also writes the pack as .NET code to `artifacts/pack/Antril`. To use it with a working copy of larva, point larva at that folder:

```
set LARVA_LANGUAGES=C:\path\to\Antril\artifacts\pack
dotnet ..\Larva\Larva.Cli\bin\Debug\net10.0\larva.dll languages
```

## Shipping

`tools/publish-pack.ps1` (Windows) or `tools/publish-pack.sh` (Linux, macOS) writes the native pack to `artifacts/languages/Antril`. Larva's installer build takes it from there by default (`..\Antril\artifacts\languages`), signs it and ships it in `Languages/Antril`.

Larva's standard library (`System.Core`, `System.Bindings`) is written in Antril but belongs to Larva: it stays in Larva's `Library/`, and Larva's installer compiles it with this pack.
