using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private const string TransparentFinishKey = "DielineUV.TransparentFinish.v1";

        private static SideFinish ChooseTransparentFinish(RhinoObject first)
        {
            var previous = ReadTransparentFinish(first?.Attributes.GetUserString(TransparentFinishKey));
            var initial = previous ?? new SideFinish { Transparent = true, Tint = Color.FromArgb(220, 240, 250),
                Transparency = .65, BackgroundMode = "auto", Printing = "single" };
            using (var dialog = new Dialog<SideFinish> { Title = Title + "：透明件贴图", Resizable = false })
            {
                var printing = new DropDown();
                printing.Items.Add("单面图案"); printing.Items.Add("双面图案"); printing.Items.Add("单面并重选首件印刷侧");
                printing.SelectedIndex = first?.Attributes.GetUserString(PrintingModeKey) == "double" ? 1 : 0;
                var background = new DropDown();
                background.Items.Add("自动识别背景");
                background.Items.Add("透明背景 PNG（保留白色图案）");
                background.Items.Add("白底 PNG（白色区域不印刷）");
                background.SelectedIndex = initial.BackgroundMode == "alpha" ? 1 : initial.BackgroundMode == "white" ? 2 : 0;
                var color = new ColorPicker { AllowAlpha = false,
                    Value = Eto.Drawing.Color.FromArgb(initial.Tint.R, initial.Tint.G, initial.Tint.B) };
                int amount = (int)Math.Round(initial.Transparency * 100);
                var slider = new Slider { MinValue = 0, MaxValue = 100, Value = amount, Width = 250 };
                var number = new NumericStepper { MinValue = 0, MaxValue = 100, DecimalPlaces = 0, Value = amount, Width = 80 };
                slider.ValueChanged += (sender, args) => { if (number.Value != slider.Value) number.Value = slider.Value; };
                number.ValueChanged += (sender, args) => { int value = (int)Math.Round(number.Value); if (slider.Value != value) slider.Value = value; };
                var ok = new Button { Text = "确定" };
                var cancel = new Button { Text = "取消" };
                ok.Click += (sender, args) => dialog.Close(new SideFinish { Transparent = true,
                    Printing = printing.SelectedIndex == 1 ? "double" : printing.SelectedIndex == 2 ? "reselect" : "single",
                    Tint = Color.FromArgb(color.Value.Rb, color.Value.Gb, color.Value.Bb), Transparency = number.Value / 100,
                    BackgroundMode = background.SelectedIndex == 1 ? "alpha" : background.SelectedIndex == 2 ? "white" : "auto" });
                cancel.Click += (sender, args) => dialog.Close(null);
                dialog.DefaultButton = ok; dialog.AbortButton = cancel;
                var layout = new DynamicLayout { Padding = new Eto.Drawing.Padding(14), Spacing = new Eto.Drawing.Size(8, 10) };
                layout.AddSeparateRow(new Label { Text = "本刀模框统一设置：有图区域不透明，其余为透明底材。" });
                layout.AddRow(new Label { Text = "图案" }, printing);
                layout.AddRow(new Label { Text = "底色（两面及侧边统一）" }, color);
                layout.AddRow(new Label { Text = "无图区域透明度 %" }, slider, number);
                layout.AddSeparateRow(new Label { Text = "0 = 不透明；100 = 完全透明。图案实色区域不受此数值影响。" });
                layout.AddRow(new Label { Text = "图片背景" }, background);
                layout.AddSeparateRow(new Label { Text = "需要白色图案时，请使用透明背景 PNG。" });
                layout.AddSeparateRow(null, ok, cancel);
                dialog.Content = layout;
                return dialog.ShowModal(RhinoEtoApp.MainWindow);
            }
        }

        private static string WriteTransparentFinish(SideFinish finish)
        {
            if (!finish.Transparent) return null;
            return string.Join(";", finish.Tint.R.ToString(), finish.Tint.G.ToString(), finish.Tint.B.ToString(),
                finish.Transparency.ToString("R", CultureInfo.InvariantCulture), finish.BackgroundMode);
        }

        private static SideFinish ReadTransparentFinish(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var values = text.Split(';');
            int r, g, b; double amount;
            if (values.Length != 5 || !int.TryParse(values[0], out r) || !int.TryParse(values[1], out g) ||
                !int.TryParse(values[2], out b) || !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out amount) ||
                r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255 || double.IsNaN(amount) || amount < 0 || amount > 1)
                return null;
            return new SideFinish { Transparent = true, Tint = Color.FromArgb(r, g, b), Transparency = amount,
                BackgroundMode = values[4] == "white" ? "white" : values[4] == "alpha" ? "alpha" : "auto" };
        }

        private static TextureAtlas CreateTransparentAtlas(string image, SideFinish finish)
        {
            using (var artwork = new Eto.Drawing.Bitmap(image))
            {
                const int tile = 512, padding = 16;
                var atlas = new TextureAtlas { Width = artwork.Width + tile + 3 * padding,
                    Height = Math.Max(artwork.Height + 2 * padding, 2 * tile + 3 * padding),
                    Artwork = CacheTexture(image), Image = Path.Combine(TextureCache(), Guid.NewGuid().ToString("N") + ".png"),
                    Print = new AtlasRegion { X = padding, Y = padding, Width = artwork.Width, Height = artwork.Height },
                    Side = new AtlasRegion { X = artwork.Width + 2 * padding, Y = padding, Width = tile, Height = tile },
                    Back = new AtlasRegion { X = artwork.Width + 2 * padding, Y = tile + 2 * padding, Width = tile, Height = tile },
                    BackColor = true, SideColor = true, Alpha = true };
                using (var bitmap = new Eto.Drawing.Bitmap(atlas.Width, atlas.Height, Eto.Drawing.PixelFormat.Format32bppRgba))
                {
                    int tint = finish.Tint.ToArgb();
                    using (var input = artwork.Lock())
                    using (var output = bitmap.Lock())
                    {
                        var bottom = Eto.Drawing.Color.FromArgb(TransparentPixels.BasePixel(tint, finish.Transparency));
                        output.SetPixels(Enumerable.Repeat(bottom, checked(atlas.Width * atlas.Height)));
                        bool white = finish.BackgroundMode == "white";
                        if (finish.BackgroundMode == "auto")
                        {
                            white = true;
                            for (int y = 0; y < artwork.Height && white; y++)
                                for (int x = 0; x < artwork.Width; x++)
                                    if (input.GetPixel(x, y).Ab < 250) { white = false; break; }
                            RhinoApp.WriteLine(white ? "透明件：本张 PNG 按白色背景识别图案。" : "透明件：本张 PNG 按透明背景识别图案。");
                        }
                        for (int y = 0; y < artwork.Height; y++)
                            for (int x = 0; x < artwork.Width; x++)
                            {
                                int pixel = TransparentPixels.Composite(input.GetPixel(x, y).ToArgb(), tint, finish.Transparency, white);
                                output.SetPixel(atlas.Print.X + x, atlas.Print.Y + y, Eto.Drawing.Color.FromArgb(pixel));
                            }
                    }
                    bitmap.Save(atlas.Image, Eto.Drawing.ImageFormat.Png);
                }
                return atlas;
            }
        }
    }
}
