namespace SpaceTestPC.App.Services;

public sealed class ScannerService : IScannerService
{
    public string Normalize(string rawInput)
    {
        return rawInput.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
