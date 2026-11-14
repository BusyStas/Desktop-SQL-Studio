# Desktop-SQL-Studio

A lightweight SSMS-style SQL Server client for Windows (C# WPF, .NET 10).

## Features (first scaffold)

- T-SQL editor with syntax highlighting (AvalonEdit)
- Multiple query windows (tabs)
- **Never lose a query:** every window is autosaved 500 ms after you stop typing, and again on exit or crash. Closing a window keeps its file (only empty scripts are removed).
- Scripts live in `root\<server>\<database>\<type>\<name>.sql`. Every script always has a type; type buttons on the toolbar change it, and changing the type, database or server moves the file.
- The left tree shows server > database > type > scripts; click a script to open it.
- Execute selected text or the whole script (F5 / Ctrl+E); `GO` batch separators supported; cancel a running query
- Multiple result sets shown in grids, plus a Messages tab
- Server groups (Group > Server tree on the left); each server has a default database and its own list of databases, chosen from a dropdown per query window
- Named credentials (SQL login, current Windows user, or a different Windows user) stored once and referenced by servers

## Structure

- `DesktopSqlStudio.Core`: all logic (config, servers and databases, query windows, autosave, running queries). No UI.
- `DesktopSqlStudio` (WPF): front end with the AvalonEdit T-SQL editor.
- `DesktopSqlStudio.WinForms`: second front end on the same library; plain-text editor, no syntax highlighting yet.
- `DesktopSqlStudio.Tests`: tests for Core only; they never connect to a database.

Both front ends share the same `config.json` and autosave folder.

## Configuration

On first start the app creates `%APPDATA%\DesktopSqlStudio\config.json`. See `src/DesktopSqlStudio/config.example.json` for all options: `Credentials` (named logins), then `ServerGroups` > `Servers`, each with `Address`, `Credential`, `DefaultDatabase` and `Databases`. Server names must be unique across groups. The database list is read from the config only (the default database is always included). Use **File > Edit Config** then **File > Reload Config**.

Passwords are stored in plain text for now (encryption is planned). `config.json` is git-ignored; never commit it.

## Autosave folder and script layout

Defaults to `Documents\DesktopSqlStudio\Queries`; change `AutosaveFolder` in the config. Make it its own git repo (separate from this one) to keep history.

```
<AutosaveFolder>\<server>\<database>\<type>\<name>.sql
<AutosaveFolder>\_unassigned\<type>\<name>.sql   (scripts with no server yet)
<AutosaveFolder>\session.json                      (which scripts are open)
```

Script files from the first version (loose `.sql` files in the root) are moved into `_unassigned\<default type>` on start.

## Script types

Types come from `%APPDATA%\DesktopSqlStudio\script-types.yaml`, created on first start (File > Edit Script Types, then File > Reload Config):

```yaml
default: scripts
types:
  - scripts
  - troubleshooting
  - cleanup
  - analytics
```

The default type is added to the list if missing. A broken file falls back to the built-in list with a warning.

## Build

First install the .NET SDK: see [install.md](install.md).

```
build.bat   (builds the solution)
run.bat     (builds and starts DesktopSqlStudio.exe)
run-winforms.bat (builds and starts the WinForms front end)
run-dll.bat (builds and starts via `dotnet DesktopSqlStudio.dll`; use this if Windows blocks the unsigned exe)
dotnet test (runs the unit tests)
```

The tests cover batch splitting, script storage, types, workspace logic and config only. They never connect to a database.
