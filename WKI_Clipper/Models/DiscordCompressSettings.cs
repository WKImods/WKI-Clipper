namespace WKI_Clipper.Models;

/// <summary>
/// The two "Discord" compress buttons of the gallery. The section has an initializer for every value,
/// so a settings file written before it existed simply loads the defaults - no schema migration.
///
/// Sizes are decimal bytes: Discord's help pages mix MB and MiB, and 20 000 000 is the one that is
/// never too big.
/// </summary>
public sealed class DiscordCompressSettings
{
    /// <summary>Free accounts: 20 MB per file.</summary>
    public long SmallTargetBytes { get; set; } = 20_000_000;

    /// <summary>Large target; 500 MB fits a Nitro account (Nitro Basic is 50 MB, Nitro up to 1 GB).</summary>
    public long LargeTargetBytes { get; set; } = 500_000_000;

    /// <summary>Hold a compress job back while a game is in the foreground (it costs frames).</summary>
    public bool OnlyWhenNoGame { get; set; } = false;
}
