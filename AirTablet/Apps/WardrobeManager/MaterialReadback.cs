using TerraFX.Interop.DirectX;

namespace WardrobeManager;

// Owns only a tiny staging texture, never a game texture or a native actor.
// Polling is non-blocking; a busy GPU is retried on a later UI frame.
internal sealed unsafe class MaterialReadback : IDisposable
{
    private ID3D11Texture2D* staging;
    private ID3D11DeviceContext* context;
    private uint width;
    private uint height;
    private DateTime deadline;
    public bool Pending => staging is not null;

    public bool Begin(nint textureAddress)
    {
        Dispose();
        if (textureAddress == 0) return false;
        var texture = (ID3D11Texture2D*)textureAddress;
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT || desc.Width != 8
            || desc.Height != 32 || desc.ArraySize != 1 || desc.MipLevels != 1 || desc.SampleDesc.Count != 1) return false;
        ID3D11Device* device = null;
        try
        {
            texture->GetDevice(&device);
            if (device is null) return false;
            ID3D11DeviceContext* acquired = null;
            device->GetImmediateContext(&acquired);
            context = acquired;
            if (context is null) return false;
            width = desc.Width;
            height = desc.Height;
            desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            desc.MiscFlags = 0;
            ID3D11Texture2D* created = null;
            var result = device->CreateTexture2D(&desc, null, &created);
            staging = created;
            if (result.FAILED || staging is null) { Dispose(); return false; }
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);
            deadline = DateTime.UtcNow.AddSeconds(2);
            return true;
        }
        finally { if (device is not null) device->Release(); }
    }

    public Half[]? Poll()
    {
        if (!Pending) return null;
        if (DateTime.UtcNow > deadline) { Dispose(); return null; }
        D3D11_MAPPED_SUBRESOURCE mapped;
        var result = context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ,
            (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (result.FAILED)
        {
            if (result.Value != unchecked((int)0x887A000A)) Dispose(); // DXGI_ERROR_WAS_STILL_DRAWING
            return null;
        }
        try
        {
            var rowElements = checked((int)width * 4);
            if (mapped.pData is null || mapped.RowPitch < rowElements * sizeof(Half)) return null;
            var values = new Half[checked(rowElements * (int)height)];
            for (var row = 0; row < height; row++)
                new ReadOnlySpan<Half>((byte*)mapped.pData + row * mapped.RowPitch, rowElements)
                    .CopyTo(values.AsSpan(row * rowElements, rowElements));
            return values;
        }
        finally
        {
            context->Unmap((ID3D11Resource*)staging, 0);
            Dispose();
        }
    }

    public void Dispose()
    {
        if (staging is not null) { staging->Release(); staging = null; }
        if (context is not null) { context->Release(); context = null; }
    }
}
