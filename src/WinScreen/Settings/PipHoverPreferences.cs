namespace WinScreen.Settings;

public sealed record PipHoverPreferences
{
    // With this on, the switches below are not read.
    public bool ChooseForMe { get; init; } = true;

    public bool Browsers { get; init; }
    public bool Firefox { get; init; }

    public bool Electron { get; init; }
    public bool FileExplorer { get; init; }

    // Only these and the kinds above ever get the helper.
    public List<string> Programs { get; init; } = [];

    internal PipHoverPreferences Validated() => this with
    {
        Programs = (Programs ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };
}
