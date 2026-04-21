# MediaDebrid-cli

MediaDebrid-cli is a powerful, feature-rich command-line interface (CLI) application built using **.NET 10.0** and C#. It acts as an advanced downloader that utilizes the **Real-Debrid API** to quickly resolve magnet links and download media files directly to your system. 

It features a stunning Terminal User Interface (TUI) powered by Spectre.Console, and under the hood, it handles complex parallel downloads with robust resuming capabilities.

---

## ✨ Features

- **Real-Debrid Integration**: Seamlessly adds magnet links, selects appropriate files, un-restricts links, and fetches direct download URLs.
- **Rich Terminal UI**: Beautiful, responsive console interface with live progress bars, spinners, and colored logs.
- **Segmented / Parallel Downloads**: Splits files into multiple chunks and downloads them concurrently for maximum throughput.
- **Robust Resumable Downloads**: Persists download state natively within the file itself, allowing downloads to survive application crashes, manual cancellations, or network drops.
- **Smart Categorization**: Automatically generates clean file paths and routes downloads to appropriate directories based on media type (Movies/Shows, Games, Others).
- **Interactive Mode**: Launch the app without arguments to enter an intuitive, guided TUI mode.
- **Pause/Cancel Support**: Gracefully pause or safely cancel downloads with keyboard shortcuts (`p` to pause, `Ctrl+C` or `x` to cancel/exit) while preserving progress.

---

## 🚀 Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A Premium [Real-Debrid](https://real-debrid.com/) Account & API Token.

---

## 🛠️ Installation & Build

Clone the repository and build the project using the .NET CLI:

```bash
git clone https://github.com/yourusername/MediaDebrid-cli.git
cd MediaDebrid-cli
dotnet build
```

Run the application:
```bash
dotnet run -- [commands]
```

---

## 📖 Usage

### Commands

*   **Interactive Mode** (Guided UI)
    ```bash
    mediadebrid-cli
    ```
*   **Add a Magnet Link**
    ```bash
    mediadebrid-cli add "magnet:?xt=urn:btih:..." [--type movie|show] [--title "Movie Title"] [--year 2023] [--season 1] [--ep 5]
    ```
*   **Resume a Download**
    ```bash
    mediadebrid-cli resume "/path/to/partially_downloaded_file.mdebrid"
    ```
*   **Set a Configuration Value**
    ```bash
    mediadebrid-cli set <key> <value>
    ```
*   **List Configurations**
    ```bash
    mediadebrid-cli list
    ```

---

## ⚙️ Configuration

Configuration is stored in `~AppData/Roaming/MediaDebrid/config.json` (on Windows) or loaded via standard environment variables / `.env` files. You can manage these settings directly via the CLI's `set` command.

| Configuration Key | Env Variable | Default Value | Description |
| :--- | :--- | :--- | :--- |
| `real_debrid_api_key` | `REAL_DEBRID_API_TOKEN` | *empty* | **Required.** Your Real-Debrid API token. |
| `media_root` | `MEDIA_ROOT` | `~/Downloads/MediaDebrid` | Path for Movies & Shows. |
| `games_root` | `GAMES_ROOT` | `~/Downloads/MediaDebrid` | Path for Games. |
| `others_root` | `OTHERS_ROOT` | `~/Downloads/MediaDebrid` | Path for miscellaneous files. |
| `parallel_download` | `PARALLEL_DOWNLOAD_ENABLED` | `true` | Enable segmented downloads. |
| `connections_per_file` | `CONNECTIONS_PER_FILE` | `8` | Number of parallel connections per file. |
| `skip_existing_episodes`| `SKIP_EXISTING_EPISODES` | `true` | Skip downloading episodes that already exist. |

---

## 🧠 Architecture: The `.mdebrid` Custom File Format

To achieve completely self-contained, resumable parallel downloads without littering the filesystem with sidecar metadata files, `MediaDebrid-cli` implements a custom temporary file format: **`.mdebrid`**.

### How it Works

When a download begins, the application creates a file with the `.mdebrid` extension. 

1.  **Sparse Pre-allocation**: (Windows only) The file is flagged as a Sparse File (`FSCTL_SET_SPARSE`). The physical size on disk grows as data is downloaded, but the logical size is pre-allocated to the exact size of the final file **plus an extra 4096 bytes (4KB)**.
2.  **Segmented Writing**: The downloader splits the file into chunks and writes data concurrently to the correct byte offsets.
3.  **The 4KB Footer**: The extra 4KB at the very end of the file is reserved exclusively for a **Resume Metadata Footer**.

### The Footer Structure

The 4096-byte footer is structured as follows, starting from `EOF - 4096` bytes:

| Byte Offset (from EOF) | Size | Description |
| :--- | :--- | :--- |
| `-4096` | Variable | UTF-8 encoded JSON string containing `ResumeMetadata`. |
| Variable | Variable | Null byte padding (`\0`) to fill the gap between the JSON and the magic marker. |
| `-8` | 8 bytes | **Magic Marker**: The exact string `MDEBRID!`. |

#### The `ResumeMetadata` JSON
The JSON payload contains critical state information:
```json
{
  "MagnetUri": "magnet:?xt=urn:btih:...",
  "FileId": "rd_file_id_123",
  "TotalSize": 1073741824,
  "Segments": [
    { "Start": 0, "End": 268435455, "Current": 150000000 },
    { "Start": 268435456, "End": 536870911, "Current": 268435456 }, ...
  ],
  "TypeOverride": "movie",
  "EpisodeOverride": null
}
```

### The Resumption Lifecycle

1.  **Save**: Periodically (every 5MB downloaded), or when the user pauses/cancels, the application updates the JSON, clears the 4KB footer space, writes the new JSON, and appends the `MDEBRID!` magic marker to the absolute end of the file.
2.  **Read**: When the user runs the `resume` command (or the app detects an existing `.mdebrid` file), it seeks to the end of the file, reads the last 8 bytes to verify the `MDEBRID!` magic marker. If valid, it reads the preceding JSON payload to reconstruct the exact state of all parallel segments.
3.  **Finalization**: Once all segments complete successfully, the file is safely closed. The file is then truncated by exactly 4096 bytes (removing the footer entirely), the Sparse flag is removed, and the file is renamed to its final, correct extension (e.g., `.mkv`, `.iso`).

---

## 👨‍💻 Contributing

When contributing to this repository, please review the `AGENTS.md` guidelines for core architectural rules, specifically regarding handling the Spectre.Console TUI, centralized exception handling (`Models/Exceptions.cs`), and correct asynchronous patterns.
