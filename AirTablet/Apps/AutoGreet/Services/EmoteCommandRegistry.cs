namespace AutoGreet.Services;

internal static class EmoteCommandRegistry
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "aback", "adventoflight", "advent", "airquotes", "allsaintscharm", "angry", "backflip", "bflip", "bstance", "battlestance",
        "beckon", "blowbubbles", "blowkiss", "blownaway", "blush", "bow", "breakfast", "bread", "breakdance", "breaking",
        "cheer", "chuckle", "clap", "clutchhead", "comfort", "congratulate", "consider", "hmm", "converse", "crimsonlotus",
        "cry", "dance", "delighted", "delight", "deny", "deride", "pagaga", "determined", "disappointed", "divinearm",
        "divinedisk", "divinetiara", "dote", "doubt", "doze", "draw", "drinkgreentea", "greentea", "tea", "earwiggle",
        "easternbow", "ebow", "easterngreeting", "egreeting", "estretch", "easternstretch", "eatapple", "apple", "eatchicken", "eatchocolate",
        "choco", "eategg", "egg", "eatpizza", "pizza", "eatpumpkincookie", "cookie", "eatriceball", "riceball", "eattaco",
        "taco", "elucidate", "embrace", "eureka", "examineself", "facepalm", "fistbump", "brofist", "fist", "fistpump",
        "gcsalute", "grandcompanysalute", "flex", "flowershower", "petals", "frighten", "fryegg", "fume", "furious", "goodbye",
        "gratuity", "makeithail", "greet", "gridaniansip", "grovel", "handover", "handtoheart", "happy", "haurchefant", "hknight",
        "headache", "highfive", "hifive", "hug", "huh", "humbletriumph", "waitforit", "huzzah", "hurray", "imperialsalute",
        "insist", "joy", "jumpforjoy1", "jj1", "jumpforjoy2", "jj2", "jumpforjoy3", "jj3", "jumpforjoy4", "jj4",
        "jumpforjoy5", "jj5", "kneel", "laliho", "laugh", "limberup", "limber", "linkpearl", "littleladiesdance", "ladance",
        "lominsansip", "lookout", "magictrick", "me", "no", "ohokaliy", "overreact", "paintblack", "paintblue", "paintred",
        "paintyellow", "panic", "pantomime", "mime", "respect", "pet", "stroke", "photograph", "point", "poke",
        "pose", "unbound", "poseoftheunbound", "powerup", "pray", "prettyplease", "pplease", "psych", "rally", "read",
        "reference", "runwaywalk", "runway", "salute", "sabotender", "sheathe", "shocked", "shrug", "shush", "shh",
        "slap", "snap", "soothe", "spectacles", "splash", "stagger", "standup", "stomp", "stretch", "sulk",
        "surprised", "think", "throw", "thumbsup", "toast", "tomestone", "twirl", "uchiwasshoi", "bigfan", "uldahnsip",
        "upset", "vexed", "vpose", "victorypose", "victoryreveal", "vreveal", "visage", "waterflip", "wave", "welcome",
        "wow", "yes",

        "atease", "attend", "attention", "balldance", "bdance", "beesknees", "blackrangerposea", "brpa", "blackrangerposeb", "brpb",
        "bombdance", "bouquet", "box", "boxstep", "breathcontrol", "cackle", "carrybook", "changepose", "cpose", "charmed",
        "cheerjumpgreen", "cheerjg", "cheerjumpindigo", "cheerji", "cheerjump", "cheerjumpred", "cheerjr", "cheerlightblue", "cheerlb", "cheerlightgreen",
        "cheerlg", "cheerlightyellow", "cheerly", "cheeronblue", "cheerob", "cheeronbright", "cheerow", "cheeronorange", "cheeroo", "cheerrhythmbright",
        "cheerrw", "cheerrhythmred", "cheerrr", "cheerrhythmviolet", "cheerrv", "cheerwavepink", "cheerwp", "cheerwaveviolet", "cheerwv", "cheerwave",
        "cheerwaveyellow", "cheerwy", "conduct", "confirm", "dazed", "devourtaco", "iceheart", "edance", "easterndance", "flamedance",
        "getfantasy", "golddance", "gdance", "goobbuedo", "mysterymachine", "gridaniangulp", "guard", "harvestdance", "hdance", "heeltoe",
        "hum", "lalihop", "lean", "lominsangulp", "lophop", "loveheart", "heart", "malevolence", "mandervilledance", "mdance",
        "mmambo", "mandervillemambo", "megaflare", "mogdance", "moonlift", "hildy", "hildibrand", "pen", "playdead", "pdead",
        "popotostep", "pushups", "rage", "redrangerposea", "rrpa", "redrangerposeb", "rrpb", "reprimand", "ritualprayer", "savortea",
        "scheme", "shakedrink", "shiver", "showleft", "showright", "sidestep", "simulationf", "simulationm", "sit", "lounge",
        "groundsit", "situps", "slump", "songbird", "spirit", "squats", "stepdance", "sdance", "study", "sundering",
        "exodus", "sundance", "sundropdance", "sweat", "broom", "sweep", "thavdance", "tdance", "tomescroll", "tremble",
        "uldahngulp", "ultima", "visor", "wasshoi", "water", "waterfloat", "winded", "wringhands", "yellowrangerposea", "yrpa",
        "yellowrangerposeb", "yrpb", "yoldance", "zantetsuken", "ztk",

        "alert", "amazed", "awe", "annoyed", "annoy", "beam", "biggrin", "concentrate", "disturbed", "content",
        "endure", "fakesmile", "furrow", "grin", "ouch", "ow", "ponder", "makeyougohmmm", "puckerup", "reflect",
        "sad", "scared", "fear", "scoff", "shuteyes", "shut", "simper", "smile", "smirk", "sneer",
        "straightface", "straight", "taunt", "leftwink", "wink", "rightwink", "worried", "worry",

        "emote", "em"
    };

    public static IReadOnlyList<string> SupportedCommands => Commands.OrderBy(command => command, StringComparer.OrdinalIgnoreCase).Select(command => "/" + command).ToList();

    public static string SupportedCommandsText => string.Join("\n", SupportedCommands);

    public static bool IsSupportedEmoteLine(string line)
        => TryGetCommandName(line, out var command) && Commands.Contains(command);

    public static bool RequiresVisibleTarget(string line)
    {
        if (!TryGetCommandName(line, out var command))
            return false;

        return command.Equals("dote", StringComparison.OrdinalIgnoreCase)
               || line.Contains("<t>", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetCommandName(string line, out string command)
    {
        command = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return false;

        var end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
            end++;

        command = trimmed[1..end].Trim();
        return !string.IsNullOrWhiteSpace(command);
    }
}
