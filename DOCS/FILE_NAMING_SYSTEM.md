# File Naming & Organization System

This document explains how the automated file naming system works in QMUSICSLSK and how you can customize it to fit your library's needs.

## Basics

The system uses a **Template Expression** to determine where files are saved and what they are named. You can change this in **Settings > Download Settings > File Name Format**.

### Variables
Placeholders inside `{ }` are replaced with metadata from your library (DB) or the search result.

| Placeholder | Description | Example Value |
| :--- | :--- | :--- |
| `{artist}` | The track artist | Daft Punk |
| `{title}` | The track title | One More Time |
| `{album}` | The album name | Discovery |
| `{filename}` | The original uploader's filename | daft-punk-1moretime-vbr |
| `{bitrate}` | Audio quality in kbps | 320 |
| `{bpm}` | Beats per minute | 128 |
| `{key}` | Musical key (Camelot) | 8A |
| `{energy}` | Energy level (0-100) | 85 |
| `{format}` | File extension | mp3 |
| `{user}` | Soulseek username | slsk_user_1 |

---

## Power User Features

### 1. Fallback Logic (`|`)
If a variable is missing in the database, you can provide an alternative using the pipe symbol.
*   **Template:** `{artist|filename} - {title}`
*   **Result:** Use the artist if available; otherwise, use the original uploader's filename.

### 2. Conditional Literals (`( )`)
Tie spacers or symbols to a variable. If the variable is empty, everything inside the parentheses is also removed.
*   **Template:** `{artist( - )}{title}`
*   **With Artist:** `Daft Punk - One More Time`
*   **Without Artist:** `One More Time` (The dash and spaces are removed to avoid messy names).

### 3. Folder Organization (`/` or `\`)
You can organize files into folders by adding slashes to your template.
*   **Template:** `{artist}/{album}/{title}`
*   **Result:** `Daft Punk/Discovery/One More Time.mp3`
*   **Missing Media:** If the album is missing, the system "squashes" the path to avoid empty folders: `Daft Punk/One More Time.mp3`.

---

## Default Behavior

By default, the system uses `{artist} - {title}`. 

**Important:** If the system cannot generate a meaningful name because of missing metadata, it will **always fallback to the uploader's original filename** as a safety measure. This ensures you never lose track of what a file is.

## Recommendations for DJs
For a Rekordbox-ready library, we recommend:
`{artist} - {title} ({bpm}bpm - {key})`
