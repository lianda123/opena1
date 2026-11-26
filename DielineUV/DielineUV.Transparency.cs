using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Render;
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
                layout.AddSeparateRow(new Label { Text = "白底图请选“白底 PNG”；需保留白色图案时，请使用真正透明背景的 PNG。" });
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
                bool white;
                if (!ResolveTransparentBackground(artwork, finish, out white)) return null;
                const int tile = 512, padding = 16;
                var atlas = new TextureAtlas { Width = artwork.Width + tile + 3 * padding,
                    Height = Math.Max(artwork.Height + 2 * padding, 2 * tile + 3 * padding),
                    Artwork = CacheTexture(image), Image = Path.Combine(TextureCache(), Guid.NewGuid().ToString("N") + ".png"),
                    TransparencyImage = Path.Combine(TextureCache(), Guid.NewGuid().ToString("N") + "-opacity.png"),
                    Print = new AtlasRegion { X = padding, Y = padding, Width = artwork.Width, Height = artwork.Height },
                    Side = new AtlasRegion { X = artwork.Width + 2 * padding, Y = padding, Width = tile, Height = tile },
                    Back = new AtlasRegion { X = artwork.Width + 2 * padding, Y = tile + 2 * padding, Width = tile, Height = tile },
                    BackColor = true, SideColor = true };
                using (var bitmap = new Eto.Drawing.Bitmap(atlas.Width, atlas.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
                using (var mask = new Eto.Drawing.Bitmap(atlas.Width, atlas.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
                {
                    int tint = finish.Tint.ToArgb();
                    using (var input = artwork.Lock())
                    using (var output = bitmap.Lock())
                    using (var transparency = mask.Lock())
                    {
                        int bottom = TransparentPixels.BasePixel(tint, finish.Transparency);
                        output.SetPixels(Enumerable.Repeat(Eto.Drawing.Color.FromArgb(TransparentPixels.ColorPixel(bottom)),
                            checked(atlas.Width * atlas.Height)));
                        transparency.SetPixels(Enumerable.Repeat(Eto.Drawing.Color.FromArgb(TransparentPixels.OpacityPixel(bottom)),
                            checked(atlas.Width * atlas.Height)));
                        for (int y = 0; y < artwork.Height; y++)
                            for (int x = 0; x < artwork.Width; x++)
                            {
                                int pixel = TransparentPixels.Composite(input.GetPixel(x, y).ToArgb(), tint, finish.Transparency, white);
                                output.SetPixel(atlas.Print.X + x, atlas.Print.Y + y,
                                    Eto.Drawing.Color.FromArgb(TransparentPixels.ColorPixel(pixel)));
                                transparency.SetPixel(atlas.Print.X + x, atlas.Print.Y + y,
                                    Eto.Drawing.Color.FromArgb(TransparentPixels.OpacityPixel(pixel)));
                            }
                    }
                    bitmap.Save(atlas.Image, Eto.Drawing.ImageFormat.Png);
                    mask.Save(atlas.TransparencyImage, Eto.Drawing.ImageFormat.Png);
                }
                return atlas;
            }
        }

        private static bool ResolveTransparentBackground(Eto.Drawing.Bitmap artwork, SideFinish finish, out bool white)
        {
            white = finish.BackgroundMode == "white";
            if (white) return true;
            bool hasAlpha = false, hasWhite = false;
            using (var pixels = artwork.Lock())
                for (int y = 0; y < artwork.Height && !hasAlpha; y++)
                    for (int x = 0; x < artwork.Width; x++)
                    {
                        var pixel = pixels.GetPixel(x, y);
                        if (pixel.Ab < 250) { hasAlpha = true; break; }
                        if (Math.Min(pixel.Rb, Math.Min(pixel.Gb, pixel.Bb)) >= 250) hasWhite = true;
                    }
            if (finish.BackgroundMode == "auto") white = !hasAlpha;
            else if (!hasAlpha && hasWhite)
            {
                var choices = new[] { "去除白底，白色区域使用透明底材", "保留白色，作为不透明图案", "取消" };
                var choice = Dialogs.ShowListBox(Title,
                    "这张 PNG 没有透明背景。若保留白底，白色区域不会随透明度变透明。", choices) as string;
                if (choice == null || choice == choices[2]) return false;
                if (choice == choices[0]) { white = true; finish.BackgroundMode = "white"; }
            }
            RhinoApp.WriteLine(white ? "透明件：白底区域使用透明底材。" : "透明件：使用 PNG 透明背景，保留白色图案。");
            return true;
        }

        private static void AttachTransparencyMap(RhinoDoc doc, RenderMaterial material, string image, int channel)
        {
            var context = RenderContent.ChangeContexts.Program;
            var texture = CreateMappedTexture(doc, image, channel, true);
            bool attached = false;
            try
            {
                material.BeginChange(context);
                try
                {
                    // Rhino inverts this texture's luminance: white is opaque, black is transparent.
                    // Global transparency is separate and would also hide solid printed pixels.
                    if (!material.SetParameter("alpha-transparency", false) ||
                        !material.SetParameter("transparency", TransparentPixels.MaterialTransparency))
                        throw new InvalidOperationException("Rhino 无法保存透明件材质设置。");
                    var slot = material.TextureChildSlotName(RenderMaterial.StandardChildSlots.Transparency);
                    if (!material.SetChild(texture, slot))
                        throw new InvalidOperationException("无法将透明遮罩添加到材质的透明纹理。");
                    attached = true;
                    material.SetChildSlotOn(slot, true, context);
                    material.SetChildSlotAmount(slot, 100.0, context);
                    if (!material.ChildSlotOn(slot) || Math.Abs(material.ChildSlotAmount(slot) - 100) > .001)
                        throw new InvalidOperationException("Rhino 未启用透明件的透明遮罩。");
                }
                finally { material.EndChange(); }
                ValidateTransparentMaterial(material);
            }
            finally { if (!attached) texture.Dispose(); }
        }

        private static void ValidateTransparentMaterial(RenderMaterial material, bool requireMask = true)
        {
            if (Math.Abs(Convert.ToDouble(material.GetParameter("transparency"), CultureInfo.InvariantCulture) -
                TransparentPixels.MaterialTransparency) > .000001)
                throw new InvalidOperationException("Rhino 未保存透明件的正确材质底值，已取消应用。");
            using (var saved = material.SimulatedMaterial(RenderTexture.TextureGeneration.Allow))
            {
                if (saved == null || Math.Abs(saved.Transparency - TransparentPixels.MaterialTransparency) > .000001)
                    throw new InvalidOperationException("Rhino 的显示材质仍为整件透明，已取消应用并保留原件。");
                using (var mask = saved.GetTexture(TextureType.Transparency))
                    if (requireMask && (mask == null || !mask.Enabled || string.IsNullOrWhiteSpace(mask.FileName) || !File.Exists(mask.FileName)))
                        throw new InvalidOperationException("Rhino 未保存透明件的不透明度遮罩，已取消应用。");
            }
        }

        private static RenderTexture CreateMappedTexture(RhinoDoc doc, string image, int channel, bool linear)
        {
#pragma warning disable 612, 618
            var bitmap = new SimulatedTexture { Filename = image };
#pragma warning restore 612, 618
            var texture = RenderTexture.NewBitmapTexture(bitmap, doc);
            if (texture == null) throw new InvalidOperationException("无法建立图片纹理。");
            try
            {
                var context = RenderContent.ChangeContexts.Program;
                texture.BeginChange(context);
                try
                {
                    texture.SetProjectionMode(TextureProjectionMode.MappingChannel, context);
                    texture.SetMappingChannel(channel, context);
                    if (!texture.SetParameter("use-alpha-channel", false))
                        throw new InvalidOperationException("Rhino 无法保存透明件的颜色／遮罩设置。");
                    if (linear && !texture.SetParameter("treat-as-linear", true))
                        throw new InvalidOperationException("Rhino 无法保存透明遮罩的线性数据。");
                }
                finally { texture.EndChange(); }
                if (texture.GetProjectionMode() != TextureProjectionMode.MappingChannel || texture.GetMappingChannel() != channel)
                    throw new InvalidOperationException("Rhino 无法保存透明件的 UV 通道。");
                return texture;
            }
            catch { texture.Dispose(); throw; }
        }
    }
}

