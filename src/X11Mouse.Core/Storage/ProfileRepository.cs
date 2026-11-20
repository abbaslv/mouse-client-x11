using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.Core.Storage;

public sealed class AppConfigStore
{
    public int ActiveProfileIndex { get; set; }
    public string Language { get; set; } = "English";
    public bool AutoApplyOnChange { get; set; }
    public List<MouseProfile> Profiles { get; set; } = new();
    public List<MacroDefinition> Macros { get; set; } = new();
}

/// <summary>
/// Manages persistent configuration inside `%APPDATA%\AttackSharkX11Clean\` (isolated from official `Mouse.exe`),
/// with read-only import support for official `pro.data`, `.profile`, `macro.data`, `.macro`, and `.json`.
/// </summary>
public sealed class ProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string StorageDirectory { get; }
    public string StoreFilePath => Path.Combine(StorageDirectory, "config.json");

    public static string OfficialAppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "X11 Mouse",
        "ms_1");

    public ProfileRepository(string? customDirectory = null)
    {
        StorageDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AttackSharkX11Clean");
    }

    public AppConfigStore LoadOrInitialize()
    {
        if (File.Exists(StoreFilePath))
        {
            string json = File.ReadAllText(StoreFilePath);
            var loaded = JsonSerializer.Deserialize<AppConfigStore>(json, JsonOptions);
            if (loaded is not null && loaded.Profiles.Count > 0)
            {
                loaded.ActiveProfileIndex = Math.Clamp(loaded.ActiveProfileIndex, 0, loaded.Profiles.Count - 1);
                return loaded;
            }
        }

        // Non-destructive initial seed: read official AppData profiles/macros read-only if present.
        var store = new AppConfigStore();
        TrySeedFromOfficialAppData(store);
        if (store.Profiles.Count == 0)
        {
            store.Profiles.Add(X11Protocol.CreateDefaultProfile("Profile1"));
        }
        if (store.Macros.Count == 0)
        {
            store.Macros.Add(new MacroDefinition { Name = "Macro1", RepeatCount = 1 });
        }

        Save(store);
        return store;
    }

    public bool TrySeedFromOfficialAppData(AppConfigStore store)
    {
        bool importedAny = false;
        string proPath = Path.Combine(OfficialAppDataDirectory, "pro.data");
        if (File.Exists(proPath))
        {
            try
            {
                var bundle = LegacyBinaryFormat.ReadProData(proPath);
                if (bundle.Profiles.Count > 0)
                {
                    store.Profiles.Clear();
                    store.Profiles.AddRange(bundle.Profiles);
                    store.ActiveProfileIndex = Math.Clamp(bundle.ActiveProfileIndex, 0, store.Profiles.Count - 1);
                    importedAny = true;
                }
            }
            catch
            {
                // Fall back to default profile cleanly if official file cannot be read.
            }
        }

        string macroPath = Path.Combine(OfficialAppDataDirectory, "macro.data");
        if (File.Exists(macroPath))
        {
            try
            {
                var macros = LegacyBinaryFormat.ReadMacroFile(macroPath);
                if (macros.Count > 0)
                {
                    store.Macros.Clear();
                    store.Macros.AddRange(macros);
                    importedAny = true;
                }
            }
            catch
            {
                // Ignore invalid legacy macro file and continue.
            }
        }

        return importedAny;
    }

    public void Save(AppConfigStore store)
    {
        Directory.CreateDirectory(StorageDirectory);
        string json = JsonSerializer.Serialize(store, JsonOptions);
        File.WriteAllText(StoreFilePath, json);
    }

    public MouseProfile ImportProfileFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".json")
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<MouseProfile>(json, JsonOptions)
                   ?? throw new InvalidDataException("Invalid profile JSON.");
        }
        return LegacyBinaryFormat.ReadSingleProfile(filePath);
    }

    public void ExportProfileFile(MouseProfile profile, string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".json")
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(profile, JsonOptions));
            return;
        }
        File.WriteAllBytes(filePath, LegacyBinaryFormat.SerializeProfileRecord(profile));
    }

    public List<MacroDefinition> ImportMacroFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".json")
        {
            string json = File.ReadAllText(filePath);
            var single = JsonSerializer.Deserialize<MacroDefinition>(json, JsonOptions);
            if (single is not null && !string.IsNullOrWhiteSpace(single.Name))
                return [single];
            return JsonSerializer.Deserialize<List<MacroDefinition>>(json, JsonOptions) ?? new List<MacroDefinition>();
        }
        return LegacyBinaryFormat.ReadMacroFile(filePath);
    }

    public void ExportMacroFile(MacroDefinition macro, string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".json")
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(macro, JsonOptions));
            return;
        }
        File.WriteAllBytes(filePath, LegacyBinaryFormat.SerializeMacroRecord(macro));
    }
}
