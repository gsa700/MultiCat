namespace MultiCat.Gui.ViewModels;

public sealed record TrafficEntry(string Time, string Body, string Note)
{
    public override string ToString() => $"{Time}  {Body}  · {Note}";
}
