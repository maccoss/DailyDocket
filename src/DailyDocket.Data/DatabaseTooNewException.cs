namespace DailyDocket.Data;

/// <summary>
/// The database was written by a newer DailyDocket than this one. Its own type, so startup can
/// tell it from every other failure: this one is cured by updating, so the app looks for the update
/// instead of stopping (a copy that stopped here could never update itself).
/// </summary>
public sealed class DatabaseTooNewException(long found, long known) : InvalidOperationException(
    "This computer's DailyDocket database was written by a newer version of DailyDocket. Update DailyDocket, then start it again.")
{
    /// <summary>The database's schema version.</summary>
    public long Found { get; } = found;

    /// <summary>The newest schema version this copy knows.</summary>
    public long Known { get; } = known;
}
