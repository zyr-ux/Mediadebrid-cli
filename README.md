<div align="center">

![MediaDebrid Logo](Logos/full-logo-transparent.svg)

</div>

MediaDebrid is a powerful, feature-rich command-line interface (CLI) application built using **.NET 10.0** and C#. It acts as an advanced cloud-resource manager that utilizes the **Real-Debrid API** to efficiently resolve magnet links and manage high-speed downloads directly to your system. 

It features a stunning Terminal User Interface (TUI) powered by Spectre.Console, and under the hood, it handles complex parallel downloads with robust resuming capabilities.

---

## ⚖️ Legal Advisory

**IMPORTANT:** This tool is intended for the management and download of **legally acquired content** and personal files stored via the Real-Debrid service. The developers of MediaDebrid do not condone, encourage, or support the use of this software for the purpose of digital piracy or the unauthorized distribution of copyrighted material. 

By using this software, you agree that you are solely responsible for ensuring that your use of the service complies with all applicable laws and the Real-Debrid Terms of Service. The authors are not liable for any misuse of this tool.

---

## ✨ Features

- **Magnet Management**: Seamlessly processes magnet links, resolves restricted access URLs, and fetches direct download streams.
- **Rich Terminal UI**: Beautiful, responsive console interface with live progress bars, spinners, and professional logging.
- **Advanced Input Handling**: Smooth, animated input prompts with custom manual wrap-around handling for a premium CLI experience.
- **Segmented / Parallel Downloads**: Splits files into multiple chunks and downloads them concurrently for maximum network throughput.
- **Robust Resumable Downloads**: Persists download state natively within a 4KB binary footer in the file itself, ensuring zero data loss on interruption.
- **Smart File Classification**: Advanced signal-based parsing to automatically organize content into logical structures (e.g., Categorized Storage, Software, or Archives).
- **Interactive Mode**: Launch the app without arguments to enter an intuitive, guided TUI mode for resource selection.
- **Pause/Resume Support**: Gracefully pause or safely save state and exit downloads with simple keyboard shortcuts.

---

## 🚀 Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A [Real-Debrid](https://real-debrid.com/) Account & API Token.

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

## 💻 Installation

### Windows
The recommended way to install on Windows is via the [GitHub Releases](https://github.com/zyr-ux/MediaDebrid-cli/releases) page. Download and run `MediaDebrid-Setup.exe`. This will:
*   Install the app to `Program Files`.
*   Automatically add `mediadebrid` to your system **PATH**.
*   Provide an uninstaller via Control Panel.

Alternatively, once published, you can use **WinGet**:
```powershell
winget install MediaDebrid
```

### Linux & macOS
1.  Download the appropriate binary for your system (e.g., `mediadebrid-linux-x64` or `mediadebrid-osx-x64`) from the [Releases](https://github.com/zyr-ux/MediaDebrid-cli/releases) page.
2.  Make the binary executable:
    ```bash
    chmod +x mediadebrid-linux-x64
    ```
3.  (Optional) Move it to your local bin directory for global access:
    ```bash
    sudo mv mediadebrid-linux-x64 /usr/local/bin/mediadebrid
    ```

---

## 📖 Usage

### Commands

*   **Interactive Mode** (Guided UI)
    ```bash
    mediadebrid
    ```
*   **Resolve Magnet Links**
    ```bash
    mediadebrid unres [magnet_link]
    ```
*   **Resume a Download**
    ```bash
    mediadebrid resume "/path/to/partially_downloaded_file.mdebrid"
    ```
*   **Set a Configuration Value**
    ```bash
    mediadebrid set <key> <value>
    ```
*   **List Configurations**
    ```bash
    mediadebrid list
    ```

---

## ⚙️ Configuration

Configuration is stored in `~AppData/Roaming/MediaDebrid/config.json` (on Windows). You can manage these settings directly via the CLI's `set` command.

| Configuration Key | Env Variable | Default Value | Description |
| :--- | :--- | :--- | :--- |
| `real_debrid_api_key` | `REAL_DEBRID_API_TOKEN` | *empty* | **Required.** Your Real-Debrid API token. |
| `media_root` | `MEDIA_ROOT` | `~/Downloads/MediaDebrid` | Path for Video content. |
| `games_root` | `GAMES_ROOT` | `~/Downloads/MediaDebrid` | Path for Software/Games. |
| `others_root` | `OTHERS_ROOT` | `~/Downloads/MediaDebrid` | Path for miscellaneous files. |
| `parallel_download` | `PARALLEL_DOWNLOAD_ENABLED` | `true` | Enable segmented downloads. |
| `connections_per_file` | `CONNECTIONS_PER_FILE` | `8` | Number of parallel connections per file. |
| `skip_existing_episodes`| `SKIP_EXISTING_EPISODES` | `true` | Skip downloading items that already exist in the library. |

---

## 🧠 Architecture: The `.mdebrid` Custom File Format

To achieve completely self-contained, resumable parallel downloads without sidecar metadata files, `MediaDebrid` implements a custom temporary file format: **`.mdebrid`**.

### How it Works

When a download begins, the application creates a file with the `.mdebrid` extension. 

1.  **Sparse Pre-allocation**: (Windows only) The file is flagged as a Sparse File. The physical size on disk grows as data is downloaded, but the logical size is pre-allocated to the exact size of the final file **plus an extra 4096 bytes (4KB)**.
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
    { "Start": 268435456, "End": 536870911, "Current": 268435456 }
  ],
  "CategoryOverride": "1",
  "ItemOverride": "4-8"
}
```

### The Resumption Lifecycle

1.  **Save**: Periodically, or when the user pauses/cancels, the application updates the JSON metadata and appends the `MDEBRID!` magic marker to the absolute end of the file.
2.  **Read**: When resuming, it verifies the `MDEBRID!` magic marker and reads the preceding JSON payload to reconstruct the state of all parallel segments.
3.  **Finalization**: Once complete, the file is truncated by exactly 4096 bytes (removing the footer), the Sparse flag is removed, and the file is renamed to its final extension (e.g., `.zip`, `.mp4`).

---

## 👨‍💻 Contributing & Technical Info

For detailed information on the internal systems, please refer to our technical guides:
- [Technical Architecture Guide](ARCHITECTURE.md)
- [Agent Guidelines](AGENTS.md)

When contributing, please follow the established patterns for centralized exception handling and TUI management.
