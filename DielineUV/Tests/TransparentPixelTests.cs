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
        Console.WriteLine("Transparent artwork regression checks passed: " + checks);
    }
}
