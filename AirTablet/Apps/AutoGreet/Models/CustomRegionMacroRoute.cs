
namespace AutoGreet.Models;

[Serializable]
public sealed class CustomRegionMacroRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Region macro";
    public Guid RegionId { get; set; }
    public Guid MacroId { get; set; }
    public bool Enabled { get; set; } = true;
}
