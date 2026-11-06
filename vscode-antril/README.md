# Antril for Visual Studio Code

The [Antril](https://claude.ai/artifact/Lx8F2U4rRx58X3YP4CrAis) language (`.ant`) in VS Code: its syntax highlighting, snippets, file icon and semantic colors.

Antril is compiled by Larva. This extension installs the **Larva** extension with it, which brings the rest: its language server (`larva lsp`, through Antril's Larva language pack) reports errors as you type and gives completion, hover, go to definition, signature help and the outline. Its commands create, run, check and build Antril projects (`.queen`) and bind native libraries into Antril.

## Features

- **Syntax highlighting** for `.ant` files, and for the Antril expressions in a `.queen` project (`$[…]`).
- **Semantic colors**: types from the standard library, static methods, enum members, fields, constants, exceptions, attributes and flag enums, each with its own color.
- **Snippets** for Antril's declarations and statements.
- **File icon**: the ant.
- **From the Larva extension**: errors as you type (the compiler's own diagnostics: parsing, names and types, borrow and region checks), completion, hover with `///` documentation, go to definition (including into the standard library), *Larva: New Project from a Template* (written in Antril), *Run* (Ctrl+F5), *Check*, *Build*, *Bind a Native Library*, *Generate Bindings from the Profile Open* and *Show Type (decompiled)* in Antril.

## Requirements

- **Larva 0.8.0 or newer** with its Antril language pack (`Languages/Antril`; `larva languages` lists it). Install Larva on its own, with its installer.
- The **Larva** extension, which VS Code installs with this one.

## Building the extension

```
npm install
npm run package          # makes antril-VERSION.vsix
code --install-extension antril-0.9.0.vsix
```
