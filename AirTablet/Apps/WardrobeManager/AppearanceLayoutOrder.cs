namespace WardrobeManager;

internal static class AppearanceLayoutOrder
{
    private static readonly string[] Equipment = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger", "MainHand", "OffHand", "Glasses", "Hat", "Visor", "Weapon", "VieraEars"];
    private static readonly string[] Customize = ["Gender", "Clan", "Race", "Height", "MuscleMass", "BustSize", "Face", "Hairstyle", "TailShape", "FacePaint", "FacialFeature1", "FacialFeature2", "FacialFeature3", "FacialFeature4", "FacialFeature5", "FacialFeature6", "FacialFeature7", "LegacyTattoo", "Eyebrows", "EyeShape", "Nose", "Jaw", "Mouth", "SkinColor", "TattooColor", "HairColor", "HighlightsColor", "EyeColorLeft", "EyeColorRight", "LipColor", "FacePaintColor", "Highlights", "SmallIris", "Lipstick", "FacePaintReversed", "Wetness"];
    private static readonly string[] Parameters = ["SkinDiffuse", "HairDiffuse", "HairHighlight", "LeftEye", "RightEye", "FeatureColor", "LipDiffuse", "DecalColor", "MuscleTone", "LeftLimbalIntensity", "RightLimbalIntensity", "FacePaintUvMultiplier", "FacePaintUvOffset"];

    public static int Rank(string section, string name)
    {
        var order = section switch { "Equipment" => Equipment, "Customize" => Customize, "Parameters" => Parameters, _ => [] };
        var index = Array.IndexOf(order, name);
        return index < 0 ? int.MaxValue : index;
    }
}
