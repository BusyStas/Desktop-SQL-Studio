# Desktop-SQL-Studio

A lightweight SSMS-style SQL Server client for Windows (C# WPF, .NET 8).

## Features (first scaffold)

- T-SQL editor with syntax highlighting (AvalonEdit)
- Multiple query windows (tabs)
- **Never lose a query:** every window is autosaved 500 ms after you stop typing, and again on exit or crash. Closing a window moves its file to `Closed/` instead of deleting it.
- Execute selected text or the whole script (F5 / Ctrl+E); `GO` batch separators supported; cancel a running query
- Multiple result sets shown in grids, plus a Messages tab
- Server groups (Group > Server tree on the left); each server has a default database and its own list of databases, chosen from a dropdown per query window
- Named credentials (SQL login, current Windows user, or a different Windows user) stored once and referenced by servers

## Configuration

On first start the app creates `%APPDATA%\DesktopSqlStudio\config.json`. See `src/DesktopSqlStudio/config.example.json` for all options: `Credentials` (named logins), then `ServerGroups` > `Servers`, each with `Address`, `Credential`, `DefaultDatabase` and `Databases`. Server names must be unique across groups. The database list is read from the config only (the default database is always included). Use **File > Edit Config** then **File > Reload Config**.

Passwords are stored in plain text for now (encryption is planned). `config.json` is git-ignored; never commit it.

## Autosave folder

Defaults to `Documents\DesktopSqlStudio\Queries`; change `AutosaveFolder` in the config. Make it its own git repo (separate from this one) to keep history. Each window is one `.sql` file plus a `session.json` listing open windows.

## Build

First install the .NET SDK: see [install.md](install.md).

```
build.bat   (builds the solution)
run.bat     (builds and starts the app)
dotnet test (runs the unit tests)
```

The tests cover batch splitting, autosave and config only. They never connect to a database.
