# Singular Power Tools for Power BI ⚡

**Singular Power Tools** is a modern, keyboard-first **Command Palette** and **Page Organizer** for Power BI Desktop authors built with **WinUI 3 (Windows App SDK)** and **.NET 10**.

Think of it as **PowerToys Run / Raycast / VS Code Command Palette for Power BI**.

---

## 🚀 Key Features

- **⚡ Power BI Context-Scoped Shortcut (`Ctrl + Alt + P`)**:
  - Activates *only* when Power BI Desktop is your active foreground window.
  - Automatically centers itself right over your active Power BI report canvas.
  - Leaves your keys unaffected when working in other applications (browser, IDE, Excel).

- **🔍 Instant Fuzzy Page Search & Navigation**:
  - Type to filter across report pages in real time.
  - Hit `Enter` to switch and activate the selected page.

- **🗂️ Interactive Page Reordering**:
  - `Alt + Up` / `Alt + Down`: Move selected page up or down in the report tab bar.
  - `Ctrl + Shift + Up` / `Ctrl + Shift + Down`: Jump page directly to the beginning or end.

- **✨ Smart Sorting Modes**:
  - **Sort A-Z**: Alphabetical order.
  - **Sort Z-A**: Reverse alphabetical order.
  - **Natural Numeric Sort**: Smart sorting for numbered pages (`Page 1`, `Page 2`, `Page 10`).
  - **Reverse**: Invert current tab order.

- **💻 Command Palette Mode (`>` token)**:
  - Type `>` in the search box to trigger actions directly:
    - `>sort az`
    - `>sort za`
    - `>sort natural`
    - `>sort reverse`
    - `>save`
    - `>reload`

- **🔄 Native PBIP / PBIR Support**:
  - Direct read/write to Power BI Enhanced Report format (`definition/pages/pages.json` & `page.json`).
  - Atomic file writes with immediate reload support in Power BI Desktop.

- **🎨 Modern Windows 11 Fluent UI**:
  - Built with **WinUI 3** and Windows App SDK.
  - Native **Mica Backdrop**, dark & light theme auto-detection, rounded corners, and fluid typography.

- **🔌 Power BI Desktop External Tool Integration**:
  - Installs directly into Power BI Desktop's **External Tools** ribbon tab via `SingularPowerTools.pbitool.json`.

---

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Action | Scope |
| :--- | :--- | :--- |
| `Ctrl + Alt + P` | Open / Center Command Palette | Power BI Desktop window |
| `Up` / `Down` | Navigate through page list | Command Palette |
| `Enter` | Set active page / execute command | Command Palette |
| `Alt + Up` / `Alt + Down` | Move selected page up / down | Command Palette |
| `Ctrl + Shift + Up` | Move page to top (first tab) | Command Palette |
| `Ctrl + Shift + Down` | Move page to bottom (last tab) | Command Palette |
| `Esc` | Clear search / dismiss palette | Command Palette |

---

## 🛠️ Project Structure

```
├── distribution/
│   ├── SingularPowerTools.pbitool.json  # External tool ribbon manifest
│   └── register-external-tool.ps1       # One-click install & registration script
├── src/
│   ├── SingularTools.Core/              # PBIP parser, models, hotkey & window detector
│   │   ├── Models.cs
│   │   ├── ReportManager.cs
│   │   ├── PowerBiDetector.cs
│   │   └── HotkeyManager.cs
│   └── SingularTools.App/               # WinUI 3 modern Fluent UI application
│       ├── MainWindow.xaml (.cs)
│       └── MainPage.xaml (.cs)
└── tests/
    └── SingularTools.Tests/             # Unit tests verifying PBIP reading & sorting
        └── ReportManagerTests.cs
```

---

## 📦 Getting Started

### 1. Build & Run Tests
```powershell
dotnet test
```

### 2. Register as External Tool in Power BI Desktop
Run the registration script from PowerShell:
```powershell
pwsh -ExecutionPolicy Bypass -File "distribution/register-external-tool.ps1"
```

This will:
1. Publish the Release build to `%LOCALAPPDATA%\SingularPowerTools\`.
2. Place `SingularPowerTools.pbitool.json` in Power BI Desktop's External Tools registry.
3. Power BI Desktop will display **Singular Power Tools** in the ribbon under **External Tools** on next launch!
