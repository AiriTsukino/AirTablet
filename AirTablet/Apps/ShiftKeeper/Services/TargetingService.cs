using Dalamud.Game.ClientState.Objects.SubKinds;
using ShiftKeeper.Models;

namespace ShiftKeeper.Services;

public sealed class TargetingService
{
    public string LastStatus { get; private set; } = string.Empty;

    public bool TryGetCurrentTarget(out string name, out string world)
    {
        name = string.Empty;
        world = string.Empty;
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter player)
        {
            LastStatus = "Target a player character before adding targeted staff.";
            return false;
        }

        name = player.Name.ToString().Trim();
        try { world = player.HomeWorld.Value.Name.ToString().Trim(); }
        catch { world = string.Empty; }

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world))
        {
            LastStatus = $"Selected {name}@{world}.";
            return true;
        }

        LastStatus = "The targeted player's name or home world could not be read.";
        return false;
    }

    public bool Target(StaffMember member)
    {
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (!pc.Name.ToString().Equals(member.Name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            string world;
            try { world = pc.HomeWorld.Value.Name.ToString(); }
            catch { continue; }
            if (!world.Equals(member.World.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            DalamudServices.TargetManager.Target = pc;
            LastStatus = $"Targeted {member.TellRecipient}.";
            return true;
        }
        LastStatus = $"{member.TellRecipient} is not currently visible or targetable.";
        DalamudServices.ChatGui.PrintError(LastStatus, "ShiftKeeper");
        return false;
    }
}
