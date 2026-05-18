// Services/SettingsService.cs
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

public sealed class SettingsService
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        IPlatformInfo.Current.SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;
    private CancellationTokenSource? _debounceCts;

    public SettingsService() : this(DefaultSettingsPath) { }

    public SettingsService(string settingsPath) => _settingsPath = settingsPath;

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Current = new AppSettings();
                await SaveImmediateAsync();
                return;
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                Current = new AppSettings();
                await SaveImmediateAsync();
                return;
            }

            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (IsMissingDefaultFields(json))
                await SaveImmediateAsync();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    private static bool IsMissingDefaultFields(string json)
    {
        try
        {
            using var current = JsonDocument.Parse(json);
            if (current.RootElement.ValueKind != JsonValueKind.Object)
                return true;

            var defaultsJson = JsonSerializer.Serialize(new AppSettings(), JsonOptions);
            using var defaults = JsonDocument.Parse(defaultsJson);

            foreach (var property in defaults.RootElement.EnumerateObject())
            {
                if (!current.RootElement.TryGetProperty(property.Name, out _))
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public void Save()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await SaveImmediateAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Save failed: {ex.Message}");
            }
        });
    }

    public async Task SaveImmediateAsync()
    {
        var dir = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
    }
}
