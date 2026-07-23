using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Core.Skills;
using TunnelAgent.Infrastructure.Skills;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

public sealed partial class SkillsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly AsmProvisionService _provision;
    private readonly AsmCliService _cli;
    private readonly IFolderOpenService _folderOpen;
    private bool _loaded;

    public SkillsViewModel(SettingsService settings, AsmProvisionService provision, AsmCliService cli, IFolderOpenService folderOpen)
    {
        _settings = settings;
        _provision = provision;
        _cli = cli;
        _folderOpen = folderOpen;
        _selectedScope = NormalizeScope(_settings.Current.Skills.Scope);
    }

    public ObservableCollection<SkillViewModel> Installed { get; } = [];
    public ObservableCollection<SkillViewModel> SearchResults { get; } = [];
    public IReadOnlyList<string> Scopes { get; } = ["global", "project"];

    [ObservableProperty] private AsmProvisionState _state = AsmProvisionState.CheckingPrerequisites;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _nodeVersion = "";
    [ObservableProperty] private string _npmVersion = "";
    [ObservableProperty] private string _asmInstalledVersion = "";
    [ObservableProperty] private string _asmLatestVersion = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedScope;
    [ObservableProperty] private SkillViewModel? _selectedSkill;
    [ObservableProperty] private string _skillDetailText = "";
    [ObservableProperty] private bool _showAuditConfirmation;
    [ObservableProperty] private SkillViewModel? _pendingInstallSkill;
    [ObservableProperty] private string _auditVerdict = "";
    [ObservableProperty] private string _auditSummary = "";

    public bool PrerequisitesAvailable => State is not AsmProvisionState.NotAvailable and not AsmProvisionState.CheckingPrerequisites;
    public bool IsAsmReady => State is AsmProvisionState.Ready or AsmProvisionState.UpdateAvailable;
    public bool CanInstallAsm => PrerequisitesAvailable && State == AsmProvisionState.NotInstalled;
    public bool UpdateAvailable => State == AsmProvisionState.UpdateAvailable;
    public int InstalledCount => Installed.Count;
    public bool HasInstalled => Installed.Count > 0;
    public bool HasSearchResults => SearchResults.Count > 0;

    partial void OnStateChanged(AsmProvisionState value) => RaiseComputed();
    partial void OnSelectedScopeChanged(string value)
    {
        var normalized = NormalizeScope(value);
        if (normalized != value) { SelectedScope = normalized; return; }
        _settings.Current.Skills.Scope = normalized;
        _settings.Save();
        if (_loaded && IsAsmReady) _ = RefreshAsync();
    }

    public Task EnsureLoadedAsync() => _loaded && State != AsmProvisionState.CheckingPrerequisites
        ? Task.CompletedTask
        : RecheckPrerequisitesAsync();

    [RelayCommand]
    public async Task RecheckPrerequisitesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        State = AsmProvisionState.CheckingPrerequisites;
        StatusMessage = "Checking Node.js and npm…";
        try
        {
            var prerequisite = await _provision.CheckPrerequisitesAsync();
            NodeVersion = prerequisite.NodeVersion ?? "Not found";
            NpmVersion = prerequisite.NpmVersion ?? "Not found";
            if (!prerequisite.IsCompatible)
            {
                State = AsmProvisionState.NotAvailable;
                StatusMessage = prerequisite.FailureReason;
                return;
            }

            _loaded = true;
            if (!_provision.IsAsmInstalled())
            {
                State = AsmProvisionState.NotInstalled;
                StatusMessage = "ASM is not installed. Install stays inside Tunnel Agent data.";
                return;
            }

            AsmInstalledVersion = await _provision.GetInstalledVersionAsync() ?? "Unknown";
            State = AsmProvisionState.Ready;
            StatusMessage = "ASM ready.";
            await ReloadInstalledAsync();
            if (_settings.Current.Skills.AutoCheckForUpdates) await CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            State = AsmProvisionState.Error;
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task InstallAsmAsync()
    {
        if (!CanInstallAsm || IsBusy) return;
        await InstallOrUpdateAsmAsync(_settings.Current.Skills.PreferredVersion);
    }

    [RelayCommand]
    public async Task UpdateAsmAsync()
    {
        if (!UpdateAvailable || IsBusy) return;
        await InstallOrUpdateAsmAsync(AsmLatestVersion);
    }

    private async Task InstallOrUpdateAsmAsync(string version)
    {
        IsBusy = true;
        State = AsmProvisionState.Installing;
        StatusMessage = $"Installing ASM {version}…";
        try
        {
            await _provision.InstallAsync(version);
            AsmInstalledVersion = await _provision.GetInstalledVersionAsync() ?? version;
            _settings.Current.Skills.PreferredVersion = AsmInstalledVersion;
            _settings.Save();
            State = AsmProvisionState.Ready;
            StatusMessage = "ASM ready.";
            await ReloadInstalledAsync();
        }
        catch (Exception ex) { State = AsmProvisionState.Error; StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task CheckForUpdateAsync()
    {
        if (!IsAsmReady) return;
        try
        {
            AsmLatestVersion = await _provision.CheckForUpdateAsync();
            if (Version.TryParse(AsmLatestVersion, out var latest) && Version.TryParse(AsmInstalledVersion, out var installed) && latest > installed)
            {
                State = AsmProvisionState.UpdateAvailable;
                StatusMessage = $"ASM {AsmLatestVersion} is available.";
            }
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsAsmReady || IsBusy) return;
        IsBusy = true;
        try { await ReloadInstalledAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (!IsAsmReady || IsBusy || string.IsNullOrWhiteSpace(SearchText)) return;
        IsBusy = true;
        try
        {
            SearchResults.Clear();
            foreach (var skill in await _cli.SearchAsync(SearchText.Trim(), SelectedScope))
            {
                if (Installed.Any(installed => string.Equals(installed.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
                    skill.Status = "installed";
                SearchResults.Add(new(skill));
            }
            RaiseComputed();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task InspectAsync(SkillViewModel? skill)
    {
        if (skill is null || !IsAsmReady) return;
        try
        {
            SelectedSkill = skill;
            if (!skill.Installed)
            {
                SkillDetailText = $"{skill.Description}\n\nSource: {skill.Source}";
                return;
            }
            var detail = await _cli.InspectAsync(skill.Name, skill.Scope.Length == 0 ? SelectedScope : skill.Scope);
            SkillDetailText = await ReadSkillDetailsAsync(detail.Path);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task AuditAsync(SkillViewModel? skill)
    {
        if (skill is null || !IsAsmReady) return;
        try
        {
            var audit = await _cli.AuditSecurityAsync(skill.Installed ? skill.Name : skill.Source);
            skill.AuditVerdict = audit.Verdict;
            SelectedSkill = skill;
            AuditVerdict = audit.Verdict;
            AuditSummary = audit.VerdictReason;
            SkillDetailText = FormatAudit(audit);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RequestInstallAsync(SkillViewModel? skill)
    {
        if (skill is null || skill.Installed || !IsAsmReady || IsBusy) return;
        IsBusy = true;
        try
        {
            var audit = await _cli.AuditSecurityAsync(skill.Source);
            PendingInstallSkill = skill;
            AuditVerdict = audit.Verdict;
            AuditSummary = audit.VerdictReason;
            ShowAuditConfirmation = true;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task ConfirmInstallAsync()
    {
        var skill = PendingInstallSkill;
        ShowAuditConfirmation = false;
        PendingInstallSkill = null;
        if (skill is null) return;
        IsBusy = true;
        try
        {
            await _cli.InstallAsync(skill.Source, SelectedScope);
            StatusMessage = $"{skill.Name} installed.";
            await RefreshAfterBusyAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void CancelInstall() { ShowAuditConfirmation = false; PendingInstallSkill = null; }

    [RelayCommand]
    public async Task UninstallAsync(SkillViewModel? skill)
    {
        if (skill is null || !skill.Installed || IsBusy) return;
        IsBusy = true;
        try
        {
            await _cli.UninstallAsync(skill.Name, skill.Scope.Length == 0 ? SelectedScope : skill.Scope);
            StatusMessage = $"{skill.Name} removed.";
            await RefreshAfterBusyAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveDuplicatesAsync()
    {
        if (!IsAsmReady || IsBusy) return;
        IsBusy = true;
        try
        {
            await _cli.AuditDedupeAsync(SelectedScope);
            StatusMessage = "Duplicate audit complete.";
            await RefreshAfterBusyAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenSkillFolder(SkillViewModel? skill)
    {
        if (skill is not null && !string.IsNullOrWhiteSpace(skill.Path)) _folderOpen.OpenFolder(skill.Path);
    }

    [RelayCommand] private void OpenSkillsFolder() => _folderOpen.OpenFolder(SkillsDirectory(SelectedScope));
    [RelayCommand] private static void OpenNodeDownload() => OpenUrl("https://nodejs.org/en/download");

    private async Task RefreshAfterBusyAsync()
    {
        IsBusy = false;
        await RefreshAsync();
        IsBusy = true;
    }

    private async Task ReloadInstalledAsync()
    {
        Installed.Clear();
        foreach (var skill in await _cli.ListInstalledAsync(SelectedScope)) Installed.Add(new(skill));
        RaiseComputed();
    }

    private static string NormalizeScope(string? scope) => string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) ? "project" : "global";
    private static string SkillsDirectory(string scope) => scope == "project"
        ? Path.Combine(Environment.CurrentDirectory, ".agents", "skills")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");

    private static async Task<string> ReadSkillDetailsAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "Skill path unavailable.";
        var markdownPath = Path.Combine(directory, "SKILL.md");
        var markdown = File.Exists(markdownPath) ? await File.ReadAllTextAsync(markdownPath) : $"SKILL.md not found in {directory}";
        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(directory, path)).Order().Take(200)
            : [];
        return $"{markdown}\n\nFiles\n-----\n{string.Join("\n", files)}";
    }

    private static string FormatAudit(SkillAudit audit) => $"Verdict: {audit.Verdict}\n{audit.VerdictReason}\n\nFiles: {audit.TotalFiles} · Lines: {audit.TotalLines}\nPermissions: {string.Join(", ", audit.Permissions.Select(p => p.Type))}";
    private static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    private void RaiseComputed()
    {
        OnPropertyChanged(nameof(PrerequisitesAvailable));
        OnPropertyChanged(nameof(IsAsmReady));
        OnPropertyChanged(nameof(CanInstallAsm));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasSearchResults));
    }
}
