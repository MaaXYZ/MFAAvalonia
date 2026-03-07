namespace MFAAvalonia.Helper.ValueType;

public sealed class EmptyDevicePlaceholder
{
    public EmptyDevicePlaceholder(string selectionText, string listText)
    {
        SelectionText = selectionText;
        ListText = listText;
    }

    public string SelectionText { get; }

    public string ListText { get; }

    public override string ToString() => SelectionText;
}
