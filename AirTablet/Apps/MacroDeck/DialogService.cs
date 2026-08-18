using System.Windows.Forms;

namespace MacroDeck;

internal sealed class DialogService : IDisposable
{
    public void SaveProfile(string name, Action<string> selected)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export MacroDeck venue profile",
            Filter = "MacroDeck venue profile (*.macrodeck.json)|*.macrodeck.json|JSON file (*.json)|*.json|All files (*.*)|*.*",
            FileName = Sanitize(name) + ".macrodeck.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            DefaultExt = "json",
            OverwritePrompt = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
            selected(dialog.FileName);
    }

    public void ImportProfile(Action<string> selected)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import MacroDeck venue profile",
            Filter = "MacroDeck and JSON profiles (*.macrodeck.json;*.json)|*.macrodeck.json;*.json|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
            selected(dialog.FileName);
    }

    public void PickImage(Action<string> selected)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a MacroDeck button image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
            selected(dialog.FileName);
    }

    private static string Sanitize(string name) => string.Concat(
        name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    public void Dispose() { }
}
