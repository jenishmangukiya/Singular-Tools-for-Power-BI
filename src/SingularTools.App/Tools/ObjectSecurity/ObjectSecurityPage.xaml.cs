using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingularTools.Core;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.ObjectSecurity;

/// <summary>
/// One role in the role list. <see cref="KindLabel"/> and <see cref="Summary"/>
/// are recalculated from the live staged state via <see cref="Refresh"/>, so a role
/// that gains OLS rules flips to "OLS + RLS" immediately rather than only after a
/// reload.
/// </summary>
public sealed class RoleItemViewModel : INotifyPropertyChanged
{
    public RoleItemViewModel(OlsRole model)
    {
        Model = model;

        // Seed the labels immediately: the list renders every role, and only the
        // selected one would otherwise be refreshed, leaving the rest blank.
        KindLabel = model.KindLabel;
        Summary = BuildSummary(model);
    }

    public OlsRole Model { get; }
    public string Name => Model.Name;

    public string KindLabel { get; private set; }
    public string Summary { get; private set; }

    /// <summary>Recomputes the labels from the model and any staged state.</summary>
    public void Refresh(OlsRole role)
    {
        KindLabel = role.KindLabel;
        Summary = BuildSummary(role);
        Raise(nameof(KindLabel));
        Raise(nameof(Summary));
    }

    private static string BuildSummary(OlsRole role)
    {
        if (role.IsOls)
        {
            return $"{role.HiddenTableCount} table(s) \u00B7 {role.HiddenColumnCount} column(s) hidden";
        }

        if (role.IsRls) return "Row-level filters only";
        return "No OLS rules";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A table card with its hide toggle and columns.</summary>
public sealed class TableNodeViewModel : INotifyPropertyChanged
{
    private bool _isHidden;

    public TableNodeViewModel(string tableName) => TableName = tableName;

    public string TableName { get; }
    public ObservableCollection<ColumnNodeViewModel> Columns { get; } = new();

    public bool OriginalHidden { get; private set; }

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            Raise(nameof(IsHidden));
            Raise(nameof(HiddenSummary));
            Raise(nameof(StateBadge));
            Raise(nameof(StateBadgeVisibility));
            foreach (var column in Columns) column.NotifyTableChanged();
        }
    }

    private bool _isToggleEnabled = true;
    public bool IsToggleEnabled
    {
        get => _isToggleEnabled;
        set
        {
            if (_isToggleEnabled == value) return;
            _isToggleEnabled = value;
            Raise(nameof(IsToggleEnabled));
        }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            Raise(nameof(IsVisible));
        }
    }

    public string HiddenSummary
    {
        get
        {
            if (_isHidden) return "Table hidden";
            var hidden = Columns.Count(c => c.IsHidden);
            return hidden > 0 ? $"{hidden} column(s) hidden" : string.Empty;
        }
    }

    public string StateBadge => _isHidden ? "Hidden" : string.Empty;
    public Visibility StateBadgeVisibility => _isHidden ? Visibility.Visible : Visibility.Collapsed;

    public void SetInitial(bool hidden)
    {
        OriginalHidden = hidden;
        _isHidden = hidden;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One column row with a hide toggle.</summary>
public sealed class ColumnNodeViewModel : INotifyPropertyChanged
{
    private readonly TableNodeViewModel _table;
    private bool _isHidden;

    public ColumnNodeViewModel(string tableName, string columnName, TableNodeViewModel table)
    {
        TableName = tableName;
        ColumnName = columnName;
        _table = table;
    }

    public string TableName { get; }
    public string ColumnName { get; }
    public bool OriginalHidden { get; private set; }

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            Raise(nameof(IsHidden));
            Raise(nameof(StateBadge));
            Raise(nameof(StateBadgeVisibility));
            _table.Raise(nameof(TableNodeViewModel.HiddenSummary));
        }
    }

    public bool IsToggleEnabled => _table.IsToggleEnabled && !_table.IsHidden;

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            Raise(nameof(IsVisible));
        }
    }

    public string StateBadge => _table.IsHidden ? "Implied" : _isHidden ? "Hidden" : string.Empty;
    public Visibility StateBadgeVisibility => string.IsNullOrEmpty(StateBadge) ? Visibility.Collapsed : Visibility.Visible;

    public void SetInitial(bool hidden)
    {
        OriginalHidden = hidden;
        _isHidden = hidden;
    }

    internal void NotifyTableChanged()
    {
        Raise(nameof(IsToggleEnabled));
        Raise(nameof(StateBadge));
        Raise(nameof(StateBadgeVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One validation message shown in the notes expander.</summary>
public sealed class IssueViewModel
{
    public IssueViewModel(OlsValidationIssue issue)
    {
        Glyph = issue.Glyph;
        Message = issue.RoleName.Length > 0 ? $"{issue.RoleName}: {issue.Message}" : issue.Message;
    }

    public string Glyph { get; }
    public string Message { get; }
}

public sealed partial class ObjectSecurityPage : Page, IToolPage
{
    public string ToolId => "object-security";
    public string Title => "Object Security";
    public string Description => "Manage object-level security (OLS) roles for tables and columns";
    public string Glyph => "\uE72E";

    public ObservableCollection<RoleItemViewModel> Roles { get; } = new();
    public ObservableCollection<TableNodeViewModel> Tables { get; } = new();
    public ObservableCollection<IssueViewModel> Issues { get; } = new();

    private string? _manualAnchor;
    private string _projectRoot = string.Empty;
    private string _modelFolder = string.Empty;
    private string _selectedRoleName = string.Empty;
    private OlsModel? _model;
    private Action? _emptyAction;
    private bool _subscribed;
    private bool _suppressSelection;
    private bool _suppressRefresh;

    public ObjectSecurityPage()
    {
        InitializeComponent();
        Loaded += ObjectSecurityPage_Loaded;
        Unloaded += ObjectSecurityPage_Unloaded;
    }

    // ---- Lifecycle --------------------------------------------------------

    public void OnActivated() => RefreshAll();

    private void ObjectSecurityPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Subscribe();
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"ObjectSecurityPage load failed: {ex}");
        }
    }

    private void ObjectSecurityPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        App.Workspace.Changed -= Workspace_Changed;
        App.Workspace.ModelChanged -= Workspace_ModelChanged;
        _subscribed = false;
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Workspace.Changed += Workspace_Changed;
        App.Workspace.ModelChanged += Workspace_ModelChanged;
        _subscribed = true;
    }

    private void Workspace_Changed(object? sender, EventArgs e)
    {
        _manualAnchor = null;
        RefreshAll();
    }

    private void Workspace_ModelChanged(object? sender, EventArgs e) => RefreshAll();

    // ---- Refresh ----------------------------------------------------------

    private void RefreshAll()
    {
        var anchor = ResolveAnchor();
        _projectRoot = ResolveProjectRoot(anchor);
        _modelFolder = OlsService.DiscoverModelFolder(anchor) ?? string.Empty;
        _model = _modelFolder.Length > 0 ? OlsService.LoadModel(_modelFolder) : null;

        UpdateHeader();

        if (_model == null)
        {
            ClearAll();
            ShowEmpty(
                "\uE72E",
                "No semantic model found",
                "Object Security edits the roles of a PBIP semantic model. Open a .pbip project, or choose the folder that holds it.",
                "Choose project folder\u2026",
                () => ChooseFolder());
            return;
        }

        if (!_model.HasTmdlDefinition)
        {
            ClearAll();
            ShowEmpty(
                "\uE783",
                "This model uses model.bim",
                "Object Security edits TMDL models (definition/roles/*.tmdl). The model at this path is stored as a single model.bim file, which is left untouched.");
            return;
        }

        PopulateRoles();

        if (_model.Roles.Count == 0)
        {
            ClearTables();
            ShowEmpty(
                "\uE72E",
                "No roles yet",
                "OLS rules live inside a security role. Create one to start hiding tables and columns, then apply.",
                "Create OLS role\u2026",
                () => NewRoleButton_Click(this, new RoutedEventArgs()));
            return;
        }

        var target = _model.FindRole(_selectedRoleName) ?? _model.Roles[0];
        SelectRole(target);
    }

    private string ResolveAnchor()
    {
        if (_manualAnchor != null) return _manualAnchor;
        return App.Workspace.HasReport ? App.Workspace.ReportPath : string.Empty;
    }

    private static string ResolveProjectRoot(string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return string.Empty;

        if (anchor.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(anchor)?.FullName;
            if (!string.IsNullOrEmpty(parent)) return parent;
        }

        return ReportConfigStore.ResolveProjectRoot(anchor);
    }

    private void PopulateRoles()
    {
        _suppressSelection = true;
        Roles.Clear();
        foreach (var role in _model!.Roles.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            Roles.Add(new RoleItemViewModel(role));
        }
        _suppressSelection = false;
    }

    private void SelectRole(OlsRole role)
    {
        _selectedRoleName = role.Name;

        _suppressSelection = true;
        RolesList.SelectedItem = Roles.FirstOrDefault(r =>
            string.Equals(r.Name, role.Name, StringComparison.OrdinalIgnoreCase));
        _suppressSelection = false;

        BuildTables(role);
    }

    private void BuildTables(OlsRole role)
    {
        _suppressRefresh = true;
        Tables.Clear();

        var byTable = role.TablePermissions
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var modelTable in _model!.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var node = new TableNodeViewModel(modelTable.Name);
            byTable.TryGetValue(modelTable.Name, out var rule);
            node.SetInitial(rule?.Permission == OlsPermission.None);

            foreach (var column in modelTable.Columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var columnNode = new ColumnNodeViewModel(modelTable.Name, column.Name, node);
                var columnRule = rule?.Columns.FirstOrDefault(c =>
                    string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                columnNode.SetInitial(columnRule?.Permission == OlsPermission.None);

                columnNode.PropertyChanged += Vm_PropertyChanged;
                node.Columns.Add(columnNode);
            }

            node.PropertyChanged += Vm_PropertyChanged;
            Tables.Add(node);
        }

        _suppressRefresh = false;

        RoleWarning.Visibility = role.IsRls ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
        HideEmpty();
        TablesScroll.Visibility = Visibility.Visible;
        RefreshPlan();
    }

    private void ClearAll()
    {
        ClearTables();
        _suppressSelection = true;
        Roles.Clear();
        _suppressSelection = false;
        _selectedRoleName = string.Empty;
        RoleWarning.Visibility = Visibility.Collapsed;
    }

    private void ClearTables()
    {
        _suppressRefresh = true;
        Tables.Clear();
        Issues.Clear();
        _suppressRefresh = false;
        TablesScroll.Visibility = Visibility.Collapsed;
        IssuesExpander.Visibility = Visibility.Collapsed;
        RoleWarning.Visibility = Visibility.Collapsed;
    }

    private void UpdateHeader()
    {
        if (_model == null)
        {
            ModelNameText.Text = "No semantic model found";
            ModelHintText.Text = string.IsNullOrEmpty(_projectRoot)
                ? "Open a Power BI project (.pbip) to manage its roles."
                : $"No .SemanticModel folder next to {_projectRoot}.";
            PowerBiWarning.Visibility = Visibility.Collapsed;
            return;
        }

        ModelNameText.Text = _model.Name;
        ModelHintText.Text = _model.HasTmdlDefinition
            ? $"{_model.Roles.Count} role(s) \u00B7 {_model.Tables.Count} tables \u00B7 {_model.FolderPath}"
            : _model.FolderPath;
        PowerBiWarning.Visibility = _model.HasTmdlDefinition ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRefresh) return;
        if (e.PropertyName == nameof(TableNodeViewModel.IsHidden) ||
            e.PropertyName == nameof(ColumnNodeViewModel.IsHidden))
        {
            RefreshPlan();
        }
    }

    private void RolesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (RolesList.SelectedItem is not RoleItemViewModel item) return;

        _selectedRoleName = item.Name;
        _suppressRefresh = true;
        BuildTables(item.Model);
    }

    // ---- Staging / validation --------------------------------------------

    private OlsRole? SelectedRole => (RolesList.SelectedItem as RoleItemViewModel)?.Model;

    private List<OlsChange> ComputeChanges()
    {
        var changes = new List<OlsChange>();
        var role = SelectedRole;
        if (role == null) return changes;

        foreach (var table in Tables)
        {
            if (table.IsHidden != table.OriginalHidden)
            {
                changes.Add(new OlsChange
                {
                    RoleName = role.Name,
                    Kind = OlsObjectKind.Table,
                    TableName = table.TableName,
                    // Revealing an existing rule writes an explicit "read" (not
                    // Default) so an applied "none" is genuinely turned into "read".
                    Desired = table.IsHidden ? OlsPermission.None : OlsPermission.Read
                });
            }

            if (table.IsHidden) continue;

            foreach (var column in table.Columns)
            {
                if (column.IsHidden != column.OriginalHidden)
                {
                    changes.Add(new OlsChange
                    {
                        RoleName = role.Name,
                        Kind = OlsObjectKind.Column,
                        TableName = table.TableName,
                        ColumnName = column.ColumnName,
                        Desired = column.IsHidden ? OlsPermission.None : OlsPermission.Read
                    });
                }
            }
        }

        return changes;
    }

    private void RefreshPlan()
    {
        var role = SelectedRole;
        var changes = ComputeChanges();

        // Keep the role's badge/summary in step with what is staged, so adding OLS
        // to an RLS role shows "OLS + RLS" before Apply, without a reload.
        if (role != null)
        {
            UpdateRoleBadge(role, changes);
        }

        var issues = new List<OlsValidationIssue>();
        if (role != null && _model != null)
        {
            issues.AddRange(OlsService.Validate(_model, changes));

            if (!role.IsOls && role.TablePermissions.Count == 0)
            {
                issues.Add(new OlsValidationIssue
                {
                    Severity = OlsValidationSeverity.Info,
                    RoleName = role.Name,
                    Message = "This role has no OLS rules, so it grants full read access to the model."
                });
            }
        }

        Issues.Clear();
        foreach (var issue in issues) Issues.Add(new IssueViewModel(issue));

        IssuesExpander.Visibility = Issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IssuesExpander.Header = $"{Issues.Count} note(s)";

        var blocked = issues.Any(i => i.BlocksApply);
        ApplyButton.IsEnabled = role != null && changes.Count > 0 && !blocked;
        ApplyButtonText.Text = changes.Count > 0 ? $"Apply changes ({changes.Count})" : "Apply changes";
        UndoButton.IsEnabled = HasBackup();

        NewRoleButton.IsEnabled = _model != null && _model.HasTmdlDefinition;
        RenameRoleButton.IsEnabled = role != null;
        DeleteRoleButton.IsEnabled = role != null;

        if (changes.Count == 0)
        {
            PlanSummaryText.Text = string.Empty;
        }
        else
        {
            var hide = changes.Count(c => c.Desired == OlsPermission.None);
            var show = changes.Count - hide;
            PlanSummaryText.Text = $"{hide} to hide \u00B7 {show} to show";
        }
    }

    private bool HasBackup()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return false;
        return OlsService.LatestBackupFolder(_projectRoot) != null;
    }

    /// <summary>
    /// Refreshes a role's list label from its applied state plus the staged changes,
    /// so the badge tracks edits live.
    /// </summary>
    private void UpdateRoleBadge(OlsRole role, IReadOnlyList<OlsChange> changes)
    {
        var item = Roles.FirstOrDefault(r => string.Equals(r.Name, role.Name, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;

        item.Refresh(OlsService.PreviewRole(role, changes));
    }

    // ---- Search -----------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;

        foreach (var table in Tables)
        {
            if (query.Length == 0)
            {
                table.IsVisible = true;
                foreach (var column in table.Columns) column.IsVisible = true;
                continue;
            }

            var tableMatch = table.TableName.Contains(query, StringComparison.OrdinalIgnoreCase);
            var anyColumnMatch = false;

            foreach (var column in table.Columns)
            {
                var match = tableMatch || column.ColumnName.Contains(query, StringComparison.OrdinalIgnoreCase);
                column.IsVisible = match;
                anyColumnMatch |= match;
            }

            table.IsVisible = tableMatch || anyColumnMatch;
        }
    }

    // ---- Commands ---------------------------------------------------------

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var role = SelectedRole;
        if (_model == null || role == null) return;

        var changes = ComputeChanges();
        if (changes.Count == 0)
        {
            ToastService.Show("Nothing to apply — no staged changes.", ToastSeverity.Informational);
            return;
        }

        var issues = OlsService.Validate(_model, changes);
        var blocking = issues.FirstOrDefault(i => i.BlocksApply);
        if (blocking != null)
        {
            ToastService.Show(blocking.Message, ToastSeverity.Error);
            return;
        }

        var warnings = issues.Count(i => i.Severity == OlsValidationSeverity.Warning);

        var message = $"This writes {changes.Count} change(s) directly to the role files in {_model.FolderPath}. " +
                      "If the report is open in Power BI Desktop, close it first or your edits may be overwritten.";
        if (warnings > 0) message += $"\n\n{warnings} warning(s) were found — review the notes list.";

        if (!await ConfirmAsync("Apply object security?", message, "Apply")) return;

        try
        {
            var result = OlsService.Apply(_model, changes, createBackup: true, projectRoot: _projectRoot);
            App.Workspace.NotifyModelFilesChanged();
            var powerBiRunning = await Task.Run(() => PowerBiPublisher.TryApplyExternalChangesInPowerBi());

            if (result.Changes == 0)
            {
                ToastService.Show("Nothing changed — the role already matched.", ToastSeverity.Informational);
            }
            else if (powerBiRunning)
            {
                ToastService.Show(
                    $"Applied {result.Changes} OLS change(s) across {result.FilesWritten} file(s) and applied the external change in Power BI.",
                    ToastSeverity.Success);
            }
            else
            {
                ToastService.Show(
                    $"Applied {result.Changes} OLS change(s) across {result.FilesWritten} file(s). Reload the model in Power BI to pick it up.",
                    ToastSeverity.Success);
            }

            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"OLS apply failed: {ex}");
            ToastService.Show($"Could not apply: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_projectRoot)) return;

        if (!await ConfirmAsync(
                "Undo last apply?",
                "The role files changed by the last Object Security apply will be restored from the backup. (Sort by Column edits are kept separate.)",
                "Restore"))
        {
            return;
        }

        try
        {
            var restored = OlsService.RestoreLatestBackup(_projectRoot, out _);
            App.Workspace.NotifyModelFilesChanged();
            var powerBiRunning = false;
            if (restored > 0)
            {
                powerBiRunning = await Task.Run(() => PowerBiPublisher.TryApplyExternalChangesInPowerBi());
            }

            if (restored <= 0)
            {
                ToastService.Show("No backup to restore.", ToastSeverity.Informational);
            }
            else if (powerBiRunning)
            {
                ToastService.Show($"Restored {restored} role file(s) and applied the external change in Power BI.", ToastSeverity.Success);
            }
            else
            {
                ToastService.Show($"Restored {restored} role file(s). Reload the model in Power BI to pick it up.", ToastSeverity.Success);
            }

            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"OLS restore failed: {ex}");
            ToastService.Show($"Could not restore: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void NewRoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;

        var name = await PromptAsync(
            "New OLS role",
            "Name the role. Leave DAX filters to Power BI — OLS roles are driven by the rules you set here.",
            "Restricted");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            OlsService.CreateRole(_model, name, createBackup: true, projectRoot: _projectRoot);
            App.Workspace.NotifyModelFilesChanged();
            _selectedRoleName = name.Trim();
            ToastService.Show($"Created role '{name.Trim()}'. Add rules, then Apply.", ToastSeverity.Success);
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"OLS create role failed: {ex}");
            ToastService.Show($"Could not create role: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void RenameRoleButton_Click(object sender, RoutedEventArgs e)
    {
        var role = SelectedRole;
        if (_model == null || role == null) return;

        var name = await PromptAsync("Rename role", "Rename the selected security role.", role.Name);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), role.Name, StringComparison.Ordinal)) return;

        try
        {
            OlsService.RenameRole(_model, role.Name, name, createBackup: true, projectRoot: _projectRoot);
            App.Workspace.NotifyModelFilesChanged();
            _selectedRoleName = name.Trim();
            ToastService.Show($"Renamed role to '{name.Trim()}'.", ToastSeverity.Success);
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"OLS rename role failed: {ex}");
            ToastService.Show($"Could not rename role: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void DeleteRoleButton_Click(object sender, RoutedEventArgs e)
    {
        var role = SelectedRole;
        if (_model == null || role == null) return;

        if (!await ConfirmAsync(
                $"Delete role '{role.Name}'?",
                "The role file is removed and its reference is taken out of model.tmdl. You can undo this from Undo last apply.",
                "Delete"))
        {
            return;
        }

        try
        {
            OlsService.DeleteRole(_model, role.Name, createBackup: true, projectRoot: _projectRoot);
            App.Workspace.NotifyModelFilesChanged();
            _selectedRoleName = string.Empty;
            ToastService.Show($"Deleted role '{role.Name}'.", ToastSeverity.Success);
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"OLS delete role failed: {ex}");
            ToastService.Show($"Could not delete role: {ex.Message}", ToastSeverity.Error);
        }
    }

    private void EmptyActionButton_Click(object sender, RoutedEventArgs e) => _emptyAction?.Invoke();

    private async void ChooseFolder()
    {
        try
        {
            var path = await ReportPicker.PickReportFolderAsync();
            if (string.IsNullOrEmpty(path)) return;

            _manualAnchor = path;
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"Object security folder pick failed: {ex}");
        }
    }

    // ---- Empty state ------------------------------------------------------

    private void ShowEmpty(string glyph, string title, string message, string? actionText = null, Action? action = null)
    {
        EmptyIcon.Glyph = glyph;
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;

        var hasAction = actionText != null && action != null;
        EmptyActionButton.Content = actionText ?? string.Empty;
        EmptyActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        _emptyAction = hasAction ? action : null;

        EmptyPanel.Visibility = Visibility.Visible;
        RefreshPlan();
    }

    private void HideEmpty() => EmptyPanel.Visibility = Visibility.Collapsed;

    // ---- Dialogs ----------------------------------------------------------

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptAsync(string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = initial.Length, MaxLength = 120 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }
}
