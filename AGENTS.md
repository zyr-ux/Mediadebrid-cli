# MediaDebrid-cli Agent Guidelines

Welcome to the `MediaDebrid-cli` repository! This document provides AI agents (and human contributors) with essential context, architectural understanding, and coding standards for this project.

## Project Overview
`MediaDebrid-cli` is a command-line interface (CLI) application built using **.NET 10.0** and C#. It acts as a powerful, feature-rich downloader utilizing the Real-Debrid API to resolve magnet links and download media files efficiently. It features a rich Terminal User Interface (TUI) for an enhanced user experience, including resumable parallel downloads.

### Core Technologies
- **.NET 10.0** (C#)
- **Spectre.Console**: Used extensively for the Terminal User Interface (TUI), including progress bars, spinners, layout, and styled text.
- **System.CommandLine**: Handles CLI argument parsing and command routing.
- **DotNetEnv**: Loads environment variables from `.env` files.

---

## Architecture & Directory Structure

The codebase follows a modular structure separating UI, services, and models.

### `Program.cs` & `Settings.cs`
- **`Program.cs`**: The entry point of the application. It configures `System.CommandLine`, parses arguments, loads environment variables (via `DotNetEnv`), and boots up the `TuiApp`.
- **`Settings.cs`**: Manages application-level settings, integrating both CLI arguments and `.env` configurations.

### `Tui/` (Terminal UI)
- **`TuiApp.cs`**: The core of the user interface. It manages the Spectre.Console components (e.g., `Progress`, layouts, panels, tables). It is responsible for orchestrating the overall flow, displaying download progress, handling user input for cancellation/pausing, and logging formatted status messages.

### `Services/` (Core Business Logic)
This directory contains the heavy lifting of the application.
- **`RealDebridClient.cs`**: Handles all HTTP interactions with the Real-Debrid API (adding magnets, selecting files, un-restricting links).
- **`Downloader.cs`**: Manages the actual file downloading process. It supports segmented (parallel) downloads, persistent resume capabilities, and precise speed/ETA calculations.
- **`MetadataResolver.cs`**: Responsible for determining what files need to be downloaded from a parsed magnet link and interacting with the `RealDebridClient` to get the final download URLs.
- **`MagnetParser.cs`**: Utility for parsing and validating magnet link structures.
- **`PathGenerator.cs`**: Constructs clean, organized, and valid file system paths for the downloaded media based on its metadata.
- **`Utils.cs`**: General utility methods (e.g., formatting byte sizes to human-readable strings, extracting names).

### `Models/` (Data Structures & Errors)
Contains POCO classes, DTOs, and application state objects.
- **`Exceptions.cs`**: **CRITICAL** - This file centralizes all custom exceptions (e.g., `RealDebridException`, `DownloadCancelledException`, `FileSelectionException`). Always use these predefined exceptions rather than throwing generic `Exception`s.
- **`RealDebridModels.cs`**: JSON mapping models for Real-Debrid API responses.
- **`DownloadProgressModel.cs` & `ResumeMetadata.cs`**: Models tracking the state of active and resumable downloads.

---

## Coding Guidelines & Rules

When modifying or generating code for `MediaDebrid-cli`, adhere strictly to the following guidelines:

### 1. Terminal UI (TUI) Rules
- **Do not use standard `Console.WriteLine`** for primary output. Always route user-facing output through `Spectre.Console` (via `AnsiConsole.MarkupLine` or the internal logging mechanisms within `TuiApp`).
- When updating progress or showing status, ensure thread-safety if interacting with Spectre.Console's `ProgressContext` from background tasks.
- Keep the TUI responsive. Heavy blocking operations should be offloaded to asynchronous tasks.

### 2. Error Handling & Exceptions
- **Always use `Models/Exceptions.cs`**: If you need to throw an error related to application logic, find the appropriate custom exception in `Exceptions.cs` or add a new one there. Do not create orphaned exception classes throughout the project.
- Ensure graceful degradation. Provide clear, human-readable error messages using `Spectre.Console`'s red markup (e.g., `[red]Error: ...[/]`) when things fail.

### 3. Asynchronous Programming
- The application relies heavily on `async/await`. Avoid using `.Result` or `.Wait()` which can cause deadlocks.
- Always pass and respect `CancellationToken`s. Downloads and API calls must be cancellable to support the TUI's "exit/cancel" features properly.

### 4. Language Features
- **Nullable Reference Types**: Enabled globally (`<Nullable>enable</Nullable>`). Ensure proper null-checking (`?`, `!`, `??`) to avoid compiler warnings.
- **C# 12/13 Features**: Utilize modern C# features like primary constructors, collection expressions (`[]`), and raw string literals where they improve readability.

### 5. Resumable Downloads
- The `Downloader.cs` uses a 4KB file footer to store JSON metadata (`ResumeMetadata`) allowing downloads to persist across application restarts. When modifying download logic, ensure this metadata is correctly parsed, updated, and re-written, avoiding data corruption.

---

## Development Workflow

- **Build**: `dotnet build`
- **Run**: `dotnet run -- [arguments]`
- **Format**: Follow standard C# naming conventions (PascalCase for classes/methods/properties, camelCase for variables/fields, `_camelCase` for private fields).

*When in doubt, prioritize user experience in the TUI and robust error handling!*
