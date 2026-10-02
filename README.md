# Singular Tools for Power BI ⚡

**Singular Tools** is a modern **toolbox** for Power BI Desktop authors built with **WinUI 3 (Windows App SDK)** and **.NET 10**. It opens on a Home launcher and hosts a growing set of tools — starting with the Pages Manager.

---

## 🚀 Key Features

- **🏠 Home Launcher**:
  - The app opens on a Home page with cards for every available tool, so future tools slot in automatically.

- **🔄 Fresh-data startup handshake**:
  - On launch, Singular Tools saves the report open in Power BI Desktop (clicking its Save button) and waits until the files have settled before showing the tools, so they never display a stale report.
  - A brief spinner covers the sync; if Power BI Desktop isn't running, the app skips it and opens immediately.
  - Later focus changes keep the report in sync automatically (save on focus, external-change apply after edits).

- **🔍 Instant Page Search**:
  - Type a page name to get a suggestion dropdown of matching pages.
  - Picking a suggestion sets it as the active page (and jumps the open report when **Go to page** is enabled).

- **🧭 Go to Page in Open Report**:
  - Toggle **Go to page in Open Report** on, then select any page in the list to jump the currently open Power BI Desktop report straight to that page.
  - Uses UI Automation against the running Power BI Desktop window, so no report reload is required.

- **🗂️ Interactive Page Reordering**:
  - `Alt + Up` / `Alt + Down`: Move selected page up or down in the report tab bar.
  - `Ctrl + Shift + Up` / `Ctrl + Shift + Down`: Jump page directly to the beginning or end.

- **↩️ Undo / Redo**:
  - Full undo and redo (`Ctrl + Z` / `Ctrl + Y`) for reorder, sort, rename, hide, set-active, duplicate and delete.

- **✏️ Inline Rename & Visibility**:
  - Rename a page in place (`F2`) and toggle hidden/visible, applied directly to the report files.

- **✨ Smart Sorting Modes**:
  - **Sort A-Z**: Alphabetical order.
  - **Sort Z-A**: Reverse alphabetical order.
  - **Natural Numeric Sort**: Smart sorting for numbered pages (`Page 1`, `Page 2`, `Page 10`).
  - **Reverse**: Invert current tab order.

- **🔄 Native PBIP / PBIR Support**:
  - Direct read/write to Power BI Enhanced Report format (`definition/pages/pages.json` & `page.json`).
  - Atomic file writes with immediate reload support in Power BI Desktop.

- **📤 Multi-Workspace Publish**:
  - Publish the open report to several Power BI workspaces at once from a single checklist.
  - Detected workspaces are cached per machine, while your ticked destinations are remembered with the report in `singular-tools.json`, alongside your publishing groups and color-sync rules.

- **📤 Publishing Groups**:
  - Save a named page recipe per audience, e.g. *Client A – External*, and choose which pages stay visible for it.
  - Publish the group to any workspace: pick a destination from the detected list, or type one by hand.
  - Page visibility is applied just for the publish and restored afterwards, so the report you are editing never changes.
  - Groups travel with the report, stored in `singular-tools.json` alongside your color-sync rules.
  - Note: hiding a page removes it from the page list — it is **not** access control.

- **🔐 Object Security (OLS)**:
  - Manage object-level security from a role list: hide an entire table, or just the sensitive columns, from a role.
  - Works alongside row-level security — OLS rules are stored in whichever role you pick, including one that already has RLS filters.
  - Changes are staged and reviewed before being written to the model's role TMDL, and can be undone.
  - Validates relationship-chain breaks and flags stale or redundant rules.

- **🎨 Modern Windows 11 Fluent UI**:
  - Built with **WinUI 3** and Windows App SDK.
  - Native **Mica Backdrop**, dark & light theme auto-detection, rounded corners, and fluid typography.

- **🔌 Power BI Desktop External Tool Integration**:
  - Installs directly into Power BI Desktop's **External Tools** ribbon tab via `SingularPowerTools.pbitool.json`.

---

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Action | Scope |
| :--- | :--- | :--- |
| `Up` / `Down` | Navigate through page list | Pages Manager |
| `Enter` | Set active page | Pages Manager |
| `F2` | Rename selected page | Pages Manager |
| `Alt + Up` / `Alt + Down` | Move selected page up / down | Pages Manager |
| `Ctrl + Shift + Up` | Move page to top (first tab) | Pages Manager |
| `Ctrl + Shift + Down` | Move page to bottom (last tab) | Pages Manager |
| `Ctrl + Z` / `Ctrl + Y` | Undo / redo | Pages Manager |
| `Ctrl + D` | Duplicate selected page | Pages Manager |
| `Delete` | Delete selected page | Pages Manager |
| `Esc` | Clear search | Pages Manager |


---

## 🛠️ Project Structure

```
├── distribution/
│   ├── SingularPowerTools.pbitool.json  # External tool ribbon manifest
│   └── register-external-tool.ps1       # One-click install & registration script
├── src/
│   ├── SingularTools.Core/              # PBIP parser, models, edit history & window detector
│   │   ├── Models.cs
│   │   ├── ReportManager.cs
│   │   ├── ReportEditHistory.cs
│   │   ├── SortByColumnService.cs       # TMDL column sort-order edits
│   │   ├── OlsService.cs                # TMDL object-level security roles
│   │   ├── ModelBackupStore.cs          # Project-keyed model snapshots (undo)
│   │   ├── PowerBiDetector.cs
│   │   └── ScreenCaptureService.cs
│   └── SingularTools.App/               # WinUI 3 modern Fluent UI application
│       ├── MainWindow.xaml (.cs)        # Tool shell (NavigationView rail)
│       ├── Shell/                       # IToolPage contract, ToolRegistry, window sizing
│       ├── Styles/Tokens.xaml           # Shared spacing/typography/surface resources
│       └── Tools/                       # One folder per tool
│           ├── Home/HomePage.xaml (.cs) # Home launcher
│           ├── ReportPagesManager/ReportPagesManagerPage.xaml (.cs)
│           ├── PublishingGroups/PublishingGroupsPage.xaml (.cs)  # Per-workspace page recipes
│           ├── ObjectSecurity/ObjectSecurityPage.xaml (.cs)      # OLS roles
│           └── SemanticColorManager/SemanticColorManagerPage.xaml (.cs)
└── tests/
    └── SingularTools.Tests/             # Unit tests verifying PBIP reading, sorting, history
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
3. Power BI Desktop will display **Singular Tools** in the ribbon under **External Tools** on next launch!
