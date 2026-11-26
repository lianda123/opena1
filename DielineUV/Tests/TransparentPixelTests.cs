using System;
using DielineUV;

internal static class TransparentPixelTests
{
    internal static void Run()
    {
        int checks = 0;
        Action<bool, string> check = (condition, message) => { checks++; if (!condition) throw new Exception(message); };
        int tint = unchecked((int)0xff3377bb), art = unchecked((int)0xffab4512);
        for (int amount = 0; amount <= 100; amount++)
        {
            double transparency = amount / 100.0;
            int printed = TransparentPixels.Composite(art, tint, transparency, false);
            check(printed == art, "solid artwork became transparent or tinted");
            int blank = TransparentPixels.Composite(0, tint, transparency, false);
            check(((uint)blank >> 24) == Math.Round(255 * (1 - transparency)), "blank opacity wrong");
            check((blank & 0x00ffffff) == (tint & 0x00ffffff), "blank lost the shared base tint");
            int white = TransparentPixels.Composite(unchecked((int)0xffffffff), tint, transparency, true);
            check(white == blank, "white background was printed");
            int whiteInk = TransparentPixels.Composite(unchecked((int)0xffffffff), tint, transparency, false);
            check(whiteInk == unchecked((int)0xffffffff), "white ink was lost in alpha mode");
        }
        int edge = TransparentPixels.Composite(unchecked((int)0x80ab4512), tint, 1, false);
        check(((uint)edge >> 24) == 128, "antialias coverage wrong");
        check((edge & 0x00ffffff) == (art & 0x00ffffff), "transparent edge picked up a tint halo");
        check(TransparentPixels.BasePixel(tint, 0) == tint, "zero transparency wrong");
        check(((uint)TransparentPixels.BasePixel(tint, 1) >> 24) == 0, "full transparency wrong");
        int previousMask = 256;
        for (int amount = 0; amount <= 100; amount++)
        {
            int blank = TransparentPixels.Composite(0, tint, amount / 100.0, false);
            int color = TransparentPixels.ColorPixel(blank);
            int mask = TransparentPixels.OpacityPixel(blank);
            int value = mask & 255;
            check(color == tint, "increasing transparency faded the color map toward white");
            check((uint)mask >> 24 == 255, "transparency map itself has alpha");
            check(((mask >> 16) & 255) == value && ((mask >> 8) & 255) == value, "mask is not neutral gray");
            check(value == (int)((uint)blank >> 24), "transparency mask polarity is reversed");
            check(value <= previousMask, "higher transparency produced a more opaque mask");
            previousMask = value;
            int printed = TransparentPixels.Composite(art, tint, amount / 100.0, false);
            check(TransparentPixels.ColorPixel(printed) == art, "opaque artwork faded in the color map");
            check(TransparentPixels.OpacityPixel(printed) == unchecked((int)0xffffffff), "artwork gained transparency");
            int whiteBackground = TransparentPixels.Composite(unchecked((int)0xffffffff), tint, amount / 100.0, true);
            check(TransparentPixels.OpacityPixel(whiteBackground) == mask, "white-background mask differs from transparent PNG");
            int whiteInk = TransparentPixels.Composite(unchecked((int)0xffffffff), tint, amount / 100.0, false);
            check(TransparentPixels.ColorPixel(whiteInk) == unchecked((int)0xffffffff) &&
                TransparentPixels.OpacityPixel(whiteInk) == unchecked((int)0xffffffff), "white artwork was made transparent");
        }
        for (int coverage = 0; coverage <= 255; coverage++)
        {
            int pixel = TransparentPixels.Composite((art & 0x00ffffff) | (coverage << 24), tint, 1, false);
            int mask = TransparentPixels.OpacityPixel(pixel) & 255;
            check(mask == coverage, "mask lost antialias coverage");
            check((uint)TransparentPixels.ColorPixel(pixel) >> 24 == 255, "color texture must be fully opaque RGB");
            if (coverage != 0)
                check((TransparentPixels.ColorPixel(pixel) & 0x00ffffff) == (art & 0x00ffffff), "edge color gained a white halo");
        }
        int fullBlank = TransparentPixels.Composite(0, tint, 1, false);
        check(TransparentPixels.OpacityPixel(fullBlank) == unchecked((int)0xff000000), "100 percent transparency must use a black opacity mask");
        int opaqueBlank = TransparentPixels.Composite(0, tint, 0, false);
        check(TransparentPixels.OpacityPixel(opaqueBlank) == unchecked((int)0xffffffff), "0 percent transparency must use a white opacity mask");
        // Rhino 7's native material uses global transparency for glass separately from the
        // inverted-luminance transparency texture. Test the renderer-facing contract as well
        // as the PNG alpha arithmetic. Source: mcneel/RhinoCycles, rhino-7.0,
        // Shaders/RhinoFullNxt.cs (invert_luminence79 and custom_alpha_cutter116).
        check(TransparentPixels.MaterialTransparency == 0, "global glass transparency would hide opaque printed pixels");
        for (int amount = 0; amount <= 100; amount++)
        {
            int blank = TransparentPixels.Composite(0, tint, amount / 100.0, false);
            int blankGray = TransparentPixels.OpacityPixel(blank) & 255;
            double blankVisible = (1 - TransparentPixels.MaterialTransparency) * blankGray / 255.0;
            check(Math.Abs(blankVisible - (1 - amount / 100.0)) <= .5 / 255 + 1e-12,
                "Rhino's black-transparent/white-opaque convention disagrees with the requested percentage");
            int printed = TransparentPixels.Composite(art, tint, amount / 100.0, false);
            int printGray = TransparentPixels.OpacityPixel(printed) & 255;
            double printVisible = (1 - TransparentPixels.MaterialTransparency) * printGray / 255.0;
            check(printVisible == 1, "solid artwork disappears in the native material contract");
        }
        Console.WriteLine("Transparent artwork regression checks passed: " + checks);
    }
}

