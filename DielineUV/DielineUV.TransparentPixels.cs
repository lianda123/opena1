using System;

namespace DielineUV
{
    // Raster coverage, independent of Rhino/Eto. Alpha is opacity: 255 is opaque.
    internal static class TransparentPixels
    {
        // Rhino's transparency texture is an opacity mask: white keeps the surface.
        // The global transparency must stay zero or even white mask pixels turn into glass.
        internal const double MaterialTransparency = 0.0;

        // Color textures are opaque RGB. Transparency goes only into a separate map.
        internal static int ColorPixel(int composite)
        {
            return unchecked((int)0xff000000) | (composite & 0x00ffffff);
        }

        internal static int OpacityPixel(int composite)
        {
            int amount = (int)((uint)composite >> 24);
            return unchecked((int)0xff000000) | (amount << 16) | (amount << 8) | amount;
        }

        internal static int BasePixel(int tint, double transparency)
        {
            double amount = Math.Max(0, Math.Min(1, transparency));
            int alpha = (int)Math.Round(255 * (1 - amount));
            return (alpha << 24) | (tint & 0x00ffffff);
        }

        internal static int Composite(int artwork, int tint, double transparency, bool whiteBackground)
        {
            int alpha = (int)((uint)artwork >> 24);
            int red = (artwork >> 16) & 255, green = (artwork >> 8) & 255, blue = artwork & 255;
            if (whiteBackground && Math.Min(red, Math.Min(green, blue)) >= 250) alpha = 0;
            if (alpha == 0) return BasePixel(tint, transparency);
            if (alpha == 255) return unchecked((int)0xff000000) | (artwork & 0x00ffffff);
            double coverage = alpha / 255.0;
            double baseOpacity = 1 - Math.Max(0, Math.Min(1, transparency));
            double opacity = coverage + (1 - coverage) * baseOpacity;
            double remaining = (1 - coverage) * baseOpacity;
            int r = (int)Math.Round((red * coverage + ((tint >> 16) & 255) * remaining) / opacity);
            int g = (int)Math.Round((green * coverage + ((tint >> 8) & 255) * remaining) / opacity);
            int b = (int)Math.Round((blue * coverage + (tint & 255) * remaining) / opacity);
            return ((int)Math.Round(opacity * 255) << 24) | (r << 16) | (g << 8) | b;
        }
    }
}

