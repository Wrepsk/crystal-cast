using SharpDX.Direct3D11;

namespace CrystalCast;

internal static class TextureSampleDiagnostics
{
    public static unsafe string SampleBgraTexture(DeviceContext context, Texture2D texture, string label)
    {
        var description = texture.Description;
        var width = description.Width;
        var height = description.Height;
        var dataBox = context.MapSubresource(texture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            const int grid = 8;
            var sampleCount = grid * grid;
            var sourceStart = (byte*)dataBox.DataPointer;
            var nonBlack = 0;
            var visibleAlpha = 0;
            var totalLuma = 0.0;
            var totalAlpha = 0.0;
            for (var y = 0; y < grid; y++)
            {
                var sampleY = Math.Min(height - 1, (height * ((y * 2) + 1)) / (grid * 2));
                for (var x = 0; x < grid; x++)
                {
                    var sampleX = Math.Min(width - 1, (width * ((x * 2) + 1)) / (grid * 2));
                    var pixel = sourceStart + (sampleY * dataBox.RowPitch) + (sampleX * 4);
                    var blue = pixel[0];
                    var green = pixel[1];
                    var red = pixel[2];
                    var alpha = pixel[3];
                    var luma = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
                    totalLuma += luma;
                    totalAlpha += alpha;
                    if (red + green + blue > 16)
                        nonBlack++;
                    if (alpha > 16)
                        visibleAlpha++;
                }
            }

            return $"{label} rgb {nonBlack}/{sampleCount} y{totalLuma / sampleCount:0}, a{visibleAlpha}/{sampleCount} avg{totalAlpha / sampleCount:0}";
        }
        finally
        {
            context.UnmapSubresource(texture, 0);
        }
    }
}
