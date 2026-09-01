using Dalamud.Configuration;

namespace WardrobeManager;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 10;
    public bool ConfirmBeforeApply { get; set; } = true;
    public string SelfieFolder { get; set; } = string.Empty;
    public float SelfieGuideHeightRatio { get; set; } = 0.76f;
    public bool GlamourerInitialImportCompleted { get; set; }
    public bool GlamourerClassificationCompleted { get; set; }
    public bool GlamourerFolderImportCompleted { get; set; }
    public bool ReloadGlamourerAfterFolderDelete { get; set; }
    public string LastAcknowledgedDevelopmentVersion { get; set; } = string.Empty;
}
