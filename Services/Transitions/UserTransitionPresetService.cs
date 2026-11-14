using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Transitions;

/// <summary>Everything the transition editor's Custom blend is made of, plus its length.</summary>
public sealed record CustomBlendSettings(
    int DurationBars,
    float? EchoDecayFactor = null,
    float? FilterStartFrequency = null,
    float? FilterEndFrequency = null,
    float? WaveDuckDepth = null,
    bool? FilterSweepRising = null,
    bool? EqSwapLow = null,
    bool? EqSwapMid = null,
    bool? EqSwapHigh = null,
    float? EqLowCrossoverHz = null,
    float? EqHighCrossoverHz = null,
    bool? LoopEnabled = null,
    int? LoopBars = null,
    int? LoopRepeats = null);

/// <summary>A DJ's own named Custom blend, shown next to the built-in presets.</summary>
public sealed record UserTransitionPreset(Guid Id, string Name, CustomBlendSettings Settings);

/// <summary>
/// User transition presets (rebuild plan D5 / P3.5, #204): <see cref="TransitionTemplateEntity"/>
/// rows surfaced in the transition editor's preset picker. The executable "how" stays the DSP
/// <c>TransitionType</c> + built-in presets; a user preset is a saved Custom blend on top of them.
/// </summary>
public interface IUserTransitionPresetService
{
    /// <summary>All user presets, by name.</summary>
    Task<IReadOnlyList<UserTransitionPreset>> GetAllAsync();

    /// <summary>Saves under <paramref name="name"/>; an existing preset with that name (any case) is replaced.</summary>
    Task<UserTransitionPreset> SaveAsync(string name, CustomBlendSettings settings);

    /// <summary>False when the preset is already gone.</summary>
    Task<bool> DeleteAsync(Guid id);
}

public sealed class UserTransitionPresetService : IUserTransitionPresetService
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<UserTransitionPresetService> _logger;

    public UserTransitionPresetService(IDbContextFactory<AppDbContext> dbFactory, ILogger<UserTransitionPresetService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UserTransitionPreset>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rows = await db.TransitionTemplates.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var presets = new List<UserTransitionPreset>();
        foreach (var row in rows)
        {
            if (FromEntity(row) is { } preset)
                presets.Add(preset);
            else
                _logger.LogWarning("[TransitionPresets] skipping unreadable preset {Name} ({Id})", row.Name, row.Id);
        }
        return presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<UserTransitionPreset> SaveAsync(string name, CustomBlendSettings settings)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ArgumentException("A preset needs a name.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var lower = trimmed.ToLowerInvariant();
        var row = await db.TransitionTemplates.FirstOrDefaultAsync(t => t.Name.ToLower() == lower).ConfigureAwait(false);
        if (row is null)
        {
            row = new TransitionTemplateEntity();
            db.TransitionTemplates.Add(row);
        }
        row.Name = trimmed;
        row.BarsBeforeMix = settings.DurationBars;
        row.EffectsJson = JsonSerializer.Serialize(settings, Json);
        row.LoopSettingsJson = settings.LoopEnabled == true
            ? JsonSerializer.Serialize(new { bars = settings.LoopBars, repeats = settings.LoopRepeats }, Json)
            : null;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new UserTransitionPreset(row.Id, row.Name, settings);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var row = await db.TransitionTemplates.FirstOrDefaultAsync(t => t.Id == id).ConfigureAwait(false);
        if (row is null)
            return false;
        db.TransitionTemplates.Remove(row);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>The preset a row holds, or null when its settings JSON is missing or unreadable.</summary>
    internal static UserTransitionPreset? FromEntity(TransitionTemplateEntity row)
    {
        if (string.IsNullOrWhiteSpace(row.EffectsJson) || string.IsNullOrWhiteSpace(row.Name))
            return null;
        try
        {
            var settings = JsonSerializer.Deserialize<CustomBlendSettings>(row.EffectsJson, Json);
            if (settings is null)
                return null;
            if (settings.DurationBars <= 0)
                settings = settings with { DurationBars = row.BarsBeforeMix > 0 ? row.BarsBeforeMix : 16 };
            return new UserTransitionPreset(row.Id, row.Name, settings);
        }
        catch (JsonException)
        {
            return null; // the caller logs the skipped row
        }
    }
}
