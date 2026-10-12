namespace HazukiPdf.Imaging;

/// <summary>Downsamples images by area averaging, which avoids the aliasing of nearest-neighbour sampling.</summary>
internal static class ImageResampler
{
    public static byte[] Downsample(byte[] pixels, int width, int height, int components, int newWidth, int newHeight)
    {
        var output = new byte[newWidth * newHeight * components];
        double scaleX = (double)width / newWidth;
        double scaleY = (double)height / newHeight;
        var sums = new double[components];
        for (int ty = 0; ty < newHeight; ty++)
        {
            double fy0 = ty * scaleY;
            double fy1 = Math.Min(height, (ty + 1) * scaleY);
            int y0 = (int)fy0;
            int y1 = Math.Min(height, (int)Math.Ceiling(fy1));
            for (int tx = 0; tx < newWidth; tx++)
            {
                double fx0 = tx * scaleX;
                double fx1 = Math.Min(width, (tx + 1) * scaleX);
                int x0 = (int)fx0;
                int x1 = Math.Min(width, (int)Math.Ceiling(fx1));
                Array.Clear(sums);
                double totalWeight = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    double wy = Math.Min(sy + 1, fy1) - Math.Max(sy, fy0);
                    if (wy <= 0)
                    {
                        continue;
                    }

                    int row = sy * width;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        double wx = Math.Min(sx + 1, fx1) - Math.Max(sx, fx0);
                        if (wx <= 0)
                        {
                            continue;
                        }

                        double w = wx * wy;
                        int o = (row + sx) * components;
                        for (int c = 0; c < components; c++)
                        {
                            sums[c] += pixels[o + c] * w;
                        }

                        totalWeight += w;
                    }
                }

                int t = ((ty * newWidth) + tx) * components;
                for (int c = 0; c < components; c++)
                {
                    output[t + c] = totalWeight > 0 ? (byte)Math.Clamp((int)Math.Round(sums[c] / totalWeight), 0, 255) : (byte)0;
                }
            }
        }

        return output;
    }
}
