using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Dalamud.Plugin;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.IID;

namespace WardrobeManager;

internal static unsafe class DirectGameCapture
{
    private static readonly Func<nint>? GetSwapChain = CreateSwapChainGetter();

    public static bool TryCapture(NormalizedCrop crop, string outputDirectory, string fileIdentity, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        ID3D11Texture2D* backBuffer = null;
        ID3D11Texture2D* resolved = null;
        ID3D11Texture2D* staging = null;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        var mapped = false;
        try
        {
            var address = GetSwapChain?.Invoke() ?? 0;
            if (address == 0) throw new InvalidOperationException("The game render surface is not available.");
            var swapChain = (IDXGISwapChain*)address;
            var iid = IID_ID3D11Texture2D;
            void* resource = null;
            var result = swapChain->GetBuffer(0, &iid, &resource);
            if (result.FAILED || resource is null) throw new InvalidOperationException($"Could not access the game frame (0x{result.Value:X8}).");
            backBuffer = (ID3D11Texture2D*)resource;
            backBuffer->GetDevice(&device);
            if (device is null) throw new InvalidOperationException("The DirectX device is not available.");
            device->GetImmediateContext(&context);
            if (context is null) throw new InvalidOperationException("The DirectX context is not available.");

            D3D11_TEXTURE2D_DESC sourceDesc;
            backBuffer->GetDesc(&sourceDesc);
            var copySource = backBuffer;
            if (sourceDesc.SampleDesc.Count > 1)
            {
                var resolveDesc = sourceDesc;
                resolveDesc.SampleDesc.Count = 1;
                resolveDesc.SampleDesc.Quality = 0;
                resolveDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
                resolveDesc.BindFlags = 0;
                resolveDesc.CPUAccessFlags = 0;
                resolveDesc.MiscFlags = 0;
                result = device->CreateTexture2D(&resolveDesc, null, &resolved);
                if (result.FAILED || resolved is null) throw new InvalidOperationException($"Could not prepare the game frame (0x{result.Value:X8}).");
                context->ResolveSubresource((ID3D11Resource*)resolved, 0, (ID3D11Resource*)backBuffer, 0, sourceDesc.Format);
                copySource = resolved;
                sourceDesc = resolveDesc;
            }

            var stagingDesc = sourceDesc;
            stagingDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;
            result = device->CreateTexture2D(&stagingDesc, null, &staging);
            if (result.FAILED || staging is null) throw new InvalidOperationException($"Could not create the selfie readback surface (0x{result.Value:X8}).");
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)copySource);

            D3D11_MAPPED_SUBRESOURCE data;
            result = context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &data);
            if (result.FAILED || data.pData is null) throw new InvalidOperationException($"Could not read the game frame (0x{result.Value:X8}).");
            mapped = true;

            using var frame = ConvertFrame(data, stagingDesc);
            var requested = crop.ToPixels(frame.Width, frame.Height);
            var portrait = FitPortrait(requested, frame.Width, frame.Height);
            using var cropped = frame.Clone(portrait, PixelFormat.Format32bppArgb);
            Directory.CreateDirectory(outputDirectory);
            var safeIdentity = PersistenceService.ImageKey(fileIdentity);
            path = Path.Combine(outputDirectory, $"WardrobeSelfie-{safeIdentity}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            cropped.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DalamudServices.Log.Warning(ex, "WardrobeManager direct game-frame capture failed.");
            return false;
        }
        finally
        {
            if (mapped && context is not null && staging is not null) context->Unmap((ID3D11Resource*)staging, 0);
            if (staging is not null) staging->Release();
            if (resolved is not null) resolved->Release();
            if (context is not null) context->Release();
            if (device is not null) device->Release();
            if (backBuffer is not null) backBuffer->Release();
        }
    }

    private static Bitmap ConvertFrame(D3D11_MAPPED_SUBRESOURCE data, D3D11_TEXTURE2D_DESC desc)
    {
        var width = checked((int)desc.Width);
        var height = checked((int)desc.Height);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                var source = (byte*)data.pData + y * data.RowPitch;
                var destination = (byte*)locked.Scan0 + y * locked.Stride;
                for (var x = 0; x < width; x++) WritePixel(source, destination, x, desc.Format);
            }
        }
        catch
        {
            bitmap.UnlockBits(locked);
            bitmap.Dispose();
            throw;
        }
        bitmap.UnlockBits(locked);
        return bitmap;
    }

    private static void WritePixel(byte* source, byte* destination, int x, DXGI_FORMAT format)
    {
        var target = destination + x * 4;
        switch (format)
        {
            case DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                var bgra = source + x * 4;
                target[0] = bgra[0]; target[1] = bgra[1]; target[2] = bgra[2]; target[3] = 255;
                return;
            case DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                var rgba = source + x * 4;
                target[0] = rgba[2]; target[1] = rgba[1]; target[2] = rgba[0]; target[3] = 255;
                return;
            case DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM:
                var packed = ((uint*)source)[x];
                target[2] = (byte)(((packed >> 0) & 0x3FF) * 255 / 1023);
                target[1] = (byte)(((packed >> 10) & 0x3FF) * 255 / 1023);
                target[0] = (byte)(((packed >> 20) & 0x3FF) * 255 / 1023);
                target[3] = 255;
                return;
            case DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT:
                var half = (ushort*)(source + x * 8);
                target[2] = FloatToByte((float)BitConverter.UInt16BitsToHalf(half[0]));
                target[1] = FloatToByte((float)BitConverter.UInt16BitsToHalf(half[1]));
                target[0] = FloatToByte((float)BitConverter.UInt16BitsToHalf(half[2]));
                target[3] = 255;
                return;
            default:
                throw new InvalidOperationException($"The current game color format ({format}) is not supported for selfies.");
        }
    }

    private static byte FloatToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(MathF.Pow(Math.Clamp(value, 0f, 1f), 1f / 2.2f) * 255f), 0, 255);

    private static Rectangle FitPortrait(Rectangle area, int width, int height)
    {
        var x = Math.Clamp(area.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(area.Y, 0, Math.Max(0, height - 1));
        var w = Math.Clamp(area.Width, 1, width - x);
        var h = Math.Clamp(area.Height, 1, height - y);
        const float aspect = 9f / 16f;
        if (w / (float)h > aspect) { var fitted = Math.Max(1, (int)MathF.Round(h * aspect)); x += (w - fitted) / 2; w = fitted; }
        else { var fitted = Math.Max(1, (int)MathF.Round(w / aspect)); y += (h - fitted) / 2; h = fitted; }
        return new Rectangle(x, y, w, h);
    }

    private static Func<nint>? CreateSwapChainGetter()
    {
        try
        {
            var type = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Interface.Internal.SwapChainHelper");
            var getter = type?.GetProperty("GameDeviceSwapChain", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetMethod;
            if (getter is null) return null;
            var dynamic = new DynamicMethod("WardrobeGetGameSwapChain", typeof(nint), Type.EmptyTypes, typeof(DirectGameCapture), true);
            var il = dynamic.GetILGenerator();
            il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ret);
            return dynamic.CreateDelegate<Func<nint>>();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "WardrobeManager could not initialize direct game-frame capture.");
            return null;
        }
    }
}
