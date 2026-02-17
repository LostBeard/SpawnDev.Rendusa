namespace SpawnDev.Rendusa.Rendering;

/// <summary>
/// A UI action produced by mouse/touch interactions with the player controls.
/// The Type string identifies the action (e.g. "play", "seek", "volume").
/// Value carries an optional float payload (e.g. seek fraction, volume level).
/// </summary>
public record PlayerAction(string Type, float Value = 0f);
