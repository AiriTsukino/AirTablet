using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace WardrobeManager;

internal sealed unsafe class MaterialHoverPreview(Func<uint, nint> locateSlot) : IDisposable
{
    private Texture* original;
    private Texture* preview;
    private uint activeKey;
    private int activeRow;
    private DateTime expires;
    private bool requested;

    public void BeginFrame() => requested = false;
    public void EndFrame() { if (!requested) Dispose(); }
    public void Tick() { if (preview is not null && DateTime.UtcNow > expires) Dispose(); }

    public bool Show(uint key, int row, Half[] captured, nint capturedTexture)
    {
        if (row is < 0 or >= 32 || captured.Length != 1024) return false;
        var slot = (Texture**)locateSlot(key);
        if (slot is null) { Dispose(); return false; }
        if (preview is not null && key == activeKey && row == activeRow && *slot == preview)
        { requested = true; expires = DateTime.UtcNow.AddMilliseconds(150); return true; }
        Dispose();
        slot = (Texture**)locateSlot(key);
        if (slot is null || *slot is null || (nint)(*slot)->D3D11Texture2D != capturedTexture) return false;
        var colors = MaterialRowDraft.HighlightRow(captured, row);
        var created = Texture.CreateTexture2D(8, 32, 1, TextureFormat.R16G16B16A16_FLOAT,
            TextureFlags.TextureType2D | TextureFlags.Managed | TextureFlags.Immutable, 7);
        if (created is null) return false;
        fixed (Half* data = colors)
        {
            if (!created->InitializeContents(data)) { created->DecRef(); return false; }
        }
        original = *slot;
        original->IncRef(); // Own a restoration reference independent of the slot.
        preview = created;  // Own the creation reference independently of the slot.
        preview->IncRef();
        *slot = preview;
        original->DecRef(); // Release the replaced slot's reference.
        activeKey = key;
        activeRow = row;
        requested = true;
        expires = DateTime.UtcNow.AddMilliseconds(150);
        return true;
    }

    public void Dispose()
    {
        if (preview is null) return;
        var slot = (Texture**)locateSlot(activeKey);
        // A redraw or another plugin may have replaced the texture. Never
        // overwrite its newer value with this preview's older snapshot.
        if (slot is not null && *slot == preview)
        {
            original->IncRef();
            *slot = original;
            preview->DecRef();
        }
        preview->DecRef();
        original->DecRef();
        preview = original = null;
        requested = false;
    }
}
