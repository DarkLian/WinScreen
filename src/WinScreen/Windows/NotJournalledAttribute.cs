namespace WinScreen.Windows;

// Everything not marked is written to the journal and read back after a crash; a test checks both directions.
[AttributeUsage(AttributeTargets.Property)]
internal sealed class NotJournalledAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
