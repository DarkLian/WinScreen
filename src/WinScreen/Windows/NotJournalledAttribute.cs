namespace WinScreen.Windows;

/// <summary>
/// Marks a piece of a changed window's state that is deliberately left out of the change journal, and says why.
/// Everything else is written to the journal and read back after a crash; a test checks both directions.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class NotJournalledAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
