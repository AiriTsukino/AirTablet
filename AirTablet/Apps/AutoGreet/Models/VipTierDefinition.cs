namespace AutoGreet.Models;

[Serializable]
public sealed class VipTierDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "VIP";
}
