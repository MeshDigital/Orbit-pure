namespace SLSKDONET.Events;

/// <summary>Settings turned the opt-in Rekordbox library write on or off; menus that offer it re-check.</summary>
public record RekordboxDbWriteSettingChangedEvent(bool Enabled);
