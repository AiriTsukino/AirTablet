namespace AutoGreet.Models;

public enum GreetingCategory
{
    FirstTime,
    Returning,
    Vip,
    Blacklisted
}

[Serializable]
public sealed class GreetingMacro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Greeting";
    public GreetingCategory Category { get; set; } = GreetingCategory.FirstTime;
    public string Script { get; set; } = "/dote <t>\n/tell <t> Welcome to the venue!";
    public bool Enabled { get; set; } = true;
}

[Serializable]
public sealed class GreetingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public List<GreetingMacro> Macros { get; set; } = [];

    public static GreetingProfile CreateDefault()
    {
        return new GreetingProfile
        {
            Name = "Default",
            Macros =
            [
                new GreetingMacro
                {
                    Name = "First-time welcome",
                    Category = GreetingCategory.FirstTime,
                    Script = "/dote <t>\n/tell <t> Welcome to the venue!\n/wait 1\n/tell <t> If you need anything, ask staff!"
                },
                new GreetingMacro
                {
                    Name = "Returning welcome",
                    Category = GreetingCategory.Returning,
                    Script = "/dote <t>\n/tell <t> Welcome back to the venue!"
                },
                new GreetingMacro
                {
                    Name = "VIP welcome",
                    Category = GreetingCategory.Vip,
                    Script = "/dote <t>\n/tell <t> Welcome back, VIP! We are glad to see you."
                }
            ]
        };
    }
}
