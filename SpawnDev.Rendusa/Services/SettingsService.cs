using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.Rendusa.Models;

namespace SpawnDev.Rendusa.Services;

/// <summary>
/// Manages application settings with IndexedDB persistence.
/// </summary>
public class SettingsService : IAsyncBackgroundService
{
    private const string DbName = "RendusaDB";
    private const int DbVersion = 5; // Must match MediaLibraryService
    private const string SettingsStoreName = "settings";
    private const string SettingsKey = "appSettings";

    private IDBDatabase? _db;

    public AppSettings Settings { get; private set; } = new();

    public event Action? OnChanged;

    /// <summary>Awaited by BlazorJSRunAsync before the first page renders.</summary>
    public Task Ready => _ready ??= InitAsync();
    private Task? _ready;

    private async Task InitAsync()
    {
        using var idbFactory = new IDBFactory();
        _db = await idbFactory.OpenAsync(DbName, DbVersion, (evt) =>
        {
            using var request = evt.Target;
            using var db = request.Result;
            var stores = db.ObjectStoreNames;
            if (!stores.Contains(SettingsStoreName))
            {
                db.CreateObjectStore<string, AppSettings>(SettingsStoreName);
            }
        });
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_db == null) return;
        try
        {
            using var tx = _db.Transaction(SettingsStoreName);
            using var store = tx.ObjectStore<string, AppSettings>(SettingsStoreName);
            var loaded = await store.GetAsync(SettingsKey);
            if (loaded != null)
            {
                Settings = loaded;
            }
        }
        catch
        {
            // First run — no settings saved yet
        }
    }

    public async Task SaveAsync()
    {
        if (_db == null) return;
        using var tx = _db.Transaction(SettingsStoreName, true);
        using var store = tx.ObjectStore<string, AppSettings>(SettingsStoreName);
        await store.PutAsync(Settings, SettingsKey);
        OnChanged?.Invoke();
    }
}
