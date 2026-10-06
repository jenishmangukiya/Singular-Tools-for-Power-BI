using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingularTools.Core;
using SingularTools.Core.Models;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.FieldRepair;

/// <summary>One replacement a missing field could be repointed at.</summary>
public sealed class FieldCandidate
{
    public FieldCandidate(string entity, string property, bool isMeasure)
    {
        Entity = entity;
        Property = property;
        IsMeasure = isMeasure;
    }

    public string Entity { get; }

    public string Property { get; }

    public bool IsMeasure { get; }

    /// <summary>"financials · SalesSegment", with measures marked so the choice is unambiguous.</summary>
    public string Label => IsMeasure ? $"{Entity} · {Property}  (measure)" : $"{Entity} · {Property}";

    public string Key => (Entity + "." + Property).TrimStart('.');
}

/// <summary>
/// One visual's uses of the selected field, collapsed so repeats read as a count rather than
/// as a wall of identical rows.
/// </summary>
public sealed class BrokenUsageItem
{
    public BrokenUsageItem(BrokenVisualUsageGroup group)
    {
        Title = group.Title;
        Subtitle = group.Subtitle;
        LocationSummary = group.LocationSummary;
        LocationVisibility = group.ShowLocations ? Visibility.Visible : Visibility.Collapsed;
        Glyph = group.Scope == BrokenUsageScope.Visual ? "\uE7C3" : "\uE71C";
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>"Axis · Category  ·  Colour rules ×8".</summary>
    public string LocationSummary { get; }

    /// <summary>Collapsed for a filter row, where the heading already names the place.</summary>
    public Visibility LocationVisibility { get; }

    public string Glyph { get; }
}

/// <summary>
/// One distinct missing field, with the replacement the author picked and whether it takes
/// part in the next repair.
/// </summary>
public sealed class BrokenFieldItem : INotifyPropertyChanged
{
    private FieldCandidate? _selectedCandidate;

    public BrokenFieldItem(BrokenField field, IReadOnlyList<FieldCandidate> candidates, FieldCandidate? initial)
    {
        Model = field;
        Candidates = new ObservableCollection<FieldCandidate>(candidates);
        _selectedCandidate = initial;

        Usages = new ObservableCollection<BrokenUsageItem>(field.UsagesByVisual.Select(u => new BrokenUsageItem(u)));
    }

    public BrokenField Model { get; }

    /// <summary>"financials.Segment", the field the report still points at.</summary>
    public string Label => Model.Key;

    public ObservableCollection<FieldCandidate> Candidates { get; }

    public ObservableCollection<BrokenUsageItem> Usages { get; }

    public string SeverityLabel => Model.Severity switch
    {
        BrokenFieldSeverity.MissingTable => "table missing",
        _ => Model.ExpectedKind == SemanticFieldKind.Measure ? "measure missing" : "column missing"
    };

    /// <summary>
    /// How far the damage reaches, naming each kind of place rather than a single number, so a
    /// filter-only break is visibly different from a broken axis.
    /// </summary>
    public string ImpactLabel
    {
        get
        {
            var parts = new List<string>();

            if (Model.VisualCount > 0) parts.Add(Plural(Model.VisualCount, "visual"));
            if (Model.FilteredPageCount > 0) parts.Add(Plural(Model.FilteredPageCount, "page filter"));

            // A report filter applies to every page, so it is named separately rather than
            // being folded into the page count.
            if (Model.HasReportFilter) parts.Add("report filter");

            return parts.Count == 0 ? "not referenced" : string.Join(" · ", parts);
        }
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>True when a replacement can be inferred without the author choosing.</summary>
    public bool IsEasyMatch => Model.IsCosmetic;

    /// <summary>The replacement currently staged for this field.</summary>
    public FieldCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (ReferenceEquals(_selectedCandidate, value)) return;
            _selectedCandidate = value;
            Raise(nameof(SelectedCandidate));
            Raise(nameof(IsSelected));
        }
    }

    /// <summary>
    /// Whether this field takes part in the next repair. Pre-selected when a replacement is
    /// already chosen, so the common case needs one click rather than two.
    /// </summary>
    public bool IsSelected
    {
        get => _selectedCandidate != null;
        set
        {
            if (!value)
            {
                SelectedCandidate = null;
                return;
            }

            if (_selectedCandidate != null) return;

            // Checking a row with no explicit choice should adopt the inferred replacement.
            SelectedCandidate = Candidates.FirstOrDefault();
        }
    }

    /// <summary>Builds the replacement list, putting the same table's fields first.</summary>
    public static IReadOnlyList<FieldCandidate> BuildCandidates(SemanticModelIndex index, BrokenField field)
    {
        var candidates = new List<FieldCandidate>();

        // Same table first: a rename almost always keeps its table, and offering another
        // table's identically named column invites a binding that silently returns the
        // wrong data.
        foreach (var column in index.GetColumns(field.Entity))
        {
            candidates.Add(new FieldCandidate(field.Entity, column, isMeasure: false));
        }

        foreach (var measure in index.GetMeasures(field.Entity))
        {
            candidates.Add(new FieldCandidate(field.Entity, measure, isMeasure: true));
        }

        if (candidates.Count > 0) return candidates;

        // The table itself is gone, so fall back to offering every table in the model.
        foreach (var table in index.TableNames)
        {
            foreach (var column in index.GetColumns(table))
            {
                candidates.Add(new FieldCandidate(table, column, isMeasure: false));
            }

            foreach (var measure in index.GetMeasures(table))
            {
                candidates.Add(new FieldCandidate(table, measure, isMeasure: true));
            }
        }

        return candidates;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Finds every place the report binds a field the semantic model no longer provides — visuals,
/// visual filters, page filters and the report filter — and repoints them at a replacement the
/// author picks. This is the state a source-system column rename leaves behind.
/// </summary>
/// <remarks>
/// The scan is read-only and cheap enough to run on activation, so the page keeps itself current
/// instead of waiting to be asked. Repairs go through <see cref="ReportWorkspace.ApplyEdit"/>
/// because the edited files live inside the report's pages directory, and the workspace nominates
/// <c>definition/report.json</c> to undo history as well, so report-filter repairs are reversible
/// too.
/// </remarks>
public sealed partial class FieldRepairPage : Page, IToolPage
{
    private readonly BrokenVisualService _service = new();

    public FieldRepairPage()
    {
        InitializeComponent();

        // Start in the message state rather than showing two empty cards for the frame
        // before the first scan lands.
        ShowEmpty("Checking the report…", "Every visual is being compared with the semantic model.");

        Loaded += FieldRepairPage_Loaded;
        Unloaded += FieldRepairPage_Unloaded;
    }

    // ------------------------------------------------------- IToolPage

    public string ToolId => "field-repair";

    public string Title => "Field Repair";

    public string Description => "Find and repair visuals and filters broken by a renamed field";

    public string Glyph => "\uE945"; // Repair

    /// <summary>
    /// The shell's own focus save is skipped while a repair is in flight, so Desktop cannot
    /// write the author's stale in-memory report over the fix we just made.
    /// </summary>
    public bool SkipAutoPowerBiSync => _applying;

    private bool _applying;

    /// <summary>
    /// How long the progress ring stays up at minimum on a manual scan. A healthy report resolves
    /// in a few milliseconds, and a ring that flashes for one frame reads as a glitch rather than
    /// as work that happened. Automatic scans skip this, so a background refresh never makes the
    /// page feel like it is doing something the author did not ask for.
    /// </summary>
    private static readonly TimeSpan MinimumScanFeedback = TimeSpan.FromSeconds(1);

    /// <summary>Guards the auto-scan so activation and first load do not both scan.</summary>
    private bool _scannedForSession;

    private SemanticModelIndex? _index;
    private BrokenVisualReport? _report;
    private bool _suspendSelectionHandlers;

    /// <summary>
    /// The report path <see cref="_index"/> was built from. Compared on every workspace change so
    /// switching reports cannot leave the scan resolving against the previous report's model.
    /// </summary>
    private string _indexedReportPath = string.Empty;

    /// <summary>True while a scan pass is running, so bursts of watcher events cannot overlap.</summary>
    private bool _scanning;

    /// <summary>Set when something changed mid-scan, so one more pass runs after the current one.</summary>
    private bool _rescanQueued;

    /// <summary>The missing fields currently listed, bound to the left-hand list.</summary>
    private ObservableCollection<BrokenFieldItem> Fields { get; } = new();

    // ------------------------------------------------------- workspace wiring

    private void FieldRepairPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Detach first so a reload cannot leave a duplicate handler behind.
            App.Workspace.Changed -= Workspace_Changed;
            App.Workspace.Changed += Workspace_Changed;

            App.Workspace.ExternalChangeDetected -= Workspace_ContentChanged;
            App.Workspace.ExternalChangeDetected += Workspace_ContentChanged;

            App.Workspace.ModelChanged -= Workspace_ContentChanged;
            App.Workspace.ModelChanged += Workspace_ContentChanged;

            UpdateHistoryButtons();
        }
        catch (Exception ex)
        {
            App.Log($"Field Repair wiring failed: {ex}");
        }
    }

    private void FieldRepairPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Workspace.Changed -= Workspace_Changed;
        App.Workspace.ExternalChangeDetected -= Workspace_ContentChanged;
        App.Workspace.ModelChanged -= Workspace_ContentChanged;
    }

    /// <summary>
    /// Keeps the undo/redo buttons honest, and rebuilds the model index when the page is looking
    /// at a report it was not built for.
    /// </summary>
    /// <remarks>
    /// An external reload resets history, so the buttons have to be re-read rather than assumed.
    /// The report can also change under us: Home can open a different report, or open one for the
    /// first time after this page was constructed, and a stale index would resolve fields against
    /// the wrong model and report a healthy report as broken (or vice versa).
    /// </remarks>
    private void Workspace_Changed(object? sender, EventArgs e)
    {
        UpdateHistoryButtons();

        var current = App.Workspace.ReportPath;
        if (App.Workspace.HasReport &&
            !string.Equals(current, _indexedReportPath, StringComparison.OrdinalIgnoreCase))
        {
            EnsureScanned();
        }
    }

    /// <summary>
    /// Power BI saved the report, or refreshed the semantic model. Either can change which fields
    /// are broken — a model refresh is precisely what breaks a visual after a column rename — so
    /// a list left on screen would be stale exactly when the author is looking at it.
    /// </summary>
    private void Workspace_ContentChanged(object? sender, EventArgs e)
    {
        UpdateHistoryButtons();

        if (App.Workspace.HasReport) _ = ScanAsync(userInitiated: false);
    }

    // ------------------------------------------------------------- state

    private void EnsureScanned(bool userInitiated = false)
    {
        if (!App.Workspace.HasReport)
        {
            _index = null;
            _indexedReportPath = string.Empty;
            ShowEmpty("No report open",
                "Open a Power BI project (.pbip) so its visuals can be checked against its semantic model.");
            LastScanText.Text = string.Empty;
            return;
        }

        var modelFolder = SortByColumnService.DiscoverModelFolder(App.Workspace.ReportPath);
        if (string.IsNullOrEmpty(modelFolder))
        {
            _index = null;
            _indexedReportPath = App.Workspace.ReportPath;
            ShowEmpty("No semantic model found",
                "This tool compares the report's visuals with its semantic model. Open a Power BI project " +
                "that has both a .Report and a .SemanticModel folder.");
            LastScanText.Text = string.Empty;
            return;
        }

        _index = SemanticModelIndex.Load(modelFolder);
        _indexedReportPath = App.Workspace.ReportPath;

        // Show the previous result's timestamp straight away, so the page never looks
        // unscanned while the fresh scan runs.
        ShowLastScanFromConfig();

        _ = ScanAsync(userInitiated);
    }

    /// <summary>
    /// Runs a scan. <paramref name="userInitiated"/> controls the feedback: an automatic
    /// scan updates the page silently, because a toast on every tab switch would be noise,
    /// whereas pressing Scan should always acknowledge the press.
    /// </summary>
    private async Task ScanAsync(bool userInitiated)
    {
        if (_index == null) return;

        // A model refresh can fire several watcher events in a burst. Without this, each one
        // would start its own scan and they would fight over the same list.
        if (_scanning)
        {
            _rescanQueued = true;
            return;
        }

        _scanning = true;
        try
        {
            await RunScanPassAsync(userInitiated);

            // Something changed while that pass was running, so take one more to leave the list
            // describing what is actually on disk. The follow-up is never treated as manual, so
            // it cannot spam a toast.
            while (_rescanQueued)
            {
                _rescanQueued = false;
                await RunScanPassAsync(userInitiated: false);
            }
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task RunScanPassAsync(bool userInitiated)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        SetBusy(true);

        try
        {
            // Parsing every visual of a large report is not instant, so it stays off the
            // UI thread even though nothing is written.
            _report = await Task.Run(() => _service.Scan(App.Workspace.Manager, _index!));

            Render(_report!);
            RememberScan(started);
            ShowLastScanFromConfig();
            UpdateHistoryButtons();

            if (userInitiated)
            {
                var report = _report!;
                if (!report.ModelReadable)
                {
                    ToastService.Show("Could not read the semantic model.", ToastSeverity.Warning);
                }
                else if (report.IsClean)
                {
                    ToastService.Show(
                        $"No broken references — every field in {App.Workspace.Manager.Pages.Count} page(s) resolves against the model.",
                        ToastSeverity.Success);
                }
                else
                {
                    var visualWord = report.Visuals.Count == 1 ? "visual" : "visuals";
                    ToastService.Show(
                        $"{report.Fields.Count} missing field(s) across {report.Visuals.Count} {visualWord}.",
                        ToastSeverity.Informational);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"Field Repair scan failed: {ex}");
            if (userInitiated)
            {
                ToastService.Show($"Could not scan the report: {ex.Message}", ToastSeverity.Error);
            }

            ShowEmpty("Scan failed", ex.Message);
        }
        finally
        {
            // Hold the ring for the minimum only on a manual scan, so a fast one still reads as a
            // deliberate action. An automatic refresh should not make the page look busy.
            if (userInitiated)
            {
                var remaining = MinimumScanFeedback - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining);
                }
            }

            SetBusy(false);
            _scannedForSession = true;
        }
    }

    // ------------------------------------------------------------- undo/redo

    private void Undo_Click(object sender, RoutedEventArgs e) => ApplyHistoryStep(undo: true);

    private void Redo_Click(object sender, RoutedEventArgs e) => ApplyHistoryStep(undo: false);

    /// <summary>
    /// Steps the shared history and re-scans.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="ReportWorkspace.Undo"/>/<see cref="ReportWorkspace.Redo"/> rather
    /// than <c>ApplyEdit</c>: ApplyEdit records the restore as a new edit, which truncates the
    /// redo branch and leaves Redo permanently disabled.
    /// </remarks>
    private void ApplyHistoryStep(bool undo)
    {
        var history = App.Workspace.History;
        if (history == null) return;
        if (undo ? !history.CanUndo : !history.CanRedo) return;

        try
        {
            var moved = undo ? App.Workspace.Undo() : App.Workspace.Redo();
            if (moved)
            {
                ToastService.Show(undo ? "Undid the last repair." : "Redid the last repair.",
                    ToastSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Field Repair history step failed: {ex}");
            ToastService.Show($"Could not restore history: {ex.Message}", ToastSeverity.Error);
        }

        UpdateHistoryButtons();

        // The report on disk just changed underneath us, so re-scan rather than leaving the list
        // describing the pre-undo state.
        _ = ScanAsync(userInitiated: false);
    }

    private void UpdateHistoryButtons()
    {
        var history = App.Workspace.History;
        if (UndoButton != null) UndoButton.IsEnabled = history?.CanUndo == true;
        if (RedoButton != null) RedoButton.IsEnabled = history?.CanRedo == true;
    }

    private void RememberScan(DateTimeOffset when)
    {
        try
        {
            var config = ReportConfigStore.Load(App.Workspace.ReportPath);
            config.SetBrokenVisualsLastScan(when);
            ReportConfigStore.Save(App.Workspace.ReportPath, config);
        }
        catch (Exception ex)
        {
            // The scan result is already on screen; failing to timestamp it is cosmetic.
            App.Log($"Could not record the broken-visuals scan time: {ex}");
        }
    }

    private void ShowLastScanFromConfig()
    {
        try
        {
            var last = ReportConfigStore.Load(App.Workspace.ReportPath).GetBrokenVisualsLastScan();
            LastScanText.Text = last.HasValue
                ? $"Last checked {last.Value.ToLocalTime():d MMM yyyy, HH:mm}"
                : "Not checked yet";
        }
        catch
        {
            LastScanText.Text = string.Empty;
        }
    }

    private void Render(BrokenVisualReport report)
    {
        _suspendSelectionHandlers = true;
        try
        {
            Fields.Clear();
            FieldsListView.ItemsSource = Fields;
            UsagesListView.ItemsSource = null;

            if (!report.ModelReadable)
            {
                ShowEmpty("Semantic model cannot be read", report.UnsupportedReason ?? string.Empty);
                ScanSummaryText.Text = "Nothing scanned.";
                FooterSummaryText.Text = string.Empty;
                UpdateActions();
                return;
            }

            if (report.IsClean)
            {
                ShowEmpty("No broken references",
                    "Every field in every visual and filter resolves against the semantic model. Run a scan again " +
                    "after refreshing the model if you have just changed the source data.");
                ScanSummaryText.Text =
                    $"Scanned {App.Workspace.Manager.Pages.Count} page(s) — all fields resolve.";
                FooterSummaryText.Text = string.Empty;
                UpdateActions();
                return;
            }

            ShowContent();

            var remembered = ReportConfigStore.Load(App.Workspace.ReportPath)
                .GetFieldRemaps()
                .ToDictionary(r => r.OldKey, r => r, StringComparer.OrdinalIgnoreCase);

            var preSelectEasy = CosmeticToggle.IsChecked == true;

            foreach (var field in report.Fields)
            {
                var candidates = BrokenFieldItem.BuildCandidates(_index!, field);

                // A remembered answer wins over inference: the author already decided, and a
                // cosmetic match can be a different column than the one they meant.
                FieldCandidate? initial = null;
                if (remembered.TryGetValue(field.Key, out var previous))
                {
                    initial = candidates.FirstOrDefault(c =>
                        string.Equals(c.Entity, previous.NewEntity, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Property, previous.NewProperty, StringComparison.Ordinal));
                }

                // Every reference is repairable, including report-level filters: the workspace
                // nominates definition/report.json to undo history, so a repair there is
                // reversible like any other edit.
                initial ??= field.HasSuggestion
                    ? candidates.FirstOrDefault(c =>
                        string.Equals(c.Entity, field.SuggestedEntity, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Property, field.SuggestedProperty, StringComparison.Ordinal))
                    : null;

                if (initial == null && preSelectEasy && field.IsCosmetic)
                {
                    initial = candidates.FirstOrDefault();
                }

                var item = new BrokenFieldItem(field, candidates, initial);
                item.PropertyChanged += FieldItem_PropertyChanged;
                Fields.Add(item);
            }

            ScanSummaryText.Text =
                $"{report.Fields.Count} missing field(s) across {report.Visuals.Count} visual(s)" +
                (report.CosmeticCount > 0 ? $" · {report.CosmeticCount} look like a rename" : string.Empty);

            UpdateFooter();
        }
        finally
        {
            _suspendSelectionHandlers = false;
        }

        // Select after the guard is released, so the change actually populates the
        // "where it is used" panel instead of being swallowed as a programmatic update.
        if (Fields.Count > 0)
        {
            FieldsListView.SelectedIndex = 0;
        }
    }

    // ------------------------------------------------------------ actions

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Workspace.HasReport)
        {
            ToastService.Show("Open a report first.", ToastSeverity.Warning);
            return;
        }

        // One scan, and it acknowledges the press.
        EnsureScanned(userInitiated: true);
    }

    private void CosmeticToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspendSelectionHandlers || _report == null) return;

        var config = ReportConfigStore.Load(App.Workspace.ReportPath);
        config.SetBrokenVisualsAutoFix(CosmeticToggle.IsChecked == true);
        ReportConfigStore.Save(App.Workspace.ReportPath, config);

        // Re-render so the new preference takes effect on the staged selections.
        Render(_report);
    }

    private void FieldCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is BrokenFieldItem item)
        {
            item.IsSelected = box.IsChecked == true;
        }

        UpdateFooter();
    }

    private void CandidateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendSelectionHandlers) return;
        if (sender is ComboBox combo && combo.DataContext is BrokenFieldItem item)
        {
            item.SelectedCandidate = combo.SelectedItem as FieldCandidate;
        }

        UpdateFooter();
    }

    private void FieldItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrokenFieldItem.SelectedCandidate)) UpdateFooter();
    }

    private void FieldsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendSelectionHandlers) return;

        var item = FieldsListView.SelectedItem as BrokenFieldItem;
        UsagesListView.ItemsSource = item?.Usages;
        UsagesHeaderText.Text = item == null ? string.Empty : $"{item.Usages.Count} place(s)";
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var remaps = CollectRemaps();
        if (remaps.Count == 0)
        {
            ToastService.Show("Choose at least one replacement first.", ToastSeverity.Warning);
            return;
        }

        SetBusy(true);
        _applying = true;
        try
        {
            RemapResult result = null!;

            // Routed through the workspace so the repair joins undo history, refreshes the
            // external-change signature and asks Power BI to reload — all of which a raw
            // write would skip.
            App.Workspace.ApplyEdit(m =>
            {
                // Use the manager handed to us rather than App.Workspace.Manager, so the
                // edit and the history snapshot it triggers describe the same object.
                result = _service.ApplyRemap(m, remaps, _index!);
            }, managerWritesInternally: true);

            foreach (var warning in result.Warnings)
            {
                ToastService.Show(warning, ToastSeverity.Warning);
            }

            if (result.NoChange)
            {
                ToastService.Show("Nothing changed — those visuals already point at the chosen fields.",
                    ToastSeverity.Informational);
                return;
            }

            // A repair is undoable, so the history buttons have to reflect the new step.
            UpdateHistoryButtons();

            RememberRemaps(remaps);

            var verb = result.ReferencesChanged == 1 ? "reference" : "references";
            ToastService.Show(
                $"Repointed {result.ReferencesChanged} {verb} in {result.FilesChanged} visual(s). " +
                "Power BI Desktop will reload the report.",
                ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            App.Log($"Field Repair apply failed: {ex}");
            ToastService.Show($"Could not apply: {ex.Message}", ToastSeverity.Error);
        }
        finally
        {
            _applying = false;

            // Re-scan so the list reflects what is actually on disk now. Silent, because the
            // apply already reported its own outcome and a second toast would be noise.
            SetBusy(false);
            if (App.Workspace.HasReport && _index != null) await ScanAsync(userInitiated: false);
        }
    }

    /// <summary>Stages the chosen replacements as remaps for the fields that have one.</summary>
    private List<FieldRemap> CollectRemaps()
    {
        var remaps = new List<FieldRemap>();
        foreach (var item in Fields)
        {
            var candidate = item.SelectedCandidate;
            if (candidate == null) continue;

            remaps.Add(new FieldRemap
            {
                OldEntity = item.Model.Entity,
                OldProperty = item.Model.Property,
                NewEntity = candidate.Entity,
                NewProperty = candidate.Property
            });
        }

        return remaps;
    }

    /// <summary>
    /// Records what was applied so the same rename does not have to be answered again on the
    /// next scan.
    /// </summary>
    private void RememberRemaps(IEnumerable<FieldRemap> applied)
    {
        try
        {
            var config = ReportConfigStore.Load(App.Workspace.ReportPath);
            var existing = config.GetFieldRemaps();
            var now = DateTimeOffset.UtcNow;

            foreach (var remap in applied)
            {
                remap.RecordedUtc = now;
                existing.RemoveAll(r => string.Equals(r.OldKey, remap.OldKey, StringComparison.OrdinalIgnoreCase));
                existing.Add(remap);
            }

            config.SetFieldRemaps(existing);

            if (!ReportConfigStore.Save(App.Workspace.ReportPath, config))
            {
                ToastService.Show("Repaired, but the mapping could not be remembered for next time.", ToastSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            // The repair itself already succeeded; failing to remember it is not worth
            // alarming the author over.
            App.Log($"Could not remember broken-visual remaps: {ex}");
        }
    }

    // -------------------------------------------------------------- chrome

    /// <summary>
    /// The live preview. Rather than a separate Preview button that only reports counts, the
    /// footer states exactly what Repair is about to do, and the button repeats it.
    /// </summary>
    private void UpdateFooter()
    {
        var staged = Fields.Where(f => f.SelectedCandidate != null).ToList();

        if (staged.Count == 0)
        {
            FooterSummaryText.Text = Fields.Count == 0
                ? string.Empty
                : $"{Fields.Count} missing field(s) — choose a replacement to repair";
            ApplyButtonLabel.Text = "Repair selected";
            UpdateActions();
            return;
        }

        var visuals = staged.Sum(f => f.Model.VisualCount);
        var filters = staged.Sum(f => f.Model.FilteredPageCount);
        var reportFilters = staged.Count(f => f.Model.HasReportFilter);

        var parts = new List<string>();
        if (visuals > 0) parts.Add(visuals == 1 ? "1 visual" : $"{visuals} visuals");
        if (filters > 0) parts.Add(filters == 1 ? "1 page filter" : $"{filters} page filters");
        if (reportFilters > 0) parts.Add(reportFilters == 1 ? "the report filter" : "the report filters");

        FooterSummaryText.Text =
            $"Repairing {staged.Count} field(s) will update {string.Join(", ", parts)}.";

        ApplyButtonLabel.Text = staged.Count == 1
            ? "Repair 1 field"
            : $"Repair {staged.Count} fields";

        UpdateActions();
    }

    private void UpdateActions()
    {
        ApplyButton.IsEnabled = Fields.Any(f => f.SelectedCandidate != null);
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy;
    }

    /// <summary>
    /// Hides the two cards entirely and shows one centred message. Leaving the card borders
    /// in place drew their edges straight through the empty-state text.
    /// </summary>
    private void ShowEmpty(string title, string body)
    {
        FieldsCard.Visibility = Visibility.Collapsed;
        UsagesCard.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;

        EmptyTitleText.Text = title;
        EmptyBodyText.Text = body;

        FieldsListView.ItemsSource = null;
        UsagesListView.ItemsSource = null;
        UsagesHeaderText.Text = string.Empty;
    }

    private void ShowContent()
    {
        FieldsCard.Visibility = Visibility.Visible;
        UsagesCard.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
    }

    public void OnActivated()
    {
        // Refresh when the author comes back to the tab: the model may have been refreshed
        // in Desktop while this page was hidden, which is exactly when new breakage appears.
        // The session guard stops the first activation scanning twice, since the page is
        // created and activated in the same pass.
        UpdateHistoryButtons();

        if (_scannedForSession) _ = ScanAsync(userInitiated: false);
        else EnsureScanned();
    }
}