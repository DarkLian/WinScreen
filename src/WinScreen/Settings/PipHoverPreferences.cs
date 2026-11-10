namespace WinScreen.Settings;

/// <summary>Which programs' views start with Enhanced hover on. Each view can still be changed from its own menu.</summary>
public sealed record PipHoverPreferences
{
    /// <summary>Chrome and the browsers built on it.</summary>
    public bool Browsers { get; init; }
    public bool Firefox { get; init; }

    /// <summary>Apps built with Electron, such as code editors and chat apps.</summary>
    public bool Electron { get; init; }
    public bool FileExplorer { get; init; }

    /// <summary>Any other program, by name. Only these and the kinds above ever get the helper.</summary>
    public List<string> Programs { get; init; } = [];

    internal PipHoverPreferences Validated() => this with
    {
        Programs = (Programs ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };
}
